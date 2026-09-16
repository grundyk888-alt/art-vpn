using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

internal static class HappHandoverTests
{
    internal static async Task RunAsync(Func<string, RuntimeOptions> options, Action<bool, string> check)
    {
        const string owner = "S-1-5-21-111-222-333-1001";
        var original = new SystemProxySnapshot(1, "127.0.0.1:10809", "local.test;<local>", null);
        var store = new MemorySystemProxyStore(original);
        var session = new FakeSession(store) { Connected = true };
        var runtime = options("native-happ-roundtrip");
        Task<bool> Ready(CancellationToken _) => Task.FromResult(true);
        Task<bool> Probe(int port, CancellationToken _) => Task.FromResult(port == 22080 ? !session.Connected : session.Connected);
        RouteModeControl Create() => new(runtime, owner, store, Probe,
            externalTunnel: () => session.OwnsTunnel ? "ExternalTunnelOwnsRoute" : null, happ: session);
        var routes = Create();
        check(routes.Mode == "Standby" && !routes.AllowsBackgroundMutation,
            "initial-standby-does-not-qualify-art-through-happ-tunnel");
        var auto = await routes.SelectAsync("takeover", "Auto", Ready, default, true);
        check(auto.Status == "Completed" && !session.Connected && routes.ExternalReservePort == 10809 &&
            store.Current.ProxyServer == "127.0.0.1:22080", "native-first-auto-releases-happ-remembers-reserve");
        var happ = await routes.SelectAsync("return-happ", "Happ", Ready, default);
        check(happ.Status == "Completed" && session.Connected && !routes.AllowsBackgroundMutation &&
            routes.Mode == "Happ", "manual-happ-connects-native-tunnel-not-only-proxy");
        routes = Create();
        check(routes.Mode == "Happ" && !routes.ObserveExternalChoice(), "manual-happ-survives-controller-reload");
        auto = await routes.SelectAsync("back-auto", "Auto", Ready, default);
        check(auto.Status == "Completed" && !session.Connected && routes.AllowsBackgroundMutation,
            "auto-after-manual-happ-returns-art");
        var fallback = await routes.SelectAsync("cold-reserve", "Happ", Ready, default, automaticFallback: true);
        check(fallback.Status == "Completed" && session.Connected && routes.Mode == "Auto" &&
            routes.SelectedExternal == ExternalProxyEndpoint.Happ && !routes.AllowsBackgroundMutation,
            "auto-starts-cold-happ-keeps-auto-no-nested-refinement");
        auto = await routes.SelectAsync("art-only", "ArtVpn", Ready, default);
        var noFallback = await routes.SelectAsync("forbidden-fallback", "Happ", Ready, default, automaticFallback: true);
        check(auto.Status == "Completed" && noFallback.Status == "Rejected" && !session.Connected,
            "art-only-does-not-start-external-reserve");
        var restore = await routes.RestorePreviousAsync(default);
        check(restore.Status is "Completed" or "Restored" && session.Connected && store.Current == original,
            "restore-previous-restores-happ-native-and-original-proxy");

        foreach (var reason in new[] { "failed", "cancelled", "manual-race" })
        {
            store = new(original); session = new(store) { Connected = true };
            runtime = options("native-happ-" + reason); routes = Create();
            var foreign = original with { ProxyServer = "127.0.0.1:39001" };
            var result = await routes.SelectAsync(reason, "Auto", _ =>
            {
                if (reason == "cancelled") throw new OperationCanceledException();
                if (reason == "manual-race") store.Current = foreign;
                return Task.FromResult(false);
            }, default);
            check(result.Status == "Rejected" && (reason == "manual-race"
                ? store.Current == foreign && !session.Connected
                : store.Current == original && session.Connected), "native-rollback-" + reason);
            check(!routes.AllowsBackgroundMutation, "failed-takeover-no-background-probing-through-external-tun-" + reason);
        }

        store = new(original); session = new(store) { Connected = true };
        runtime = options("native-rollback-http-unconfirmed");
        var rollbackChecks = 0;
        routes = new(runtime, owner, store, (_, _) =>
        {
            rollbackChecks++;
            throw new IOException("LabTransportFailure");
        }, externalTunnel: () => session.OwnsTunnel ? "ExternalTunnelOwnsRoute" : null, happ: session);
        var unconfirmed = await routes.SelectAsync("http-return-failed", "Auto", _ => Task.FromResult(false), default);
        check(rollbackChecks == 1 && unconfirmed.DetailCode == "HappRollbackRouteUnconfirmed" &&
            unconfirmed.Status == "Rejected" && routes.Mode == "External" && store.Current == original && session.Connected,
            "rollback-restores-native-state-but-never-claims-unverified-internet");

        store = new(original); session = new(store) { Connected = true };
        runtime = options("native-rollback-http-warming"); rollbackChecks = 0;
        routes = new(runtime, owner, store, (_, _) => Task.FromResult(++rollbackChecks >= 2),
            externalTunnel: () => session.OwnsTunnel ? "ExternalTunnelOwnsRoute" : null, happ: session);
        var warmed = await routes.SelectAsync("http-return-warming", "Auto", _ => Task.FromResult(false), default);
        check(rollbackChecks == 2 && warmed.DetailCode == "StableFrontNotReady" &&
            routes.Mode == "Standby" && !routes.AllowsBackgroundMutation && store.Current == original,
            "rollback-waits-for-real-http-not-just-port-or-tunnel");

        store = new(original); session = new(store) { Connected = true };
        runtime = options("native-crash-recovery");
        var interrupted = new HappRouteHandover(runtime, store, session);
        await interrupted.ChangeAsync("crash-test", false, original, default);
        await new HappRouteHandover(runtime, store, session).RecoverAsync(default);
        check(session.Connected && store.Current == original, "interrupted-native-handover-recovers-before-route-load");

        var art = original with { ProxyServer = "127.0.0.1:22080" };
        store = new(art); session = new(store);
        runtime = options("native-one-retry");
        var restarting = new HappRouteHandover(runtime, store, session);
        await restarting.ChangeAsync("one-retry", true, art, default);
        var attempts = 0;
        var warmedOnce = await HappWarmupRetry.ConfirmAsync((attempt, _) =>
            Task.FromResult(++attempts == 2 && attempt == 2), async token =>
            { await restarting.RetryFailedStartAsync(token); }, default);
        check(warmedOnce && attempts == 2 && session.Commands.SequenceEqual(new[] {true,false,true}) &&
            session.Connected && HappRouteHandover.IsHapp(store.Current), "native-start-one-owned-reconnect-recovers-transport");
        await restarting.RollbackAsync(default);
        check(!session.Connected && store.Current == art, "failed-second-native-start-still-restores-original-art");
        var retries = 0; attempts = 0;
        var failedTwice = await HappWarmupRetry.ConfirmAsync((_, _) => {attempts++; return Task.FromResult(false);},
            _ => {retries++; return Task.CompletedTask;}, default);
        check(!failedTwice && retries == 1 && attempts == 2, "native-start-retry-is-bounded-not-a-watchdog");
        retries = 0;
        check(await HappWarmupRetry.ConfirmAsync((_, _) => Task.FromResult(true),
            _ => {retries++; return Task.CompletedTask;}, default) && retries == 0, "healthy-native-start-not-restarted");
        using var cancelled = new CancellationTokenSource();
        var cancelledCorrectly = false;
        try { await HappWarmupRetry.ConfirmAsync((_, _) => {cancelled.Cancel(); return Task.FromResult(false);},
            _ => {retries++; return Task.CompletedTask;}, cancelled.Token); }
        catch (OperationCanceledException) {cancelledCorrectly = true;}
        check(cancelledCorrectly && retries == 0, "cancelled-native-start-does-not-dispatch-retry");
    }

    private sealed class FakeSession(MemorySystemProxyStore store) : IHappRouteSession
    {
        public bool Available => true;
        internal bool Connected;
        internal List<bool> Commands = [];
        public bool OwnsTunnel => Connected;
        public Task SetConnectedAsync(bool connected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Commands.Add(connected);
            Connected = connected;
            store.Current = store.Current with { ProxyEnable = connected ? 1 : 0,
                ProxyServer = connected ? "127.0.0.1:10809" : "" };
            return Task.CompletedTask;
        }
    }
}
