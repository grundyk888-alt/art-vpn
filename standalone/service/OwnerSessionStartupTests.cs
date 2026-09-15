using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

internal static class OwnerSessionStartupTests
{
    internal static async Task<IReadOnlyList<string>> RunAsync(string root)
    {
        const string owner = "S-1-5-21-111-222-333-1001";
        var cases = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("OwnerSessionStartupFailed:" + name);
            cases.Add("owner-session-" + name);
        }
        foreach (var mode in new[] { "Standby", "Auto", "ArtVpn", "Throne", "Happ" })
        {
            var options = RuntimeOptions.Test(Path.Combine(root, mode), "OwnerSession." + mode);
            var store = new SessionStore(new(0, null, "custom.test;<local>", null));
            var routes = new RouteModeControl(options, owner, store, (_, _) => Task.FromResult(true));
            if (mode != "Standby")
                await routes.SelectAsync("fixture-owner", mode, _ => Task.FromResult(true), CancellationToken.None);
            var snapshot = store.Snapshot;
            var writes = store.Writes;
            var routeBefore = File.Exists(Path.Combine(options.DataRoot, "state", "route-mode.v1.json"))
                ? File.ReadAllBytes(Path.Combine(options.DataRoot, "state", "route-mode.v1.json")) : [];
            store.Available = false;
            var install = new InstallState(1, "art-vpn-consumer-install", "Configured", Environment.MachineName,
                "S-1-5-21-111-222-333", owner, Guid.NewGuid().ToString("N"), "fixture", new string('E', 64),
                DateTimeOffset.UtcNow.ToString("o"), false, false);
            var controller = new ProtectedController(options, install, (_, _) => throw new Exception("UnexpectedFetch"),
                front: new StableFrontHost(options, restorePersistedTarget: false), enforcePrivateAcl: false,
                systemProxyStore: store, modeProbe: (_, _) => Task.FromResult(true));
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var prepare = controller.PrepareAsync(cancel.Token);
            await Task.Delay(35, cancel.Token);
            var status = JsonSerializer.Deserialize<SanitizedServiceStatus>(File.ReadAllText(options.SanitizedStatusPath), JsonSettings.Strict)!;
            Check(!prepare.IsCompleted && status.Health == "Paused" && status.ServiceAvailable, mode + "-waits-with-live-status");
            Check(store.Writes == writes && store.Snapshot == snapshot, mode + "-no-network-write-before-logon");
            store.Available = true;
            await prepare.WaitAsync(TimeSpan.FromSeconds(5));
            var reloaded = new RouteModeControl(options, owner, store, (_, _) => Task.FromResult(true));
            Check(reloaded.Mode == mode && store.Writes == writes && store.Snapshot == snapshot, mode + "-preserved-after-logon");
            var routeAfter = File.Exists(Path.Combine(options.DataRoot, "state", "route-mode.v1.json"))
                ? File.ReadAllBytes(Path.Combine(options.DataRoot, "state", "route-mode.v1.json")) : [];
            Check(routeBefore.SequenceEqual(routeAfter), mode + "-receipt-not-rewritten");
            store.Available = false;
            Check(!reloaded.AllowsBackgroundMutation, mode + "-logoff-blocks-background-mutation");
        }
        var missing = new SessionStore(new(0, null, null, null)) { Available = false };
        var announcements = 0;
        using (var cancellation = new CancellationTokenSource())
        {
            var waiting = OwnerSessionAvailability.WaitAsync(missing, () => announcements++, cancellation.Token, TimeSpan.FromMilliseconds(5));
            await Task.Delay(30);
            cancellation.Cancel();
            var cancelled = false;
            try { await waiting; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && announcements == 1 && missing.Writes == 0, "stop-cancels-without-restart-or-log-flood");
        }
        missing.Fault = new UnauthorizedAccessException("FixtureAccessDenied");
        var visible = false;
        try { OwnerSessionAvailability.IsAvailable(missing); } catch (UnauthorizedAccessException) { visible = true; }
        Check(visible, "unrelated-registry-failure-not-hidden");
        return cases;
    }

    private sealed class SessionStore(SystemProxySnapshot snapshot) : ISystemProxyStore
    {
        internal volatile bool Available = true;
        internal SystemProxySnapshot Snapshot = snapshot;
        internal int Writes;
        internal Exception? Fault;
        public SystemProxySnapshot Read() => Fault is not null ? throw Fault :
            Available ? Snapshot : throw new SystemProxyOwnerUnavailableException();
        public void Write(SystemProxySnapshot value)
        {
            if (!Available) throw new SystemProxyOwnerUnavailableException();
            Snapshot = value;
            Writes++;
        }
    }
}
