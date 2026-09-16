using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

internal static class ExternalReserveTests
{
    internal static async Task<string[]> RunAsync(string root)
    {
        const string owner = "S-1-5-21-111-222-333-1001";
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("ExternalReserveInvariantFailed:" + name);
            checks.Add(name);
        }
        static Task<bool> Ready(CancellationToken _) => Task.FromResult(true);
        var before = new SystemProxySnapshot(1, "127.0.0.1:22080", "local.test;*.ru;<local>", null);
        foreach (var client in new[] { ExternalProxyEndpoint.Throne, ExternalProxyEndpoint.Happ })
        {
            var options = RuntimeOptions.Test(Path.Combine(root, client.Mode), "Fixture." + client.Mode);
            var store = new MemorySystemProxyStore(before);
            var probes = new List<int>();
            var route = new RouteModeControl(options, owner, store, (port, _) =>
            { probes.Add(port); return Task.FromResult(true); });
            var selected = await route.SelectAsync("external", client.Mode, _ => throw new Exception("NativeStartedForExternal"), CancellationToken.None);
            Check(selected.Status == "Completed" && route.Mode == client.Mode && !route.AllowsBackgroundMutation &&
                probes.SequenceEqual(new[] { client.Port, client.Port }) && store.Current.ProxyServer == $"127.0.0.1:{client.Port}", client.Mode + "-manual-endpoint-verified");
            Check(route.ExternalReservePort == client.Port, client.Mode + "-reserve-preference-committed");
            var front = new StableFrontHost(options, restorePersistedTarget: false);
            front.SwitchNewConnectionsTo(22086, "FixtureStaleNativeFront");
            var install = new InstallState(1, "art-vpn-consumer-install", "Configured", Environment.MachineName,
                "S-1-5-21-111-222-333", owner, Guid.NewGuid().ToString("N"), "fixture", new string('E', 64),
                DateTimeOffset.UtcNow.ToString("o"), false, false);
            var controller = new ProtectedController(options, install, (_, _) => throw new Exception("ProviderReadForManualReserve"),
                front: front, enforcePrivateAcl: false, systemProxyStore: store, modeProbe: (_, _) => Task.FromResult(true));
            await controller.PrepareAsync(CancellationToken.None);
            var startup = JsonSerializer.Deserialize<SanitizedServiceStatus>(File.ReadAllText(options.SanitizedStatusPath), JsonSettings.Strict)!;
            Check(front.TargetPort == client.Port && startup.Mode == client.Mode && startup.Health == "Fallback" &&
                startup.Country == client.DisplayName && startup.Nodes.Length == 0,
                client.Mode + "-startup-reconciles-stale-front-without-starting-native-core");
            var writes = store.Writes;
            var restarted = new RouteModeControl(options, owner, store, (_, _) => Task.FromResult(true));
            Check(restarted.Mode == client.Mode && restarted.ExternalReservePort == client.Port &&
                !restarted.AllowsBackgroundMutation && store.Writes == writes, client.Mode + "-restart-preserves-manual");
            var auto = await restarted.SelectAsync("auto", "Auto", Ready, CancellationToken.None);
            Check(auto.Status == "Completed" && restarted.Mode == "Auto" && restarted.ExternalReservePort == client.Port,
                client.Mode + "-auto-retains-preferred-reserve");
            Check(new RouteModeControl(options, owner, store, (_, _) => Task.FromResult(true)).ExternalReservePort == client.Port,
                client.Mode + "-auto-preference-survives-restart");
            var restored = restarted.RestorePrevious();
            Check(restored.Status == "Completed" && store.Current == before, client.Mode + "-restore-original-not-intermediate-proxy");

            var rejectedOptions = RuntimeOptions.Test(Path.Combine(root, client.Mode + "-rejected"), "Fixture.Rejected");
            var rejectedStore = new MemorySystemProxyStore(before);
            var rejectedRoute = new RouteModeControl(rejectedOptions, owner, rejectedStore, (_, _) => Task.FromResult(false));
            var rejected = await rejectedRoute.SelectAsync("dead", client.Mode, Ready, CancellationToken.None);
            Check(rejected.Status == "Rejected" && rejected.DetailCode == "SelectedRoutePreflightFailed" &&
                rejectedStore.Current == before && rejectedStore.Writes == 0 && rejectedRoute.ExternalReservePort == 2080,
                client.Mode + "-failed-preflight-preserves-network-and-preference");
            var round = 0;
            rejectedRoute = new RouteModeControl(rejectedOptions, owner, rejectedStore, (_, _) => Task.FromResult(++round == 1));
            rejected = await rejectedRoute.SelectAsync("postflight", client.Mode, Ready, CancellationToken.None);
            Check(rejected.Status == "Rejected" && rejectedStore.Current == before && rejectedStore.Writes == 2 &&
                rejectedRoute.ExternalReservePort == 2080, client.Mode + "-failed-postflight-rolls-back");

            var status = RouteStatusPresentation.AfterCommit(SanitizedServiceStatus.Initial("fixture"), client.Mode,
                22086, client.Port, true, DateTimeOffset.UtcNow);
            Check(status.Country == client.DisplayName && status.Mode == client.Mode && status.Nodes.Length == 0 && status.Health == "Fallback",
                client.Mode + "-manual-status-hides-native-pool");
            status = RouteStatusPresentation.AfterCommit(status, "Auto", 22086, client.Port, true, DateTimeOffset.UtcNow);
            Check(status.Country == client.DisplayName && status.Mode == "Авто" && status.Health == "Fallback",
                client.Mode + "-auto-status-names-actual-external");
        }
        // ART is the default working route, not dependent on the optional
        // reserve being started. A remembered reserve is only a preference;
        // it is never evidence that its process or connection is ready.
        foreach (var client in new[] { ExternalProxyEndpoint.Throne, ExternalProxyEndpoint.Happ })
        {
            var options = RuntimeOptions.Test(Path.Combine(root, "art-first-" + client.Mode), "Fixture.ArtFirst." + client.Mode);
            var store = new MemorySystemProxyStore(before);
            var reserveReady = true;
            var probes = new List<int>();
            Task<bool> Probe(int port, CancellationToken _)
            { probes.Add(port); return Task.FromResult(port == 22080 || (port == client.Port && reserveReady)); }
            var route = new RouteModeControl(options, owner, store, Probe);
            await route.SelectAsync("remember", client.Mode, Ready, CancellationToken.None);
            reserveReady = false;
            probes.Clear();
            var auto = await route.SelectAsync("art-first", "Auto", Ready, CancellationToken.None);
            Check(auto.Status == "Completed" && route.Mode == "Auto" && store.Current.ProxyServer == "127.0.0.1:22080" &&
                probes.Count == 2 && probes.All(port => port == 22080),
                client.Mode + "-inactive-reserve-does-not-block-art-auto");
            route = new RouteModeControl(options, owner, store, Probe);
            Check(route.Mode == "Auto" && route.AllowsBackgroundMutation && route.ExternalReservePort == client.Port,
                client.Mode + "-reload-retains-art-auto-with-inactive-preferred-reserve");
            var writes = store.Writes;
            var unavailable = await route.SelectAsync("unavailable-manual", client.Mode, Ready, CancellationToken.None);
            Check(unavailable.Status == "Rejected" && route.Mode == "Auto" && store.Writes == writes &&
                store.Current.ProxyServer == "127.0.0.1:22080",
                client.Mode + "-manual-unavailable-reserve-does-not-cut-working-art");
            reserveReady = true;
            var manual = await route.SelectAsync("manual-ready", client.Mode,
                _ => throw new InvalidOperationException("ArtMustNotStartForManualReserve"), CancellationToken.None);
            Check(manual.Status == "Completed" && route.Mode == client.Mode && !route.AllowsBackgroundMutation &&
                store.Current.ProxyServer == $"127.0.0.1:{client.Port}",
                client.Mode + "-manual-selection-suspends-art-auto-return");
            var rejectedReturn = await route.SelectAsync("art-not-ready", "Auto", _ => Task.FromResult(false), CancellationToken.None);
            Check(rejectedReturn.Status == "Rejected" && route.Mode == client.Mode &&
                store.Current.ProxyServer == $"127.0.0.1:{client.Port}",
                client.Mode + "-failed-art-return-preserves-working-manual-reserve");
            var returned = await route.SelectAsync("art-ready", "Auto", Ready, CancellationToken.None);
            Check(returned.Status == "Completed" && route.Mode == "Auto" && route.AllowsBackgroundMutation &&
                store.Current.ProxyServer == "127.0.0.1:22080" && route.ExternalReservePort == client.Port,
                client.Mode + "-explicit-auto-returns-to-art-and-remembers-reserve");
        }
        // Migration: old route receipts without the optional preference still choose Throne.
        var legacyOptions = RuntimeOptions.Test(Path.Combine(root, "legacy"), "Fixture.Legacy");
        AtomicFile.ReplaceJson(Path.Combine(legacyOptions.DataRoot, "state", "route-mode.v1.json"), new
        { schema = 1, kind = "art-vpn-route-mode", requestId = "legacy", mode = "Auto", phase = "Committed",
            expected = before, atUtc = DateTimeOffset.UtcNow.ToString("o"), previousMode = "", previousExpected = (object?)null,
            containsProviderSecret = false });
        var legacy = new RouteModeControl(legacyOptions, owner, new MemorySystemProxyStore(before), (_, _) => Task.FromResult(true));
        Check(legacy.Mode == "Auto" && legacy.ExternalReservePort == 2080, "legacy-auto-receipt-remains-compatible");
        checks.AddRange(await SlotRuntimeManager.VerifyExternalFallbackAsync(Path.Combine(root, "watchdog")));
        return checks.ToArray();
    }
}
