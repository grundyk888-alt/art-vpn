using System.Net;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Setup;

internal static class UninstallRouteReturnTests
{
    internal static async Task<object> RunAsync()
    {
        const string owner = "S-1-5-21-111-222-333-1001";
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-uninstall-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "system-proxy-lease.v1.json");
        var passed = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidDataException(name); passed.Add(name); }
        var previous = new SystemProxySnapshot(1, "127.0.0.1:10809", "custom.local;<local>", null);
        var applied = previous with { ProxyServer = "127.0.0.1:22080" };
        var now = DateTimeOffset.UtcNow.ToString("o");
        var lease = new SystemProxyLease(1, "art-vpn-system-proxy-lease", owner, "Applied", previous, applied, now, now, false);
        async Task<SystemProxyOperationReceipt> Restore(ISystemProxyStore store, bool ready = false) =>
            await UninstallRouteReturn.RestoreStoppedAsync(path, owner, store, CancellationToken.None,
                (_, _) => Task.FromResult(ready));
        try
        {
            foreach (var port in new[] {10809, 2080, 39007})
            {
                var baseline = previous with { ProxyServer = "127.0.0.1:" + port };
                SystemProxyLeaseCoordinator.WriteLease(path, lease with { Previous = baseline });
                var store = new MemorySystemProxyStore(applied);
                var result = await Restore(store);
                Check(result.Changed && store.Current == baseline with { ProxyEnable = 0, ProxyServer = null } &&
                    !File.Exists(path), "AbsentLocalVpnRemovable-" + port);
            }
            SystemProxyLeaseCoordinator.WriteLease(path, lease);
            var live = new MemorySystemProxyStore(applied);
            await Restore(live, true);
            Check(live.Current == previous && !File.Exists(path), "RunningPreviousProxyRestored");

            SystemProxyLeaseCoordinator.WriteLease(path, lease);
            var bypass = new MemorySystemProxyStore(applied with { ProxyOverride = "new.local;<local>" });
            await Restore(bypass);
            Check(bypass.Current == previous with { ProxyEnable = 0, ProxyServer = null, ProxyOverride = "new.local;<local>" },
                "NewBypassPreservedWithoutHapp");

            foreach (var original in new[] {
                previous with { ProxyEnable = 0 }, previous with { ProxyServer = "proxy.example:8080" },
                previous with { ProxyServer = "127.0.0.1:10809", AutoConfigUrl = "https://pac.example/config" },
                previous with { ProxyServer = "http=127.0.0.1:10809;https=127.0.0.1:10809" } })
            {
                SystemProxyLeaseCoordinator.WriteLease(path, lease with { Previous = original });
                var store = new MemorySystemProxyStore(applied);
                await Restore(store);
                Check(store.Current == original, "NonLocalOrDisabledOrPacPreserved-" + passed.Count);
            }
            foreach (var external in new[] { previous with { ProxyServer = "127.0.0.1:39001" },
                previous with { ProxyEnable = 0 }, previous with { AutoConfigUrl = "https://pac.example/new" } })
            {
                SystemProxyLeaseCoordinator.WriteLease(path, lease);
                var store = new MemorySystemProxyStore(external);
                await Restore(store);
                Check(store.Current == external && store.Writes == 0 && !File.Exists(path), "NewUserChoicePreserved-" + passed.Count);
            }

            SystemProxyLeaseCoordinator.WriteLease(path, lease with { Previous = applied });
            var self = new MemorySystemProxyStore(applied);
            await Restore(self);
            Check(self.Current.ProxyEnable == 0 && self.Current.ProxyServer is null, "SelfPreviousEndpointRemoved");
            var repeat = await Restore(self);
            Check(!repeat.Changed, "AlreadyUnmanagedIsIdempotent");

            var orphan = new MemorySystemProxyStore(applied);
            try { await Restore(orphan); throw new Exception("OrphanAccepted"); }
            catch (InvalidOperationException ex) { Check(ex.Message == "SystemProxyOwnershipRequiresManualResolution" && orphan.Writes == 0, "UnknownOwnershipRejected"); }

            SystemProxyLeaseCoordinator.WriteLease(path, lease);
            var failed = new MemorySystemProxyStore(applied) { FailNextWrite = true };
            try { await Restore(failed); throw new Exception("WriteFailureIgnored"); }
            catch (IOException) { Check(failed.Current == applied && File.Exists(path), "WriteFailureKeepsRecoveryLease"); }

            var foreign = previous with { ProxyServer = "127.0.0.1:39002" };
            var racing = new MemorySystemProxyStore(applied);
            try
            {
                await UninstallRouteReturn.RestoreStoppedAsync(path, owner, racing, CancellationToken.None,
                    (_, _) => { racing.Current = foreign; return Task.FromResult(false); });
                throw new Exception("RaceIgnored");
            }
            catch (InvalidOperationException ex) { Check(ex.Message == "SystemProxyOwnershipLost" && racing.Current == foreign && racing.Writes == 0, "ConcurrentUserChangePreserved"); }

            SystemProxyLeaseCoordinator.WriteLease(path, lease with { OwnerSid = "S-1-5-21-111-222-333-1002" });
            var wrong = new MemorySystemProxyStore(applied);
            try { await Restore(wrong); throw new Exception("WrongOwnerAccepted"); }
            catch (InvalidDataException) { Check(wrong.Writes == 0, "WrongOwnerRejected"); }

            foreach (var error in new Exception[] { new IOException(), new TimeoutException(), new OperationCanceledException(), new InvalidOperationException() })
                Check(UninstallRouteReturn.OptionalReturnFailure(error, CancellationToken.None), "OptionalClientFailure-" + error.GetType().Name);
            Check(!UninstallRouteReturn.OptionalReturnFailure(new OperationCanceledException(), new CancellationToken(true)), "CallerCancellationRespected");
            Check(!UninstallRouteReturn.OptionalReturnFailure(new System.Security.SecurityException(), CancellationToken.None), "SecurityFailureNotSwallowed");
            Check(UninstallRouteReturn.LocalEndpoint(previous with { ProxyServer = "[::1]:10809" })?.Address.Equals(IPAddress.IPv6Loopback) == true, "IPv6LoopbackSupported");
            foreach (var host in new[] { "proxy.example:8080", "10.0.0.1:10809", "http://127.0.0.1:10809", "127.0.0.1:0" })
                Check(UninstallRouteReturn.LocalEndpoint(previous with { ProxyServer = host }) is null, "NotOwnedLocalEndpoint-" + passed.Count);
            return new { status = "Passed", checks = passed.Count, scenarios = passed, systemChanged = false, secretDisplayed = false };
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
