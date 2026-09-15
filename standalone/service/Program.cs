using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(["--first-connection-test"], StringComparer.Ordinal))
            {
                WriteJson(await FirstConnectionTests.RunAsync().ConfigureAwait(false));
                return 0;
            }
            if (args.SequenceEqual(["--runtime-identity"], StringComparer.Ordinal))
            {
                WriteJson(new { schema = 1, sha256 = RuntimeAssetVerifier.ExpectedCoreSha256, bytes = RuntimeAssetVerifier.ExpectedCoreBytes });
                return 0;
            }
            if (args.SequenceEqual(["--microsoft-auth-proxy-status"], StringComparer.Ordinal))
            {
                WriteJson(ArtSport.Vpn.Shared.MicrosoftAuthProxyCompatibility.Inspect());
                return 0;
            }
            if (args.SequenceEqual(["--repair-microsoft-auth-proxy"], StringComparer.Ordinal))
            {
                WriteJson(ArtSport.Vpn.Shared.MicrosoftAuthProxyCompatibility.Ensure());
                return 0;
            }
            // Read-only transport diagnosis, restricted to the two product endpoints.
            // Does not load a subscription, start the service, write status or change a proxy.
            if (args.Length == 2 && args[0] == "--observe-route" && args[1] is "2080" or "10809" or "22080")
            {
                WriteJson(await SelectedRouteProbe.ObserveAsync(int.Parse(args[1]), LiveProbeTransport.RunAsync,
                    CancellationToken.None).ConfigureAwait(false));
                return 0;
            }
            if (args.SequenceEqual(["--route-mode-test"], StringComparer.Ordinal))
            {
                WriteJson(await RouteModeControlTests.RunAsync().ConfigureAwait(false));
                return 0;
            }
            if (args.SequenceEqual(["--shutdown-lifetime-test"], StringComparer.Ordinal))
            {
                WriteJson(await ShutdownLifetimeTests.RunAsync().ConfigureAwait(false));
                return 0;
            }
            if (args.SequenceEqual(["--resource-budget-test"], StringComparer.Ordinal))
            {
                WriteJson(await ResourceBudgetTests.RunAsync().ConfigureAwait(false));
                return 0;
            }
            if (args.SequenceEqual(["--provider-replacement-test"], StringComparer.Ordinal))
            {
                WriteJson(await ProviderReplacementTests.RunAsync().ConfigureAwait(false));
                return 0;
            }
            if (args.SequenceEqual(["--bypass-refresh-test"], StringComparer.Ordinal))
            {
                WriteJson(await BypassRefreshTests.RunAsync().ConfigureAwait(false));
                return 0;
            }
            if (args.SequenceEqual(["--contract-test"], StringComparer.Ordinal))
            {
                WriteJson(ContractChecks.Run());
                return 0;
            }

            if (args.SequenceEqual(["--self-test"], StringComparer.Ordinal))
            {
                WriteJson(await ServiceSelfTest.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--engine-test"], StringComparer.Ordinal))
            {
                WriteJson(ContinuityPolicyTests.Run());
                return 0;
            }

            if (args.SequenceEqual(["--provision-test"], StringComparer.Ordinal))
            {
                WriteJson(await ProviderSecretSelfTest.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--provider-adapter-test"], StringComparer.Ordinal))
            {
                WriteJson(ProviderAdapterTests.Run());
                return 0;
            }

            if (args.SequenceEqual(["--controller-test"], StringComparer.Ordinal))
            {
                WriteJson(await ProtectedControllerTests.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--connection-diagnosis-test"], StringComparer.Ordinal))
            {
                WriteJson(await ConnectionDiagnosisTests.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--client-acquisition-test"], StringComparer.Ordinal))
            {
                WriteJson(await ClientAcquisitionTests.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--release-continuity-test"], StringComparer.Ordinal))
            {
                WriteJson(await ReleaseContinuityTests.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--front-test"], StringComparer.Ordinal))
            {
                WriteJson(await StableFrontTests.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--qualification-policy-test"], StringComparer.Ordinal))
            {
                WriteJson(QualificationPolicyTests.Run());
                return 0;
            }

            if (args.SequenceEqual(["--node-qualification-test"], StringComparer.Ordinal))
            {
                WriteJson(await NodeQualificationTests.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--runtime-assets-test"], StringComparer.Ordinal))
            {
                WriteJson(RuntimeAssetTests.Run());
                return 0;
            }

            if (args.SequenceEqual(["--core-runtime-contract-test"], StringComparer.Ordinal))
            {
                WriteJson(await CoreRuntimeContractTests.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--slot-runtime-test"], StringComparer.Ordinal))
            {
                WriteJson(await SlotRuntimeManagerTests.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--selector-status-test"], StringComparer.Ordinal))
            {
                WriteJson(LiveSelectorStatusTests.Run());
                return 0;
            }

            if (args.SequenceEqual(["--process-job-test"], StringComparer.Ordinal))
            {
                WriteJson(await CoreProcessJobTests.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--watchdog-policy-test"], StringComparer.Ordinal))
            {
                WriteJson(WatchdogContinuityGateTests.Run());
                return 0;
            }

            if (args.SequenceEqual(["--job-test-child"], StringComparer.Ordinal))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return 0;
            }

            if (args.SequenceEqual(["--machine-identity-test"], StringComparer.Ordinal))
            {
                WriteJson(MachineIdentityTests.Run());
                return 0;
            }

            if (args.SequenceEqual(["--system-proxy-test"], StringComparer.Ordinal))
            {
                WriteJson(SystemProxyLeaseTests.Run());
                return 0;
            }

            if (args.SequenceEqual(["--update-test"], StringComparer.Ordinal))
            {
                WriteJson(SignedUpdatePolicyTests.Run());
                return 0;
            }

            if (args.SequenceEqual(["--support-diagnostics-test"], StringComparer.Ordinal))
            {
                WriteJson(await SupportDiagnosticsTests.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.SequenceEqual(["--update-check-test"], StringComparer.Ordinal))
            {
                WriteJson(await ProductUpdateCheckerTests.RunAsync().ConfigureAwait(false));
                return 0;
            }

            if (args.Length == 0 || args.SequenceEqual(["--service"], StringComparer.Ordinal))
            {
                if (Environment.UserInteractive)
                {
                    WriteJson(new ErrorReceipt("ServiceControlManagerRequired"));
                    return 2;
                }

                using var identity = WindowsIdentity.GetCurrent();
                if (!identity.IsSystem)
                    throw new InvalidOperationException("ART VPN service must run as LocalSystem.");

                ServiceBase.Run(new ArtVpnWindowsService());
                return 0;
            }

            WriteJson(new ErrorReceipt("UnsupportedInvocation"));
            return 2;
        }
        catch (Exception ex)
        {
            WriteJson(new ErrorReceipt(ErrorCodes.From(ex)));
            return 1;
        }
    }

    private static void WriteJson<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonSettings.Output));
}

internal sealed class ArtVpnWindowsService : ServiceBase
{
    private CancellationTokenSource? _stopping;
    private Task? _runtime;

    public ArtVpnWindowsService()
    {
        ServiceName = ProductContract.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        var options = RuntimeOptions.Production();
        var install = InstallStateReader.OpenAndValidate(options.InstallStatePath);
        Directory.CreateDirectory(options.RequestRoot);
        _stopping = new CancellationTokenSource();
        var control = new ControlPipeServer(options, install, requireInteractiveSession: true);
        var provisioning = new ProviderProvisioningPipeServer(options, install, requireInteractiveSession: true);
        var front = new StableFrontHost(options, restorePersistedTarget: false);
        var controller = new ProtectedController(options, install, SubscriptionFetcher.DownloadAsync, front: front);
        controller.Initialize();
        // Local management must come up even when Windows has not acquired a
        // network yet. Restore/probing belongs to the controller, not startup.
        // Bind both callbacks to this start. StopRuntime may clear the field or
        // a later start may replace it while an old task is still unwinding.
        var stopping = _stopping.Token;
        _runtime = Task.Run(() => RuntimeLifetime.RunAsync(stopping,
            front.RunAsync, control.RunAsync, provisioning.RunAsync, controller.RunAsync));
        _ = RuntimeLifetime.ObserveFaultAsync(_runtime, stopping,
            exception => Environment.FailFast("ARTVpnRuntimeFault", exception));
        // Start the VPN first: mutex/native Windows repair can be slow at boot.
        // Once per real service start, never per observation or status request.
        _ = ArtSport.Vpn.Shared.MicrosoftAuthProxyCompatibility.StartBackground(
            () => ArtSport.Vpn.Shared.MicrosoftAuthProxyCompatibility.Ensure(),
            ex => SafeLog.Write(options.DataRoot, "MicrosoftAuthProxyCompatibilityPending:" + ex.GetType().Name));
    }

    protected override void OnStop() => StopRuntime();
    protected override void OnShutdown() => StopRuntime();

    private void StopRuntime()
    {
        if (_stopping is null) return;
        _stopping.Cancel();
        try { _runtime?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        _stopping.Dispose();
        _stopping = null;
        _runtime = null;
    }
}

internal static class ProductContract
{
    public const int Schema = 1;
    public const string ServiceName = "ARTVpnService";
    public const string PipeName = "ARTSPORT.ARTVpn.Control.v1";
    public const string ProvisionPipeName = "ARTSPORT.ARTVpn.Provision.v1";
    public const string RequestKind = "art-vpn-control-request";
    public const string ResponseKind = "art-vpn-control-response";
    public const int StableFrontPort = 22080;
    public const int StableFrontApiPort = 22090;
    public const int MaxRequestChars = 16_384;

    public static readonly string[] Actions =
    [
        "Ping", "GetStatus", "CheckNow", "CheckUpdates", "RefreshSubscription", "ConnectSubscription", "SetMode", "Rollback", "CollectReport", "SubmitReport",
        "AcquireClient", "EnableSystemProxy", "RestoreSystemProxy", "CheckBypassUpdates"
    ];

    public static readonly string[] Modes = ["Auto", "ArtVpn", "Throne", "Happ"];
    public static readonly string[] Clients = ["Throne", "Happ"];
}

internal sealed record RuntimeOptions(
    string DataRoot,
    string RuntimeRoot,
    string InstallStatePath,
    string RequestRoot,
    string SanitizedStatusPath,
    string PipeName,
    string ProvisionPipeName,
    string PrivateRoot,
    string ProviderSecretPath,
    string ProviderEntropyPath,
    string ProviderReceiptPath)
{
    public string ProcessingRoot => Path.Combine(DataRoot, "state", "processing");
    public string ResultRoot => Path.Combine(DataRoot, "state", "results");
    public string PrivateIncomingRoot => Path.Combine(PrivateRoot, "incoming");
    public string PrivateGenerationRoot => Path.Combine(PrivateRoot, "generations");
    public string StagedGenerationPath => Path.Combine(DataRoot, "state", "staged-generation.v1.json");
    public string LatestSupportReportPath => Path.Combine(DataRoot, "reports", "latest-support.v1.json");
    public string SupportDeliveryReceiptPath => Path.Combine(DataRoot, "reports", "latest-support-delivery.v1.json");
    public string SupportEndpointPath => Path.Combine(DataRoot, "state", "support-endpoint.v1.json");
    public string UpdateChannelPath => Path.Combine(RuntimeRoot, "update", "channel.v1.json");
    public string UpdateCheckReceiptPath => Path.Combine(DataRoot, "state", "update-check.v1.json");
    public string SystemProxyLeasePath => Path.Combine(DataRoot, "state", "system-proxy-lease.v1.json");

    public static RuntimeOptions Production()
    {
        const string dataRoot = @"C:\ProgramData\ART VPN";
        return new RuntimeOptions(
            dataRoot,
            @"C:\Program Files\ART VPN\runtime",
            Path.Combine(dataRoot, "state", "install.v1.json"),
            Path.Combine(dataRoot, "state", "requests"),
            Path.Combine(dataRoot, "state", "status.v1.json"),
            ProductContract.PipeName,
            ProductContract.ProvisionPipeName,
            Path.Combine(dataRoot, "private"),
            Path.Combine(dataRoot, "private", "provider.dpapi"),
            Path.Combine(dataRoot, "private", "provider.entropy"),
            Path.Combine(dataRoot, "state", "provider.v1.json"));
    }

    public static RuntimeOptions Test(string root, string pipeName)
    {
        var exactRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return new RuntimeOptions(
            exactRoot,
            Path.Combine(exactRoot, "test-runtime"),
            Path.Combine(exactRoot, "state", "install.v1.json"),
            Path.Combine(exactRoot, "state", "requests"),
            Path.Combine(exactRoot, "state", "status.v1.json"),
            pipeName,
            pipeName + ".Provision",
            Path.Combine(exactRoot, "private"),
            Path.Combine(exactRoot, "private", "provider.dpapi"),
            Path.Combine(exactRoot, "private", "provider.entropy"),
            Path.Combine(exactRoot, "state", "provider.v1.json"));
    }
}

internal sealed record InstallState(
    int Schema,
    string Kind,
    string Status,
    string ComputerName,
    string MachineSid,
    string OwnerSid,
    string InstallId,
    string Version,
    string ManifestSha256,
    string InstalledAtUtc,
    bool SystemProxyManaged,
    bool ContainsProviderSecret);

internal static partial class InstallStateReader
{
    private static readonly string[] ExpectedProperties =
    [
        "schema", "kind", "status", "computerName", "machineSid", "ownerSid", "installId", "version",
        "manifestSha256", "installedAtUtc", "systemProxyManaged", "containsProviderSecret"
    ];

    [GeneratedRegex("^S-1-5-21-(?:[0-9]+-){3}[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerSidPattern();

    [GeneratedRegex("^S-1-5-21-(?:[0-9]+-){2}[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MachineSidPattern();

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallIdPattern();

    [GeneratedRegex("^[A-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static InstallState OpenAndValidate(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("InstallStateMissing");
        var json = File.ReadAllText(path, Encoding.UTF8);
        var state = ValidateJson(json, Environment.MachineName, expectedOwnerSid: null);
        if (!string.Equals(state.MachineSid, MachineIdentity.ReadLocalSamSid(), StringComparison.Ordinal))
            throw new InvalidDataException("InstallStateMachineSidRejected");
        return state;
    }

    public static InstallState ValidateJson(string json, string expectedComputerName, string? expectedOwnerSid)
    {
        using var document = JsonDocument.Parse(json, JsonSettings.Document);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("InstallStateShapeRejected");
        if (!root.EnumerateObject().Select(p => p.Name).SequenceEqual(ExpectedProperties, StringComparer.Ordinal))
            throw new InvalidDataException("InstallStatePropertySetRejected");

        var state = JsonSerializer.Deserialize<InstallState>(json, JsonSettings.Strict)
            ?? throw new InvalidDataException("InstallStateEmpty");
        if (state.Schema != ProductContract.Schema || state.Kind != "art-vpn-consumer-install")
            throw new InvalidDataException("InstallStateContractRejected");
        if (!new[] { "Staged", "Configured", "Active", "RepairRequired" }.Contains(state.Status, StringComparer.Ordinal))
            throw new InvalidDataException("InstallStateStatusRejected");
        if (!string.Equals(state.ComputerName, expectedComputerName, StringComparison.OrdinalIgnoreCase) ||
            state.ComputerName.Length is < 1 or > 63)
            throw new InvalidDataException("InstallStateComputerRejected");
        if (!MachineSidPattern().IsMatch(state.MachineSid) || !OwnerSidPattern().IsMatch(state.OwnerSid))
            throw new InvalidDataException("InstallStateIdentityRejected");
        _ = new SecurityIdentifier(state.OwnerSid);
        if (expectedOwnerSid is not null && !string.Equals(state.OwnerSid, expectedOwnerSid, StringComparison.Ordinal))
            throw new InvalidDataException("InstallStateOwnerRejected");
        if (!InstallIdPattern().IsMatch(state.InstallId) || !VersionPattern().IsMatch(state.Version) ||
            !Sha256Pattern().IsMatch(state.ManifestSha256))
            throw new InvalidDataException("InstallStateIntegrityRejected");
        if (!DateTimeOffset.TryParseExact(state.InstalledAtUtc, "o", null,
                System.Globalization.DateTimeStyles.None, out _))
            throw new InvalidDataException("InstallStateTimeRejected");
        if (state.ContainsProviderSecret)
            throw new InvalidDataException("InstallStateSecretRejected");
        return state;
    }
}

internal sealed class ControlPipeServer
{
    private readonly RuntimeOptions _options;
    private readonly InstallState _install;
    private readonly bool _requireInteractiveSession;
    private readonly ReplayGuard _replay = new();

    public ControlPipeServer(RuntimeOptions options, InstallState install, bool requireInteractiveSession)
    {
        _options = options;
        _install = install;
        _requireInteractiveSession = requireInteractiveSession;
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
                // A stalled request must release this listener, not restart VPN.
                SafeLog.Write(_options.DataRoot, "ControlRequestTimedOut");
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
            _options.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            16_384,
            16_384,
            security);

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        // The listener accepts one client at a time. A connected peer which
        // sends no newline must not block every subsequent UI/control request.
        // Start the deadline after connection, not while waiting for a user.
        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        cancellationToken = requestDeadline.Token;
        var identity = PipeClientIdentityReader.Read(pipe, _requireInteractiveSession);
        if (!identity.IsAuthorized(_install.OwnerSid))
        {
            await WriteResponseAsync(pipe, ControlResponse.Rejected("", "", "CallerRejected"), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var line = await PipeProtocol.ReadBoundedLineAsync(pipe, cancellationToken).ConfigureAwait(false);
        ControlRequest request;
        try
        {
            request = ControlRequestParser.Parse(line);
            if (!_replay.TryAccept(request.Nonce))
                throw new InvalidDataException("ReplayRejected");
        }
        catch (Exception ex)
        {
            await WriteResponseAsync(pipe, ControlResponse.Rejected("", "", ErrorCodes.From(ex)), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ControlResponse response;
        if (request.Action is "Ping" or "GetStatus")
        {
            response = ControlResponse.AcceptedRead(request, request.Action == "Ping" ? "ServiceReady" : "StatusReady");
        }
        else
        {
            RequestQueue.Write(_options, request, identity);
            response = ControlResponse.AcceptedQueued(request);
        }

        await WriteResponseAsync(pipe, response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(Stream stream, ControlResponse response, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, JsonSettings.Output) + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        Array.Clear(bytes);
    }
}

internal sealed record ClientProcessIdentity(string Sid, int ProcessId, int SessionId, bool IsAdministrator, bool SessionAccepted)
{
    public bool IsAuthorized(string ownerSid) =>
        SessionAccepted &&
        (string.Equals(Sid, ownerSid, StringComparison.Ordinal) ||
         string.Equals(Sid, "S-1-5-18", StringComparison.Ordinal) || IsAdministrator);
}

internal static class PipeClientIdentityReader
{
    public static ClientProcessIdentity Read(NamedPipeServerStream pipe, bool requireInteractiveSession)
    {
        SecurityIdentifier? sid = null;
        var isAdministrator = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            sid = identity.User;
            isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        });
        if (sid is null) throw new UnauthorizedAccessException("CallerTokenUnavailable");

        if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid == 0)
            throw new UnauthorizedAccessException("CallerProcessUnavailable");
        using var process = Process.GetProcessById(checked((int)clientPid));
        var sessionAccepted = !requireInteractiveSession || InteractiveSession.IsPresent(process.SessionId);
        return new ClientProcessIdentity(sid.Value, process.Id, process.SessionId, isAdministrator, sessionAccepted);
    }
}

internal static class InteractiveSession
{
    public static bool IsPresent(int sessionId)
    {
        if (sessionId < 0) return false;
        try
        {
            return Process.GetProcessesByName("explorer").Any(process =>
            {
                using (process) return process.SessionId == sessionId;
            });
        }
        catch { return false; }
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint clientProcessId);
}

internal static class PipeSecurityFactory
{
    public static PipeSecurity Create(string ownerSid)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        Add(security, new SecurityIdentifier("S-1-5-18"), PipeAccessRights.FullControl);
        Add(security, new SecurityIdentifier("S-1-5-32-544"), PipeAccessRights.FullControl);
        Add(security, new SecurityIdentifier(ownerSid), PipeAccessRights.ReadWrite);
        return security;
    }

    private static void Add(PipeSecurity security, SecurityIdentifier sid, PipeAccessRights rights) =>
        security.AddAccessRule(new PipeAccessRule(sid, rights, AccessControlType.Allow));
}

internal sealed record ControlRequest(int Schema, string Kind, string RequestId, string Nonce, string Action, JsonElement Arguments);

internal static partial class ControlRequestParser
{
    private static readonly string[] ExpectedTopLevel = ["schema", "kind", "requestId", "nonce", "action", "arguments"];

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex32();

    public static ControlRequest Parse(string json)
    {
        using var document = JsonDocument.Parse(json, JsonSettings.Document);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.EnumerateObject().Select(p => p.Name).SequenceEqual(ExpectedTopLevel, StringComparer.Ordinal))
            throw new InvalidDataException("RequestPropertySetRejected");

        var request = JsonSerializer.Deserialize<ControlRequest>(json, JsonSettings.Strict)
            ?? throw new InvalidDataException("RequestEmpty");
        if (request.Schema != ProductContract.Schema || request.Kind != ProductContract.RequestKind)
            throw new InvalidDataException("RequestContractRejected");
        if (!LowerHex32().IsMatch(request.RequestId) || !LowerHex32().IsMatch(request.Nonce))
            throw new InvalidDataException("RequestIdentityRejected");
        if (!ProductContract.Actions.Contains(request.Action, StringComparer.Ordinal))
            throw new InvalidDataException("RequestActionRejected");
        if (request.Arguments.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("RequestArgumentsRejected");

        var names = request.Arguments.EnumerateObject().Select(p => p.Name).ToArray();
        if (request.Action == "SetMode")
        {
            if (!names.SequenceEqual(["mode"], StringComparer.Ordinal))
                throw new InvalidDataException("RequestArgumentsRejected");
            var mode = request.Arguments.GetProperty("mode");
            if (mode.ValueKind != JsonValueKind.String ||
                !ProductContract.Modes.Contains(mode.GetString(), StringComparer.Ordinal))
                throw new InvalidDataException("RequestModeRejected");
        }
        else if (request.Action == "AcquireClient")
        {
            if (!names.SequenceEqual(["client"], StringComparer.Ordinal))
                throw new InvalidDataException("RequestArgumentsRejected");
            var client = request.Arguments.GetProperty("client");
            if (client.ValueKind != JsonValueKind.String ||
                !ProductContract.Clients.Contains(client.GetString(), StringComparer.Ordinal))
                throw new InvalidDataException("RequestClientRejected");
        }
        else if (names.Length != 0)
        {
            throw new InvalidDataException("RequestArgumentsRejected");
        }

        return request;
    }
}

internal sealed class ReplayGuard
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public bool TryAccept(string nonce)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _seen)
            if (now - item.Value > TimeSpan.FromMinutes(10)) _seen.TryRemove(item.Key, out _);
        return _seen.TryAdd(nonce, now);
    }
}

internal static class RequestQueue
{
    public static void Write(RuntimeOptions options, ControlRequest request, ClientProcessIdentity caller)
    {
        Directory.CreateDirectory(options.RequestRoot);
        var fullRoot = Path.GetFullPath(options.RequestRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var finalPath = Path.GetFullPath(Path.Combine(options.RequestRoot, request.RequestId + ".json"));
        if (!finalPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("RequestPathRejected");

        var queued = new QueuedRequest(
            ProductContract.Schema,
            "art-vpn-controller-request",
            request.RequestId,
            request.Action,
            request.Action == "SetMode" ? request.Arguments.GetProperty("mode").GetString() : null,
            request.Action == "AcquireClient" ? request.Arguments.GetProperty("client").GetString() : null,
            caller.Sid,
            caller.SessionId,
            DateTimeOffset.UtcNow.ToString("o"));
        AtomicFile.WriteJson(finalPath, queued);
    }

    public static string WriteProvisionedRefresh(RuntimeOptions options, ClientProcessIdentity caller, bool connect = false)
    {
        Directory.CreateDirectory(options.RequestRoot);
        var requestId = Guid.NewGuid().ToString("N");
        var queued = new QueuedRequest(
            ProductContract.Schema,
            "art-vpn-controller-request",
            requestId,
            connect ? "ConnectSubscription" : "RefreshSubscription",
            null,
            null,
            caller.Sid,
            caller.SessionId,
            DateTimeOffset.UtcNow.ToString("o"));
        AtomicFile.WriteJson(Path.Combine(options.RequestRoot, requestId + ".json"), queued);
        return requestId;
    }
}

internal sealed record QueuedRequest(
    int Schema,
    string Kind,
    string RequestId,
    string Action,
    string? Mode,
    string? Client,
    string CallerSid,
    int CallerSessionId,
    string QueuedAtUtc);

internal sealed record ControlResponse(
    int Schema,
    string Kind,
    string Status,
    string RequestId,
    string Action,
    bool Accepted,
    bool Queued,
    string DetailCode,
    string AtUtc)
{
    public static ControlResponse AcceptedRead(ControlRequest request, string code) =>
        new(ProductContract.Schema, ProductContract.ResponseKind, "Accepted", request.RequestId, request.Action,
            true, false, code, DateTimeOffset.UtcNow.ToString("o"));

    public static ControlResponse AcceptedQueued(ControlRequest request) =>
        new(ProductContract.Schema, ProductContract.ResponseKind, "Accepted", request.RequestId, request.Action,
            true, true, "QueuedForProtectedController", DateTimeOffset.UtcNow.ToString("o"));

    public static ControlResponse Rejected(string requestId, string action, string code) =>
        new(ProductContract.Schema, ProductContract.ResponseKind, "Rejected", requestId, action,
            false, false, code, DateTimeOffset.UtcNow.ToString("o"));
}

internal static class PipeProtocol
{
    public static async Task<string> ReadBoundedLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(1024);
        var buffer = new byte[1];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("RequestEndedBeforeNewline");
            if (buffer[0] == (byte)'\n') break;
            if (buffer[0] == (byte)'\r' || buffer[0] == 0)
                throw new InvalidDataException("RequestEncodingRejected");
            bytes.Add(buffer[0]);
            if (bytes.Count > ProductContract.MaxRequestChars)
                throw new InvalidDataException("RequestTooLarge");
        }

        try { return new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { throw new InvalidDataException("RequestEncodingRejected"); }
        finally { buffer[0] = 0; }
    }
}

internal static class AtomicFile
{
    public static void WriteJson<T>(string finalPath, T value)
    {
        var directory = Path.GetDirectoryName(finalPath) ?? throw new InvalidOperationException("DestinationRejected");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(finalPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonSettings.Output);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, finalPath, overwrite: false);
        }
        finally
        {
            Array.Clear(bytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void WriteBytes(string finalPath, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(finalPath) ?? throw new InvalidOperationException("DestinationRejected");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(finalPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, finalPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void ReplaceJson<T>(string finalPath, T value)
    {
        var directory = Path.GetDirectoryName(finalPath) ?? throw new InvalidOperationException("DestinationRejected");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(finalPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonSettings.Output);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, finalPath, overwrite: true);
        }
        finally
        {
            Array.Clear(bytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal static class SafeLog
{
    internal const long MaxFileBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Write(string root, string code)
    {
        try
        {
            // Logs contain bounded diagnostic codes, never arbitrary provider
            // responses, subscription URLs or exception messages.
            if (string.IsNullOrEmpty(code) || code.Length > 128 ||
                code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or ':' or '_' or '-')))
                code = "DiagnosticCodeRedacted";
            var logRoot = Path.Combine(root, "logs");
            var line = JsonSerializer.Serialize(new
            {
                schema = 1,
                kind = "art-vpn-service-event",
                code,
                atUtc = DateTimeOffset.UtcNow.ToString("o")
            }, JsonSettings.Output) + Environment.NewLine;
            lock (Gate)
            {
                Directory.CreateDirectory(logRoot);
                var path = Path.Combine(logRoot, "service-events.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaxFileBytes)
                {
                    // Only these exact owned files are rotated; never enumerate
                    // or delete unrelated logs. At most three files, 6 MiB total.
                    if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2", overwrite: true);
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch { }
    }
}

internal static class ContractChecks
{
    public static ContractReceipt Run()
    {
        var good = NewRequestJson("CheckNow");
        var parsed = ControlRequestParser.Parse(good);
        if (parsed.Action != "CheckNow") throw new InvalidOperationException("ValidRequestRejected");

        ExpectRejected(good.Replace("\"arguments\":{}", "\"arguments\":{},\"command\":\"whoami\"", StringComparison.Ordinal));
        ExpectRejected(NewRequestJson("SetMode", "{\"mode\":\"Unknown\"}"));
        ExpectRejected(NewRequestJson("AcquireClient", "{\"client\":\"Unknown\"}"));
        ExpectRejected(NewRequestJson("RunScript"));

        var state = new
        {
            schema = 1,
            kind = "art-vpn-consumer-install",
            status = "Staged",
            computerName = "TEST-PC",
            machineSid = "S-1-5-21-111-222-333",
            ownerSid = "S-1-5-21-111-222-333-1001",
            installId = "0123456789abcdef0123456789abcdef",
            version = "0.1.0-beta.1",
            manifestSha256 = new string('A', 64),
            installedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            systemProxyManaged = false,
            containsProviderSecret = false
        };
        var validated = InstallStateReader.ValidateJson(JsonSerializer.Serialize(state, JsonSettings.Output),
            "TEST-PC", "S-1-5-21-111-222-333-1001");
        if (validated.ContainsProviderSecret) throw new InvalidOperationException("InstallStateSecretAccepted");

        return new ContractReceipt(
            "Passed",
            ProductContract.PipeName,
            ProductContract.StableFrontPort,
            ProductContract.StableFrontApiPort,
            ProductContract.Actions,
            true,
            false,
            false);
    }

    internal static string NewRequestJson(string action, string arguments = "{}") =>
        "{\"schema\":1,\"kind\":\"art-vpn-control-request\",\"requestId\":\"" +
        Guid.NewGuid().ToString("N") + "\",\"nonce\":\"" + Guid.NewGuid().ToString("N") +
        "\",\"action\":\"" + action + "\",\"arguments\":" + arguments + "}";

    private static void ExpectRejected(string json)
    {
        try
        {
            _ = ControlRequestParser.Parse(json);
            throw new InvalidOperationException("UnsafeRequestAccepted");
        }
        catch (InvalidDataException) { }
    }
}

internal static class ServiceSelfTest
{
    public static async Task<SelfTestReceipt> RunAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("SelfTestIdentityMissing");
        if (!Regex.IsMatch(sid, "^S-1-5-21-(?:[0-9]+-){3}[0-9]+$", RegexOptions.CultureInvariant))
            return new SelfTestReceipt("Passed", true, true, false, "NonConsumerSidSkipped");

        var root = Path.Combine(Path.GetTempPath(), "art-vpn-service-selftest-" + Guid.NewGuid().ToString("N"));
        var pipeName = "ARTSPORT.ARTVpn.SelfTest." + Guid.NewGuid().ToString("N");
        var options = RuntimeOptions.Test(root, pipeName);
        Directory.CreateDirectory(Path.GetDirectoryName(options.InstallStatePath)!);
        try
        {
            var state = new
            {
                schema = 1,
                kind = "art-vpn-consumer-install",
                status = "Configured",
                computerName = Environment.MachineName,
                machineSid = MachineIdentity.ReadLocalSamSid(),
                ownerSid = sid,
                installId = Guid.NewGuid().ToString("N"),
                version = "0.1.0-beta.1",
                manifestSha256 = new string('B', 64),
                installedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                systemProxyManaged = false,
                containsProviderSecret = false
            };
            AtomicFile.WriteJson(options.InstallStatePath, state);
            var install = InstallStateReader.OpenAndValidate(options.InstallStatePath);
            var server = new ControlPipeServer(options, install, requireInteractiveSession: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var stalledClientsReleased = 0;
            // Isolated, authenticated clients that stay connected without a
            // complete line must not monopolize the single control listener.
            foreach (var prefix in new[] { "", "{\"schema\":" })
            {
                var stalled = server.ServeOneAsync(timeout.Token);
                await using var silent = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                await silent.ConnectAsync(5_000, timeout.Token).ConfigureAwait(false);
                if (prefix.Length != 0)
                    await silent.WriteAsync(Encoding.UTF8.GetBytes(prefix), timeout.Token).ConfigureAwait(false);
                try
                {
                    await stalled.WaitAsync(TimeSpan.FromSeconds(7)).ConfigureAwait(false);
                    throw new InvalidOperationException("SilentClientWasNotTimedOut");
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { stalledClientsReleased++; }
                catch (System.TimeoutException) { throw new InvalidDataException("ControlClientDeadlineMissing"); }
            }
            // The normal request below proves the same server accepts work again.
            var serve = server.ServeOneAsync(timeout.Token);

            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await client.ConnectAsync(5_000, timeout.Token).ConfigureAwait(false);
            var requestId = Guid.NewGuid().ToString("N");
            var request = "{\"schema\":1,\"kind\":\"art-vpn-control-request\",\"requestId\":\"" + requestId +
                          "\",\"nonce\":\"" + Guid.NewGuid().ToString("N") +
                          "\",\"action\":\"CheckNow\",\"arguments\":{}}\n";
            var bytes = Encoding.UTF8.GetBytes(request);
            await client.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
            Array.Clear(bytes);
            var responseLine = await PipeProtocol.ReadBoundedLineAsync(client, timeout.Token).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<ControlResponse>(responseLine, JsonSettings.Strict)
                ?? throw new InvalidDataException("SelfTestResponseEmpty");
            await serve.ConfigureAwait(false);

            var queuedPath = Path.Combine(options.RequestRoot, requestId + ".json");
            if (!response.Accepted || !response.Queued || !File.Exists(queuedPath))
                throw new InvalidOperationException("SelfTestQueueRejected");
            var queuedText = File.ReadAllText(queuedPath, Encoding.UTF8);
            if (queuedText.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
                queuedText.Contains("subscription", StringComparison.OrdinalIgnoreCase) ||
                queuedText.Contains("provider", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SelfTestSecretLeak");

            return new SelfTestReceipt("Passed", true, true, false, "AuthenticatedTypedRequestQueued", stalledClientsReleased);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(Path.GetFileName(resolved), "^art-vpn-service-selftest-[a-f0-9]{32}$", RegexOptions.CultureInvariant) &&
                Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }
}

internal static class JsonSettings
{
    public static readonly JsonSerializerOptions Output = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static readonly JsonDocumentOptions Document = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };
}

internal static class ErrorCodes
{
    public static string From(Exception exception)
    {
        var text = exception.Message;
        if (Regex.IsMatch(text, "^[A-Za-z][A-Za-z0-9]{2,63}$", RegexOptions.CultureInvariant)) return text;
        return exception switch
        {
            UnauthorizedAccessException => "AccessRejected",
            JsonException => "JsonRejected",
            InvalidDataException => "ContractRejected",
            IOException => "IoFailure",
            _ => "InternalFailure"
        };
    }
}

internal sealed record ErrorReceipt(string ErrorCode)
{
    public int Schema { get; init; } = ProductContract.Schema;
    public string Kind { get; init; } = "art-vpn-service-error";
    public string Status { get; init; } = "Failed";
    public bool SecretDisplayed { get; init; } = false;
}

internal sealed record ContractReceipt(
    string Status,
    string PipeName,
    int StableFrontPort,
    int StableFrontApiPort,
    string[] Actions,
    bool CallerBound,
    bool ArbitraryCommandAccepted,
    bool SecretDisplayed);

internal sealed record SelfTestReceipt(
    string Status,
    bool PipeAuthenticated,
    bool TypedRequestQueued,
    bool SecretDisplayed,
    string DetailCode,
    int StalledClientsReleased = 0);
