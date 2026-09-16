using System.Text;
using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

internal static class RouteModeControlTests
{
    private const string Owner = "S-1-5-21-111-222-333-1001";
    public static async Task<object> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-route-mode-test-" + Guid.NewGuid().ToString("N"));
        var checkedCases = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("RouteModeInvariantFailed:" + name);
            checkedCases.Add(name);
        }
        var before = new SystemProxySnapshot(1, "127.0.0.1:2080", "local.test;*.ru;<local>", null);
        var external = before with { ProxyServer = "127.0.0.1:39001", ProxyOverride = "foreign.test" };
        static Task<bool> Ready(CancellationToken _) => Task.FromResult(true);
        static Task<bool> Probe(int _, CancellationToken __) => Task.FromResult(true);
        RuntimeOptions Options(string name) => RuntimeOptions.Test(Path.Combine(root, name), "RouteModeTest." + name);
        try
        {
            await HappHandoverTests.RunAsync(Options, Check);
            var tunnelStore = new MemorySystemProxyStore(before);
            var tunnelRoute = new RouteModeControl(Options("tun-guard"), Owner, tunnelStore, Probe,
                externalTunnel: () => "ExternalTunnelOwnsRoute");
            var preparedWithTunnel = false;
            var tunnelBlocked = await tunnelRoute.SelectAsync("tun", "Auto", _ =>
            { preparedWithTunnel = true; return Task.FromResult(true); }, CancellationToken.None, true);
            Check(tunnelBlocked.DetailCode == "ExternalTunnelOwnsRoute" && tunnelStore.Writes == 0 && !preparedWithTunnel,
                "external-tun-blocks-before-qualification-without-network-write");
            var externalStillSelectable = await tunnelRoute.SelectAsync("tun-happ", "Happ", Ready, CancellationToken.None);
            Check(externalStillSelectable.Status == "Completed", "external-client-remains-selectable-with-tun");
            var checks = 0;
            var lateStore = new MemorySystemProxyStore(before);
            var lateTunnel = new RouteModeControl(Options("late-tun"), Owner, lateStore, Probe,
                externalTunnel: () => ++checks > 1 ? "ExternalTunnelOwnsRoute" : null);
            var lateBlocked = await lateTunnel.SelectAsync("late", "Auto", Ready, CancellationToken.None);
            Check(lateBlocked.DetailCode == "ExternalTunnelOwnsRoute" && lateStore.Writes == 0,
                "tun-appearing-during-qualification-prevents-commit");
            Check(ExternalTunnelDiagnosis.IsKnownTunnel("happ-xray", "Happ Tunnel") &&
                !ExternalTunnelDiagnosis.IsKnownTunnel("Ethernet", "Microsoft Hyper-V Network Adapter"),
                "tun-detection-does-not-confuse-hyperv-network");
            var changedBypassOptions = Options("reclaim-bypass");
            var changedBypassStore = new MemorySystemProxyStore(before);
            var reclaim = new RouteModeControl(changedBypassOptions, Owner, changedBypassStore, Probe);
            await reclaim.SelectAsync("before-bypass", "Auto", Ready, CancellationToken.None);
            changedBypassStore.Current = changedBypassStore.Current with { ProxyOverride = "new.user.rule" };
            await reclaim.SelectAsync("after-bypass", "Auto", Ready, CancellationToken.None);
            var retainedLease = SystemProxyLeaseCoordinator.TryReadLease(changedBypassOptions.SystemProxyLeasePath)!;
            Check(retainedLease.Previous.ProxyServer == before.ProxyServer && retainedLease.Previous.ProxyOverride == "new.user.rule",
                "reclaim-after-bypass-edit-never-saves-own-endpoint-as-uninstall-fallback");
            checkedCases.AddRange(await OwnerSessionStartupTests.RunAsync(Path.Combine(root, "owner-session")));
            foreach (var name in await SelectedRouteProbeTests.RunAsync(Path.Combine(root, "probe-evidence")))
                checkedCases.Add(name);
            checkedCases.AddRange(await ExternalReserveTests.RunAsync(Path.Combine(root, "external-reserve")));
            Check(RouteStatusPresentation.VerifyContract() == 14, "route-transition-metadata-is-current");
            foreach (var available in new[] { true, false })
            {
                var rememberedStore = new MemorySystemProxyStore(before with { ProxyServer = "127.0.0.1:10809" });
                var remembered = new RouteModeControl(Options("first-happ-" + available), Owner,
                    rememberedStore, (port, _) => Task.FromResult(port != 10809 || available));
                var first = await remembered.SelectAsync("first-happ", "Auto", Ready, CancellationToken.None, true);
                Check(first.Status == "Completed" && remembered.ExternalReservePort == (available ? 10809 : 2080),
                    "first-connect-remembers-only-verified-external-" + available);
            }
            foreach (var (manual, probeReady) in new[] { (false, false), (true, true), (true, false) })
            {
                var startupOptions = Options("startup-" + manual + "-" + probeReady);
                var startupStore = new MemorySystemProxyStore(before);
                if (manual)
                    await new RouteModeControl(startupOptions, Owner, startupStore, Probe)
                        .SelectAsync("startup-fixture", "Throne", Ready, CancellationToken.None);
                AtomicFile.ReplaceJson(startupOptions.SanitizedStatusPath, SanitizedServiceStatus.Initial("fixture") with
                {
                    Health = "Healthy", Country = "OldCountry",
                    Nodes = [new("● ОСНОВНОЙ", "OldCountry", "old-node", "old-proof")]
                });
                var startupInstall = new InstallState(1, "art-vpn-consumer-install", "Configured", Environment.MachineName,
                    "S-1-5-21-111-222-333", Owner, Guid.NewGuid().ToString("N"), "fixture", new string('E', 64),
                    DateTimeOffset.UtcNow.ToString("o"), false, false);
                var startupController = new ProtectedController(startupOptions, startupInstall, (_, _) => Task.FromResult(Array.Empty<byte>()),
                    front: new StableFrontHost(startupOptions, restorePersistedTarget: false), enforcePrivateAcl: false,
                    systemProxyStore: startupStore, modeProbe: (_, _) => Task.FromResult(probeReady));
                await startupController.PrepareAsync(CancellationToken.None);
                var status = JsonSerializer.Deserialize<SanitizedServiceStatus>(File.ReadAllText(startupOptions.SanitizedStatusPath), JsonSettings.Strict)!;
                Check(status.Health == (manual ? probeReady ? "Fallback" : "Unavailable" : "Stopped") &&
                    status.Nodes.Length == 0 && startupStore.Current == before && startupStore.Writes == 0,
                    "startup-never-reuses-old-health-" + manual + "-" + probeReady);
            }
            var options = Options("normal");
            var store = new MemorySystemProxyStore(before);
            var route = new RouteModeControl(options, Owner, store, Probe);
            Check(route.Mode == "Standby", "no-status-derived-auto");
            var idempotent = await route.SelectAsync("test1", "Throne", Ready, CancellationToken.None);
            Check(idempotent.Status == "Completed" && !idempotent.Changed && store.Writes == 0,
                "idempotent-throne-no-registry-write");
            Check(!route.AllowsBackgroundMutation, "manual-throne-disables-background");
            var restarted = new RouteModeControl(options, Owner, store, Probe);
            Check(restarted.Mode == "Throne" && !restarted.AllowsBackgroundMutation && store.Writes == 0,
                "restart-honors-throne");
            var auto = await restarted.SelectAsync("test2", "Auto", Ready, CancellationToken.None);
            Check(auto.Status == "Completed" && auto.Changed && store.Current.ProxyServer == "127.0.0.1:22080" &&
                restarted.AllowsBackgroundMutation, "auto-real-proxy");
            Check(store.Current.ProxyOverride!.Contains("local.test") && store.Current.AutoConfigUrl is null,
                "custom-bypass-preserved");
            Check(new RouteModeControl(options, Owner, store, Probe).Mode == "Auto", "restart-retains-owned-auto");
            var writes = store.Writes;
            var art = await restarted.SelectAsync("test3", "ArtVpn", Ready, CancellationToken.None);
            Check(art.Status == "Completed" && !art.Changed && store.Writes == writes && restarted.Mode == "ArtVpn",
                "manual-art-idempotent-shared-front");
            var back = await restarted.SelectAsync("test4", "Throne", Ready, CancellationToken.None);
            Check(back.Status == "Completed" && back.Changed && store.Current.ProxyServer == "127.0.0.1:2080" &&
                !restarted.AllowsBackgroundMutation, "art-to-throne-real-switch");
            var restore = restarted.RestorePrevious();
            Check(restore.Status == "Completed" && store.Current == before && restarted.Mode == "External",
                "restore-original-and-pause");

            options = Options("preflight"); store = new MemorySystemProxyStore(before);
            route = new RouteModeControl(options, Owner, store, (_, _) => Task.FromResult(false));
            var rejected = await route.SelectAsync("test5", "Auto", Ready, CancellationToken.None);
            Check(rejected.DetailCode == "SelectedRoutePreflightFailed" && store.Current == before && store.Writes == 0,
                "dead-candidate-no-change");
            route = new RouteModeControl(options, Owner, store, Probe);
            rejected = await route.SelectAsync("test6", "Auto", _ => Task.FromResult(false), CancellationToken.None);
            Check(rejected.DetailCode == "StableFrontNotReady" && store.Writes == 0, "art-runtime-not-ready");
            store.Current = before with { AutoConfigUrl = "https://pac.example.invalid/config" };
            rejected = await route.SelectAsync("test7", "Throne", Ready, CancellationToken.None);
            Check(rejected.DetailCode == "SystemProxyPacConflict" && store.Writes == 0, "pac-preserved");

            options = Options("foreign-before"); store = new MemorySystemProxyStore(before);
            route = new RouteModeControl(options, Owner, store, (_, _) => { store.Current = external; return Task.FromResult(true); });
            rejected = await route.SelectAsync("test8", "Auto", Ready, CancellationToken.None);
            Check(rejected.Status == "Rejected" && store.Current == external && store.Writes == 0 &&
                !route.AllowsBackgroundMutation, "foreign-change-during-preflight");

            options = Options("postflight"); store = new MemorySystemProxyStore(before);
            var round = 0;
            route = new RouteModeControl(options, Owner, store, (_, _) => Task.FromResult(++round == 1));
            rejected = await route.SelectAsync("test9", "Auto", Ready, CancellationToken.None);
            Check(rejected.DetailCode == "SelectedRoutePostflightFailed" && store.Current == before && store.Writes == 2,
                "postflight-failure-restores-exact-proxy");
            Check(!File.Exists(options.SystemProxyLeasePath), "postflight-restores-lease");

            options = Options("foreign-after"); store = new MemorySystemProxyStore(before); round = 0;
            route = new RouteModeControl(options, Owner, store, (_, _) =>
            {
                if (++round == 2) store.Current = external;
                return Task.FromResult(true);
            });
            rejected = await route.SelectAsync("test10", "Auto", Ready, CancellationToken.None);
            Check(rejected.Status == "Rejected" && store.Current == external && store.Writes == 1 && route.Mode == "External",
                "rollback-never-overwrites-newer-external-choice");

            options = Options("state-write-failure"); store = new MemorySystemProxyStore(before);
            var failOnce = true;
            route = new RouteModeControl(options, Owner, store, Probe, (path, value) =>
            {
                if (value.Mode == "Auto" && value.Phase == "Committed" && failOnce)
                { failOnce = false; throw new IOException("InjectedModeReceiptFailure"); }
                AtomicFile.ReplaceJson(path, value);
            });
            rejected = await route.SelectAsync("test11", "Auto", Ready, CancellationToken.None);
            Check(rejected.Status == "Rejected" && store.Current == before && !File.Exists(options.SystemProxyLeasePath),
                "receipt-failure-rolls-back-own-change");

            options = Options("write-failure"); store = new MemorySystemProxyStore(before) { FailNextWrite = true };
            route = new RouteModeControl(options, Owner, store, Probe);
            rejected = await route.SelectAsync("test12", "Auto", Ready, CancellationToken.None);
            Check(rejected.Status == "Rejected" && store.Current == before, "failed-registry-write-preserves-route");

            options = Options("external-restart"); store = new MemorySystemProxyStore(before);
            route = new RouteModeControl(options, Owner, store, Probe);
            await route.SelectAsync("test13", "Auto", Ready, CancellationToken.None);
            store.Current = external;
            var restart = new RouteModeControl(options, Owner, store, Probe);
            writes = store.Writes;
            Check(restart.Mode == "External" && !restart.AllowsBackgroundMutation && store.Writes == writes,
                "restart-does-not-reclaim-external-proxy");
            Check(route.ObserveExternalChoice() && route.Mode == "External" && store.Current == external,
                "running-external-override-pauses-controller");

            options = Options("crash-before-commit"); store = new MemorySystemProxyStore(before);
            var modePath = Path.Combine(options.DataRoot, "state", "route-mode.v1.json");
            var pending = new RouteModeState(1, "art-vpn-route-mode", "test14", "Auto", "Prepared",
                before with { ProxyServer = "127.0.0.1:22080" }, DateTimeOffset.UtcNow.ToString("o"), "Throne", before, false);
            AtomicFile.ReplaceJson(modePath, pending);
            restart = new RouteModeControl(options, Owner, store, Probe);
            Check(restart.Mode == "Throne" && store.Writes == 0, "crash-before-proxy-restores-prior-mode");

            options = Options("crash-after-lease"); store = new MemorySystemProxyStore(before);
            modePath = Path.Combine(options.DataRoot, "state", "route-mode.v1.json");
            AtomicFile.ReplaceJson(modePath, pending);
            store.Current = pending.Expected;
            SystemProxyLeaseCoordinator.WriteLease(options.SystemProxyLeasePath,
                new SystemProxyLease(1, "art-vpn-system-proxy-lease", Owner, "Applied", before,
                    pending.Expected, pending.AtUtc, DateTimeOffset.UtcNow.ToString("o"), false));
            restart = new RouteModeControl(options, Owner, store, Probe);
            Check(restart.Mode == "Auto" && restart.AllowsBackgroundMutation && store.Writes == 0,
                "crash-after-verified-lease-reconciles-without-network-write");

            options = Options("preempt-refresh"); store = new MemorySystemProxyStore(before);
            ProviderSecretStore.Store(options, Encoding.UTF8.GetBytes("https://example.invalid/fixture"), enforcePrivateAcl: false);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var install = new InstallState(1, "art-vpn-consumer-install", "Configured", Environment.MachineName,
                "S-1-5-21-111-222-333", Owner, Guid.NewGuid().ToString("N"), "1.0.1", new string('E', 64),
                DateTimeOffset.UtcNow.ToString("o"), false, false);
            var controller = new ProtectedController(options, install, async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            }, enforcePrivateAcl: false, systemProxyStore: store, modeProbe: Probe);
            var refresh = Request("RefreshSubscription");
            AtomicFile.WriteJson(Path.Combine(options.RequestRoot, refresh.RequestId + ".json"), refresh);
            var refreshRun = controller.ProcessAvailableAsync(CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var throne = Request("SetMode", "Throne");
            AtomicFile.WriteJson(Path.Combine(options.RequestRoot, throne.RequestId + ".json"), throne);
            await refreshRun.WaitAsync(TimeSpan.FromSeconds(3));
            Check(await controller.ProcessAvailableAsync(CancellationToken.None), "manual-request-preempts-stuck-provider-fetch");
            var receipt = JsonSerializer.Deserialize<ControllerRequestResult>(File.ReadAllText(
                Path.Combine(options.ResultRoot, throne.RequestId + ".json")), JsonSettings.Strict)!;
            Check(receipt.Status == "Completed" && store.Current == before && store.Writes == 0,
                "preemption-executes-real-throne-control-without-proxy-bounce");

            return new { status = "Passed", scenarios = checkedCases.Count, checks = checkedCases,
                productionNetworkChanged = false, realFunctionsWithInjectedEffects = true,
                authenticatedRemoteTested = false, secretDisplayed = false };
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("art-vpn-route-mode-test-", StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static QueuedRequest Request(string action, string? mode = null) => new(1, "art-vpn-controller-request",
        Guid.NewGuid().ToString("N"), action, mode, null, Owner, 1, DateTimeOffset.UtcNow.ToString("o"));
}
