using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ArtSport.ArtVpn.Common;

internal sealed record ProviderConnectionResult(bool Success, string DetailCode);

// One protocol for Setup and the subscription wizard. Secrets stay in memory;
// the service encrypts them. The elevated installer never receives a secret on
// its command line, and an acknowledgement is never reported as a connection.
internal static class ProviderConnectionClient
{
    internal static async Task<ProviderConnectionResult> ConnectAsync(SecureString first,
        SecureString confirmation, CancellationToken token)
    {
        byte[]? a = null, b = null;
        try
        {
            a = CopyUtf8(first); b = CopyUtf8(confirmation);
            if (a.Length is < 1 or > 8192 || b.Length is < 1 or > 8192)
                return new(false, "ProviderValueRejected");
            await using var pipe = new NamedPipeClientStream(".", "ARTSPORT.ARTVpn.Provision.v1",
                PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(5000, token).ConfigureAwait(false);
            // A pipe name/ACL alone does not authenticate its server. Compare
            // its actual process to SCM before sending the subscription.
            if (!IsServicePipe(pipe.SafePipeHandle)) return new(false, "ServiceIdentityRejected");
            await pipe.WriteAsync("ARTVPNP2"u8.ToArray(), token).ConfigureAwait(false);
            foreach (var value in new[] { a, b })
            {
                await pipe.WriteAsync(BitConverter.GetBytes(value.Length), token).ConfigureAwait(false);
                await pipe.WriteAsync(value, token).ConfigureAwait(false);
            }
            await pipe.FlushAsync(token).ConfigureAwait(false);
            var line = await ReadLineAsync(pipe, token).ConfigureAwait(false);
            var acknowledgement = ParseAcknowledgement(line);
            if (acknowledgement.RequestId is null) return new(false, acknowledgement.Code);
            return await WaitAsync(acknowledgement.RequestId, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(false, "ControllerResultTimedOut"); }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        { return new(false, "ServiceUnavailable"); }
        finally
        {
            if (a is not null) CryptographicOperations.ZeroMemory(a);
            if (b is not null) CryptographicOperations.ZeroMemory(b);
        }
    }

    internal static (string? RequestId, string Code) ParseAcknowledgement(string json)
    {
        using var document = JsonDocument.Parse(json);
        var r = document.RootElement;
        if (!r.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1 ||
            !r.TryGetProperty("kind", out var kind) || kind.GetString() != "art-vpn-provider-response" ||
            !r.TryGetProperty("secretDisplayed", out var secret) || secret.ValueKind != JsonValueKind.False ||
            !r.TryGetProperty("accepted", out var accepted) || accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !r.TryGetProperty("detailCode", out var code) || code.ValueKind != JsonValueKind.String)
            return (null, "ControllerResultRejected");
        if (!accepted.GetBoolean()) return (null, code.GetString()!);
        if (code.GetString() != "ProviderConnectQueued" || !r.TryGetProperty("requestId", out var id) ||
            !Guid.TryParseExact(id.GetString(), "N", out var request)) return (null, "ControllerResultRejected");
        return (request.ToString("N"), code.GetString()!);
    }

    internal static ProviderConnectionResult ParseCompletion(string json, string requestId)
    {
        using var document = JsonDocument.Parse(json);
        var r = document.RootElement;
        if (r.GetProperty("schema").GetInt32() != 1 || r.GetProperty("kind").GetString() != "art-vpn-controller-result" ||
            r.GetProperty("requestId").GetString() != requestId || r.GetProperty("containsProviderSecret").ValueKind != JsonValueKind.False)
            return new(false, "ControllerResultRejected");
        var code = r.GetProperty("detailCode").GetString() ?? "ControllerResultRejected";
        // A successful refresh without a committed Auto route isn't enough.
        return new(r.GetProperty("status").GetString() == "Completed" && code == "ProviderConnectedAuto", code);
    }

    private static async Task<ProviderConnectionResult> WaitAsync(string requestId, CancellationToken token)
    {
        var path = Path.Combine(@"C:\ProgramData\ART VPN\state\results", requestId + ".json");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > 16384 || info.LinkTarget is not null) return new(false, "ControllerResultRejected");
                    return ParseCompletion(await File.ReadAllTextAsync(path, Encoding.UTF8, token).ConfigureAwait(false), requestId);
                }
                catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException)
                { return new(false, "ControllerResultRejected"); }
            }
            await Task.Delay(200, token).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(); var one = new byte[1];
        while (bytes.Count < 16384)
        {
            if (await stream.ReadAsync(one, token).ConfigureAwait(false) == 0) throw new EndOfStreamException();
            if (one[0] == '\n') return new UTF8Encoding(false, true).GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        throw new InvalidDataException();
    }

    private static byte[] CopyUtf8(SecureString secret)
    {
        var ptr = Marshal.SecureStringToGlobalAllocUnicode(secret);
        var chars = new char[secret.Length];
        try
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = (char)Marshal.ReadInt16(ptr, i * 2);
            return new UTF8Encoding(false, true).GetBytes(chars);
        }
        finally { Array.Clear(chars); Marshal.ZeroFreeGlobalAllocUnicode(ptr); }
    }

    private static bool IsServicePipe(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pipePid)) return false;
        var scm = OpenSCManager(null, null, 1);
        if (scm == IntPtr.Zero) return false;
        try
        {
            var service = OpenService(scm, "ARTVpnService", 4);
            if (service == IntPtr.Zero) return false;
            try
            {
                return QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _) &&
                    status.State == 4 && status.ProcessId != 0 && status.ProcessId == pipePid;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
