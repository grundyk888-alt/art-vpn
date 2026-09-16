using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Setup;

// An installer must not remove ART and leave Windows pointing at the HAPP
// listener that ART disconnected. Ask the authenticated service to return the
// native client first; on failure keep the installed, recoverable product.
internal static class UninstallRouteReturn
{
    internal static bool Needed(SystemProxyLease? lease, SystemProxySnapshot actual) =>
        lease is not null && lease.Previous.ProxyEnable == 1 &&
        string.Equals(lease.Previous.ProxyServer, "127.0.0.1:10809", StringComparison.Ordinal) &&
        (actual == lease.Applied || SystemProxyLeaseCoordinator.RestoreAfterBypassOnlyChange(lease, actual) is not null);

    internal static async Task RestoreAsync(string dataRoot, string owner, CancellationToken token)
    {
        var leasePath = Path.Combine(dataRoot, "state", "system-proxy-lease.v1.json");
        var lease = SystemProxyLeaseCoordinator.TryReadLease(leasePath);
        var store = new RegistrySystemProxyStore(owner);
        if (!Needed(lease, store.Read())) return;
        if (lease is not null) SystemProxyLeaseCoordinator.ValidateLease(lease, owner);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
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
}
