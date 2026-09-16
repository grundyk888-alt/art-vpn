using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

internal interface IHappRouteSession
{
    bool Available { get; }
    bool OwnsTunnel { get; }
    Task SetConnectedAsync(bool connected, CancellationToken token);
}

internal sealed class HappRouteSession(string owner) : IHappRouteSession
{
    public bool Available => HappSessionCommand.Available(owner);
    public bool OwnsTunnel => ExternalTunnelDiagnosis.HappOwnsRoute();
    public Task SetConnectedAsync(bool connected, CancellationToken token) =>
        HappSessionCommand.ExecuteAsync(owner, connected, token);
}

internal sealed record HappHandoverReceipt(int Schema, string RequestId, bool WasConnected,
    bool Connect, SystemProxySnapshot Before, SystemProxySnapshot? After, bool ContainsProviderSecret);

// Native commands are part of the route transaction, not a replacement for
// HTTP acceptance. Keep a small durable intent so service interruption can
// restore the previous client; never edit HAPP settings, adapters or services.
internal sealed class HappRouteHandover(RuntimeOptions options, ISystemProxyStore store, IHappRouteSession session)
{
    private string ReceiptPath => Path.Combine(options.DataRoot, "state", "happ-handover.v1.json");
    private HappHandoverReceipt? _receipt;
    internal bool Released => _receipt is { Connect: false, WasConnected: true };
    internal bool Started => _receipt is { Connect: true };
    internal bool CanRetryStart => _receipt is { Connect: true, WasConnected: false };

    internal async Task<SystemProxySnapshot> RetryFailedStartAsync(CancellationToken token)
    {
        if (!CanRetryStart || _receipt is not { } receipt)
            throw new InvalidOperationException("HappRetryNotOwned");
        // The first native start sometimes leaves HAPP's core alive without a
        // working transport. Revert that exact owned start before one retry;
        // never reinstall HAPP, alter its config, or loop indefinitely.
        await RollbackAsync(token).ConfigureAwait(false);
        return await ChangeAsync(receipt.RequestId, true, receipt.Before, token).ConfigureAwait(false);
    }

    internal async Task<SystemProxySnapshot> ChangeAsync(string requestId, bool connect,
        SystemProxySnapshot before, CancellationToken token)
    {
        if (!session.Available) throw new InvalidOperationException("HappControlUnavailable");
        if (store.Read() != before) throw new InvalidOperationException("SystemProxyChangedBeforeHandover");
        if (File.Exists(ReceiptPath)) throw new InvalidOperationException("HappRecoveryPending");
        _receipt = new(1, requestId, session.OwnsTunnel || IsHapp(before), connect, before, null, false);
        AtomicFile.ReplaceJson(ReceiptPath, _receipt);
        // Record intent before sending the command. Cancellation after dispatch
        // still goes through independent, bounded rollback in RouteModeControl.
        await session.SetConnectedAsync(connect, token).ConfigureAwait(false);
        var after = await WaitForNativeStateAsync(connect, token).ConfigureAwait(false);
        _receipt = _receipt with { After = after };
        AtomicFile.ReplaceJson(ReceiptPath, _receipt);
        return after;
    }

    private async Task<SystemProxySnapshot> WaitForNativeStateAsync(bool connect, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        while (true)
        {
            var actual = store.Read();
            if (string.IsNullOrWhiteSpace(actual.AutoConfigUrl) &&
                (connect ? IsHapp(actual) : !session.OwnsTunnel && IsReleased(actual))) return actual;
            await Task.Delay(200, deadline.Token).ConfigureAwait(false);
        }
    }

    internal async Task RollbackAsync(CancellationToken token)
    {
        if (_receipt is not { } receipt) return;
        var actual = store.Read();
        // Do not overwrite a third-party/manual choice made during this action.
        var owned = receipt.After is { } after ? actual == after :
            actual == receipt.Before || (receipt.Connect ? IsHapp(actual) : IsReleased(actual));
        if (!owned)
        {
            // A newer operator choice ends our intent. Retaining a recovery
            // intent here would block all future selections or replay it later.
            Complete();
            throw new InvalidOperationException("HappRollbackOwnershipLost");
        }
        await session.SetConnectedAsync(receipt.WasConnected, token).ConfigureAwait(false);
        var restored = await WaitForNativeStateAsync(receipt.WasConnected, token).ConfigureAwait(false);
        if (store.Read() != restored) throw new InvalidOperationException("HappRollbackOwnershipLost");
        if (restored != receipt.Before) store.Write(receipt.Before);
        if (store.Read() != receipt.Before) throw new InvalidOperationException("HappRollbackNotVerified");
        Complete();
    }

    internal void Complete()
    {
        if (_receipt is not null) File.Delete(ReceiptPath);
        _receipt = null;
    }

    internal async Task RecoverAsync(CancellationToken token)
    {
        if (!File.Exists(ReceiptPath)) return;
        var receipt = JsonSerializer.Deserialize<HappHandoverReceipt>(File.ReadAllText(ReceiptPath), JsonSettings.Strict)
            ?? throw new InvalidDataException("HappRecoveryRejected");
        if (receipt.Schema != 1 || receipt.ContainsProviderSecret || receipt.RequestId.Length > 128)
            throw new InvalidDataException("HappRecoveryRejected");
        SystemProxyLeaseCoordinator.ValidateSnapshot(receipt.Before);
        if (receipt.After is not null) SystemProxyLeaseCoordinator.ValidateSnapshot(receipt.After);
        _receipt = receipt;
        var routePath = Path.Combine(options.DataRoot, "state", "route-mode.v1.json");
        if (File.Exists(routePath))
        {
            var route = JsonSerializer.Deserialize<RouteModeState>(File.ReadAllText(routePath), JsonSettings.Strict);
            if (route is { Phase: "Committed" } && route.RequestId == receipt.RequestId && store.Read() == route.Expected)
            { Complete(); return; }
        }
        await RollbackAsync(token).ConfigureAwait(false);
    }

    internal static bool IsHapp(SystemProxySnapshot snapshot) => snapshot.ProxyEnable == 1 &&
        ExternalProxyEndpoint.KnownProxyPort(snapshot.ProxyServer) == 10809 && string.IsNullOrWhiteSpace(snapshot.AutoConfigUrl);
    private static bool IsReleased(SystemProxySnapshot snapshot) => string.IsNullOrWhiteSpace(snapshot.AutoConfigUrl) &&
        (snapshot.ProxyEnable == 0 || SystemProxyLeaseCoordinator.IsArtVpnEndpoint(snapshot));
}

internal static class HappWarmupRetry
{
    internal static async Task<bool> ConfirmAsync(Func<int, CancellationToken, Task<bool>> probe,
        Func<CancellationToken, Task> retryOwnedStart, CancellationToken token)
    {
        if (await probe(1, token).ConfigureAwait(false)) return true;
        token.ThrowIfCancellationRequested();
        await retryOwnedStart(token).ConfigureAwait(false);
        return await probe(2, token).ConfigureAwait(false);
    }
}
