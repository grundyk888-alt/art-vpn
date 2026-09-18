using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Setup;

// A third-party client is optional, including during removal. Native return is
// best effort; after stopping ART, restore only the proxy settings we own.
internal static class UninstallRouteReturn
{
    internal static bool Needed(SystemProxyLease? lease, SystemProxySnapshot actual) =>
        lease is not null && lease.Previous.ProxyEnable == 1 &&
        string.Equals(lease.Previous.ProxyServer, "127.0.0.1:10809", StringComparison.Ordinal) &&
        (actual == lease.Applied || SystemProxyLeaseCoordinator.RestoreAfterBypassOnlyChange(lease, actual) is not null);

    internal static async Task<string> TryNativeReturnAsync(string dataRoot, string owner, CancellationToken token)
    {
        var leasePath = Path.Combine(dataRoot, "state", "system-proxy-lease.v1.json");
        var lease = SystemProxyLeaseCoordinator.TryReadLease(leasePath);
        var store = new RegistrySystemProxyStore(owner);
        if (lease is not null) SystemProxyLeaseCoordinator.ValidateLease(lease, owner);
        if (!Needed(lease, store.Read())) return "NotNeeded";
        try
        {
            await RequestNativeReturnAsync(dataRoot, store, token).ConfigureAwait(false);
            return "NativeReturnConfirmed";
        }
        catch (Exception ex) when (OptionalReturnFailure(ex, token))
        {
            // Do not make uninstall depend on a deleted/stopped HAPP, an
            // unavailable control pipe, the subscription, or Internet access.
            return "NativeReturnUnavailable";
        }
    }

    internal static bool OptionalReturnFailure(Exception ex, CancellationToken token) =>
        !token.IsCancellationRequested && ex is IOException or TimeoutException or
            OperationCanceledException or JsonException or InvalidOperationException;

    private static async Task RequestNativeReturnAsync(string dataRoot, ISystemProxyStore store, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var requestId = Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeClientStream(".", "ARTSPORT.ARTVpn.Control.v1", PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000, deadline.Token).ConfigureAwait(false);
        var request = JsonSerializer.Serialize(new { schema = 1, kind = "art-vpn-control-request", requestId,
            nonce = Guid.NewGuid().ToString("N"), action = "RestoreSystemProxy", arguments = new Dictionary<string, string>() });
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(request + "\n"), deadline.Token).ConfigureAwait(false);
        await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
        var bytes = new List<byte>(); var next = new byte[1];
        while (bytes.Count < 8192)
        {
            if (await pipe.ReadAsync(next, deadline.Token).ConfigureAwait(false) != 1) throw new IOException("UninstallReturnUnconfirmed");
            if (next[0] == (byte)'\n') break;
            bytes.Add(next[0]);
        }
        if (bytes.Count >= 8192) throw new InvalidDataException("UninstallReturnRejected");
        using var ack = JsonDocument.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
        var value = ack.RootElement;
        if (value.GetProperty("schema").GetInt32() != 1 || value.GetProperty("kind").GetString() != "art-vpn-control-response" ||
            value.GetProperty("action").GetString() != "RestoreSystemProxy" || value.GetProperty("requestId").GetString() != requestId ||
            !value.GetProperty("accepted").GetBoolean() || !value.GetProperty("queued").GetBoolean())
            throw new InvalidDataException("UninstallReturnRejected");
        var receiptPath = Path.Combine(dataRoot, "state", "results", requestId + ".json");
        while (!File.Exists(receiptPath)) await Task.Delay(200, deadline.Token).ConfigureAwait(false);
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath, deadline.Token).ConfigureAwait(false));
        var result = receipt.RootElement;
        if (result.GetProperty("schema").GetInt32() != 1 || result.GetProperty("kind").GetString() != "art-vpn-controller-result" ||
            result.GetProperty("requestId").GetString() != requestId || result.GetProperty("containsProviderSecret").GetBoolean() ||
            result.GetProperty("status").GetString() != "Completed") throw new InvalidOperationException("UninstallReturnUnconfirmed");
        if (SystemProxyLeaseCoordinator.IsArtVpnEndpoint(store.Read())) throw new InvalidOperationException("UninstallReturnUnconfirmed");
    }

    // Call only after the owned service is stopped: a delayed native-return
    // request must not race the final registry write or reclaim the proxy.
    internal static async Task<SystemProxyOperationReceipt> RestoreStoppedAsync(string leasePath, string owner,
        ISystemProxyStore store, CancellationToken token,
        Func<IPEndPoint, CancellationToken, Task<bool>>? listening = null)
    {
        var lease = SystemProxyLeaseCoordinator.TryReadLease(leasePath);
        if (lease is not null) SystemProxyLeaseCoordinator.ValidateLease(lease, owner);
        var actual = store.Read();
        var owned = lease is not null && (actual == lease.Applied ||
            SystemProxyLeaseCoordinator.RestoreAfterBypassOnlyChange(lease, actual) is not null);
        if (owned)
        {
            var previous = actual == lease!.Applied ? lease.Previous :
                SystemProxyLeaseCoordinator.RestoreAfterBypassOnlyChange(lease, actual)!;
            var endpoint = LocalEndpoint(previous);
            if (endpoint is not null && !SystemProxyLeaseCoordinator.IsArtVpnEndpoint(previous) &&
                !await (listening ?? IsListeningAsync)(endpoint, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                var direct = previous with { ProxyEnable = 0, ProxyServer = null };
                if (store.Read() != actual) throw new InvalidOperationException("SystemProxyOwnershipLost");
                store.Write(direct);
                if (store.Read() != direct) throw new InvalidOperationException("SystemProxyRestoreNotVerified");
                // Drop only our validated lease; never rewrite a newer choice.
                _ = SystemProxyLeaseCoordinator.Restore(leasePath, owner, store, dropExternalLease: true);
                return new("Completed", "UnavailablePreviousLocalProxyRemoved", true, false, false);
            }
        }
        var result = SystemProxyLeaseCoordinator.Restore(leasePath, owner, store, dropExternalLease: false,
            restoreOwnedEndpointWithChangedBypass: true);
        if (result.Status != "Completed") throw new InvalidOperationException(result.DetailCode);
        if (SystemProxyLeaseCoordinator.IsArtVpnEndpoint(store.Read()))
            throw new InvalidOperationException("SystemProxyOwnershipRequiresManualResolution");
        if (result.DetailCode == "SystemProxyExternalOwnerPreserved")
            _ = SystemProxyLeaseCoordinator.Restore(leasePath, owner, store, dropExternalLease: true);
        return result;
    }

    internal static IPEndPoint? LocalEndpoint(SystemProxySnapshot snapshot)
    {
        // Do not infer ownership or disable a corporate/PAC/multi-proxy setup.
        if (snapshot.ProxyEnable != 1 || !string.IsNullOrWhiteSpace(snapshot.AutoConfigUrl) ||
            string.IsNullOrWhiteSpace(snapshot.ProxyServer) ||
            snapshot.ProxyServer.IndexOfAny([';', '=', '/', '@', ' ', '\\']) >= 0 ||
            !IPEndPoint.TryParse(snapshot.ProxyServer, out var endpoint) ||
            !IPAddress.IsLoopback(endpoint.Address) || endpoint.Port is < 1 or > 65535) return null;
        return endpoint;
    }

    private static async Task<bool> IsListeningAsync(IPEndPoint endpoint, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var client = new TcpClient(endpoint.AddressFamily);
        try { await client.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false); return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }
}
