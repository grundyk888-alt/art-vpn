using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ArtSport.ArtVpn.Service;

internal sealed class ProviderProvisioningPipeServer
{
    private readonly RuntimeOptions _options;
    private readonly InstallState _install;
    private readonly bool _requireInteractiveSession;
    private readonly bool _enforcePrivateAcl;

    public ProviderProvisioningPipeServer(
        RuntimeOptions options,
        InstallState install,
        bool requireInteractiveSession,
        bool enforcePrivateAcl = true)
    {
        _options = options;
        _install = install;
        _requireInteractiveSession = requireInteractiveSession;
        _enforcePrivateAcl = enforcePrivateAcl;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ServeOneAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                SafeLog.Write(_options.DataRoot, "ProvisionRequestTimedOut");
            }
            catch (Exception ex)
            {
                SafeLog.Write(_options.DataRoot, ErrorCodes.From(ex));
                try { await Task.Delay(750, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async Task ServeOneAsync(CancellationToken cancellationToken)
    {
        var security = PipeSecurityFactory.Create(_install.OwnerSid);
        await using var pipe = NamedPipeServerStreamAcl.Create(
            _options.ProvisionPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            8_192,
            8_192,
            security);

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Match the control pipe: a stalled setup dialog must release its
        // connection without restarting the VPN or replacing a saved provider.
        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        cancellationToken = requestDeadline.Token;
        var caller = PipeClientIdentityReader.Read(pipe, _requireInteractiveSession);
        if (!caller.IsAuthorized(_install.OwnerSid))
        {
            await ProvisioningProtocol.WriteResponseAsync(pipe,
                ProviderProvisionResponse.Rejected("CallerRejected"), cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[]? first = null;
        byte[]? second = null;
        try
        {
            bool connect;
            (first, second, connect) = await ProvisioningProtocol.ReadRequestAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(first, second))
                throw new InvalidDataException("ProviderConfirmationDiffers");
            ProviderSecretStore.StoreOrReplace(_options, first, _enforcePrivateAcl);
            var requestId = RequestQueue.WriteProvisionedRefresh(_options, caller, connect);
            await ProvisioningProtocol.WriteResponseAsync(pipe,
                ProviderProvisionResponse.Success(connect ? requestId : null), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await ProvisioningProtocol.WriteResponseAsync(pipe,
                ProviderProvisionResponse.Rejected(ErrorCodes.From(ex)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (first is not null) CryptographicOperations.ZeroMemory(first);
            if (second is not null) CryptographicOperations.ZeroMemory(second);
        }
    }
}

internal static class ProviderSecretStore
{
    private static readonly object Gate = new();

    // Replacing a subscription never changes the active generation. Entropy stays
    // fixed, so the only commit is one atomic encrypted-file replacement.
    public static void StoreOrReplace(RuntimeOptions options, byte[] providerUtf8, bool enforcePrivateAcl)
    {
        lock (Gate)
        {
            ValidateProviderUtf8(providerUtf8);
            if (!File.Exists(options.ProviderSecretPath) && !File.Exists(options.ProviderEntropyPath))
            {
                Store(options, providerUtf8, enforcePrivateAcl);
                return;
            }
            if (!File.Exists(options.ProviderSecretPath) || !File.Exists(options.ProviderEntropyPath))
                throw new InvalidDataException("ProviderPartialStateRequiresRecovery");
            if (enforcePrivateAcl) PrivateDirectorySecurity.Apply(options.PrivateRoot);
            var previous = Open(options);
            var entropy = File.ReadAllBytes(options.ProviderEntropyPath);
            byte[]? cipher = null;
            byte[]? reopened = null;
            var staged = Path.Combine(options.PrivateRoot, ".provider-replacement-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                if (CryptographicOperations.FixedTimeEquals(previous, providerUtf8)) return;
                cipher = ProtectedData.Protect(providerUtf8, entropy, DataProtectionScope.LocalMachine);
                AtomicFile.WriteBytes(staged, cipher);
                reopened = ProtectedData.Unprotect(File.ReadAllBytes(staged), entropy, DataProtectionScope.LocalMachine);
                if (!CryptographicOperations.FixedTimeEquals(reopened, providerUtf8))
                    throw new CryptographicException("ProviderReplacementReopenRejected");
                File.Replace(staged, options.ProviderSecretPath, options.ProviderSecretPath + ".previous", ignoreMetadataErrors: false);
                try
                {
                    AtomicFile.ReplaceJson(options.ProviderReceiptPath, new ProviderReceipt(1,
                        "art-vpn-provider-receipt", "Configured", "generic-https", "LocalMachine",
                        Convert.ToHexString(SHA256.HashData(cipher)), DateTimeOffset.UtcNow.ToString("o"), false));
                }
                catch (IOException) { SafeLog.Write(options.DataRoot,"ProviderReceiptRefreshDeferred"); }
                catch (UnauthorizedAccessException) { SafeLog.Write(options.DataRoot,"ProviderReceiptRefreshDeferred"); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(previous);
                CryptographicOperations.ZeroMemory(entropy);
                if (cipher is not null) CryptographicOperations.ZeroMemory(cipher);
                if (reopened is not null) CryptographicOperations.ZeroMemory(reopened);
                try { if (File.Exists(staged)) File.Delete(staged); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static void Store(RuntimeOptions options, byte[] providerUtf8, bool enforcePrivateAcl)
    {
        ValidateProviderUtf8(providerUtf8);
        if (File.Exists(options.ProviderSecretPath) || File.Exists(options.ProviderEntropyPath))
            throw new InvalidOperationException("ProviderAlreadyConfigured");

        Directory.CreateDirectory(options.PrivateRoot);
        if (enforcePrivateAcl) PrivateDirectorySecurity.Apply(options.PrivateRoot);

        var entropy = RandomNumberGenerator.GetBytes(32);
        byte[]? cipher = null;
        byte[]? reopened = null;
        var entropyWritten = false;
        var cipherWritten = false;
        try
        {
            cipher = ProtectedData.Protect(providerUtf8, entropy, DataProtectionScope.LocalMachine);
            AtomicFile.WriteBytes(options.ProviderEntropyPath, entropy);
            entropyWritten = true;
            AtomicFile.WriteBytes(options.ProviderSecretPath, cipher);
            cipherWritten = true;
            reopened = ProtectedData.Unprotect(
                File.ReadAllBytes(options.ProviderSecretPath),
                File.ReadAllBytes(options.ProviderEntropyPath),
                DataProtectionScope.LocalMachine);
            if (!CryptographicOperations.FixedTimeEquals(providerUtf8, reopened))
                throw new CryptographicException("ProviderReopenRejected");

            var receipt = new ProviderReceipt(
                1,
                "art-vpn-provider-receipt",
                "Configured",
                "generic-https",
                "LocalMachine",
                Convert.ToHexString(SHA256.HashData(cipher)),
                DateTimeOffset.UtcNow.ToString("o"),
                false);
            AtomicFile.WriteJson(options.ProviderReceiptPath, receipt);
        }
        catch
        {
            if (cipherWritten && File.Exists(options.ProviderSecretPath)) File.Delete(options.ProviderSecretPath);
            if (entropyWritten && File.Exists(options.ProviderEntropyPath)) File.Delete(options.ProviderEntropyPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
            if (cipher is not null) CryptographicOperations.ZeroMemory(cipher);
            if (reopened is not null) CryptographicOperations.ZeroMemory(reopened);
        }
    }

    public static byte[] Open(RuntimeOptions options)
    {
        lock (Gate) return OpenLocked(options);
    }

    private static byte[] OpenLocked(RuntimeOptions options)
    {
        var cipher = File.ReadAllBytes(options.ProviderSecretPath);
        var entropy = File.ReadAllBytes(options.ProviderEntropyPath);
        try
        {
            var opened = ProtectedData.Unprotect(cipher, entropy, DataProtectionScope.LocalMachine);
            ValidateProviderUtf8(opened);
            return opened;
        }
        catch
        {
            throw new CryptographicException("ProviderOpenRejected");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public static void ValidateProviderUtf8(byte[] value)
    {
        if (value.Length is < 12 or > 4096 || value.Contains((byte)0) || value.Contains((byte)'\r') || value.Contains((byte)'\n'))
            throw new InvalidDataException("ProviderValueRejected");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(value); }
        catch (DecoderFallbackException) { throw new InvalidDataException("ProviderValueRejected"); }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(uri.DnsSafeHost) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (!uri.IsDefaultPort && uri.Port != 443))
            throw new InvalidDataException("ProviderValueRejected");
    }
}

internal static class PrivateDirectorySecurity
{
    public static void Apply(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier("S-1-5-18"),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier("S-1-5-32-544"),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}

internal static class ProvisioningProtocol
{
    private static readonly byte[] Magic = "ARTVPNP1"u8.ToArray();
    private static readonly byte[] ConnectMagic = "ARTVPNP2"u8.ToArray();

    public static async Task<(byte[] First, byte[] Second, bool Connect)> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var magic = new byte[Magic.Length];
        await ReadExactlyAsync(stream, magic, cancellationToken).ConfigureAwait(false);
        var connect = CryptographicOperations.FixedTimeEquals(magic, ConnectMagic);
        if (!connect && !CryptographicOperations.FixedTimeEquals(magic, Magic))
            throw new InvalidDataException("ProvisionProtocolRejected");
        CryptographicOperations.ZeroMemory(magic);

        var first = await ReadValueAsync(stream, cancellationToken).ConfigureAwait(false);
        try
        {
            var second = await ReadValueAsync(stream, cancellationToken).ConfigureAwait(false);
            return (first, second, connect);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(first);
            throw;
        }
    }

    public static async Task WriteRequestAsync(Stream stream, byte[] first, byte[] second, CancellationToken cancellationToken,
        bool connect = false)
    {
        await stream.WriteAsync(connect ? ConnectMagic : Magic, cancellationToken).ConfigureAwait(false);
        await WriteValueAsync(stream, first, cancellationToken).ConfigureAwait(false);
        await WriteValueAsync(stream, second, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteResponseAsync(Stream stream, ProviderProvisionResponse response, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, JsonSettings.Output) + "\n");
        try
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static async Task<byte[]> ReadValueAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await ReadExactlyAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes, 0);
        CryptographicOperations.ZeroMemory(lengthBytes);
        if (length is < 12 or > 4096) throw new InvalidDataException("ProviderValueRejected");
        var value = new byte[length];
        await ReadExactlyAsync(stream, value, cancellationToken).ConfigureAwait(false);
        return value;
    }

    private static async Task WriteValueAsync(Stream stream, byte[] value, CancellationToken cancellationToken)
    {
        if (value.Length is < 12 or > 4096) throw new InvalidDataException("ProviderValueRejected");
        var length = BitConverter.GetBytes(value.Length);
        try
        {
            await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(length); }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("ProvisionRequestEnded");
            offset += count;
        }
    }
}

internal sealed record ProviderReceipt(
    int Schema,
    string Kind,
    string Status,
    string Adapter,
    string DpapiScope,
    string CipherSha256,
    string ConfiguredAtUtc,
    bool ContainsProviderSecret);

internal sealed record ProviderProvisionResponse(
    int Schema,
    string Kind,
    string Status,
    bool Accepted,
    string DetailCode,
    bool SecretDisplayed,
    string AtUtc,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? RequestId = null)
{
    public static ProviderProvisionResponse Success(string? requestId = null) =>
        new(1, "art-vpn-provider-response", "Configured", true,
            requestId is null ? "ProviderStoredAndReopened" : "ProviderConnectQueued", false,
            DateTimeOffset.UtcNow.ToString("o"), requestId);

    public static ProviderProvisionResponse Rejected(string code) =>
        new(1, "art-vpn-provider-response", "Rejected", false, code, false,
            DateTimeOffset.UtcNow.ToString("o"));
}

internal static class ProviderSecretSelfTest
{
    public static async Task<ProviderSecretTestReceipt> RunAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("SelfTestIdentityMissing");
        if (!System.Text.RegularExpressions.Regex.IsMatch(sid, "^S-1-5-21-(?:[0-9]+-){3}[0-9]+$"))
            return new ProviderSecretTestReceipt("Passed", true, "LocalMachine", false, "NonConsumerSidSkipped");

        var root = Path.Combine(Path.GetTempPath(), "art-vpn-provision-selftest-" + Guid.NewGuid().ToString("N"));
        var pipeName = "ARTSPORT.ARTVpn.ProvisionTest." + Guid.NewGuid().ToString("N");
        var options = RuntimeOptions.Test(root, pipeName);
        Directory.CreateDirectory(Path.GetDirectoryName(options.InstallStatePath)!);
        const string testValue = "https://example.invalid/provider-test-value";
        var first = Encoding.UTF8.GetBytes(testValue);
        var second = Encoding.UTF8.GetBytes(testValue);
        byte[]? opened = null;
        try
        {
            var state = new
            {
                schema = 1,
                kind = "art-vpn-consumer-install",
                status = "Staged",
                computerName = Environment.MachineName,
                machineSid = MachineIdentity.ReadLocalSamSid(),
                ownerSid = sid,
                installId = Guid.NewGuid().ToString("N"),
                version = "0.1.0-beta.1",
                manifestSha256 = new string('D', 64),
                installedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                systemProxyManaged = false,
                containsProviderSecret = false
            };
            AtomicFile.WriteJson(options.InstallStatePath, state);
            var install = InstallStateReader.OpenAndValidate(options.InstallStatePath);
            var server = new ProviderProvisioningPipeServer(options, install, requireInteractiveSession: false,
                enforcePrivateAcl: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var stalledClientsReleased = 0;
            foreach (var prefix in new[] { "", "ARTV" })
            {
                var stalled = server.ServeOneAsync(timeout.Token);
                await using var silent = new NamedPipeClientStream(".", options.ProvisionPipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                await silent.ConnectAsync(5_000, timeout.Token).ConfigureAwait(false);
                if (prefix.Length != 0)
                    await silent.WriteAsync(Encoding.UTF8.GetBytes(prefix), timeout.Token).ConfigureAwait(false);
                try
                {
                    await stalled.WaitAsync(TimeSpan.FromSeconds(7)).ConfigureAwait(false);
                    throw new InvalidOperationException("SilentProvisionClientWasNotTimedOut");
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { stalledClientsReleased++; }
                catch (TimeoutException) { throw new InvalidDataException("ProvisionClientDeadlineMissing"); }
            }
            var serve = server.ServeOneAsync(timeout.Token);
            await using var client = new NamedPipeClientStream(".", options.ProvisionPipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await client.ConnectAsync(5_000, timeout.Token).ConfigureAwait(false);
            await ProvisioningProtocol.WriteRequestAsync(client, first, second, timeout.Token).ConfigureAwait(false);
            var responseLine = await PipeProtocol.ReadBoundedLineAsync(client, timeout.Token).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<ProviderProvisionResponse>(responseLine, JsonSettings.Strict)
                ?? throw new InvalidDataException("ProvisionResponseEmpty");
            await serve.ConfigureAwait(false);
            if (!response.Accepted || response.SecretDisplayed) throw new InvalidOperationException("ProvisionResponseRejected");

            opened = ProviderSecretStore.Open(options);
            if (!CryptographicOperations.FixedTimeEquals(first, opened)) throw new InvalidOperationException("ProviderReopenRejected");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var payload = File.ReadAllBytes(file);
                try
                {
                    if (Encoding.UTF8.GetString(payload).Contains(testValue, StringComparison.Ordinal))
                        throw new InvalidOperationException("ProviderPlaintextPersisted");
                }
                finally { CryptographicOperations.ZeroMemory(payload); }
            }

            return new ProviderSecretTestReceipt("Passed", true, "LocalMachine", false, "ProtectedAndReopened", stalledClientsReleased);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
            if (opened is not null) CryptographicOperations.ZeroMemory(opened);
            var resolved = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(resolved),
                    "^art-vpn-provision-selftest-[a-f0-9]{32}$") && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }
}

internal sealed record ProviderSecretTestReceipt(
    string Status,
    bool ProtectedAtRest,
    string Scope,
    bool SecretDisplayed,
    string DetailCode,
    int StalledClientsReleased = 0);
