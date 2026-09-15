using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

// Uses actual controller methods with private temporary state and an in-memory
// proxy store. Does not start a VPN, bind ports or change Windows settings.
internal static class ConnectionDiagnosisControllerTests
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool ok) { if (!ok) throw new InvalidOperationException("DiagnosisControllerInvariant" + checks); checks++; }
        using var fixture = new Fixture();
        var ctl = fixture.Controller;
        var token = CancellationToken.None;
        await ctl.ProbeAndRecordRouteAsync(22080, token);
        await ctl.UpdateConnectionDiagnosisAsync(22080, false, false, token);
        Check(fixture.DirectCalls == 0 && fixture.Detail() == "");
        await ctl.UpdateConnectionDiagnosisAsync(22080, false, false, token);
        Check(fixture.DirectCalls == 0); // Reusing the same observation is not a second failure.
        await Task.Delay(2);
        await ctl.ProbeAndRecordRouteAsync(22080, token);
        await ctl.UpdateConnectionDiagnosisAsync(22080, false, false, token);
        Check(fixture.DirectCalls == 1 && fixture.Detail() == "VpnUnavailableInternetWorks");
        await ctl.ProbeAndRecordRouteAsync(22080, token);
        await ctl.UpdateConnectionDiagnosisAsync(22080, false, false, token);
        Check(fixture.DirectCalls == 1); // Rate-limited, even with newer failures.
        fixture.Probe = (_, _) => Task.FromResult(true);
        await ctl.ProbeAndRecordRouteAsync(22080, token);
        await ctl.UpdateConnectionDiagnosisAsync(22080, true, false, token);
        Check(fixture.Detail() == "Resolved" && fixture.DirectCalls == 1);
        fixture.Probe = (_, _) => Task.FromResult(false);
        await ctl.ProbeAndRecordRouteAsync(22080, token);
        await ctl.UpdateConnectionDiagnosisAsync(22080, false, false, token);
        Check(fixture.DirectCalls == 1); // Recovery reset the streak.
        fixture.Direct = _ => Task.FromResult(new DirectInternetEvidence(false, false));
        await ctl.UpdateConnectionDiagnosisAsync(22080, false, true, token);
        Check(fixture.Detail() == "NetworkUnavailable");
        fixture.Direct = _ => throw new IOException("FixtureDirectFailure");
        await ctl.UpdateConnectionDiagnosisAsync(22080, false, true, token);
        Check(fixture.Detail() == "NetworkUnavailable"); // Optional diagnosis cannot fail the control command.

        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await ctl.UpdateConnectionDiagnosisAsync(22080, false, true, canceled.Token); throw new InvalidOperationException("CanceledDiagnosisRan"); }
        catch (OperationCanceledException) { checks++; }
        fixture.Direct = _ => Task.FromResult(new DirectInternetEvidence(true, false));
        await ctl.UpdateConnectionDiagnosisAsync(22080, false, true, token);
        Check(fixture.Detail() == "DirectInternetUnconfirmed");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DirectInternetEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Direct = async ct => { entered.TrySetResult(); return await release.Task.WaitAsync(ct); };
        var lateDiagnosis = ctl.UpdateConnectionDiagnosisAsync(22080, false, true, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var calls = fixture.DirectCalls;
        await ctl.UpdateConnectionDiagnosisAsync(22080, false, true, token);
        Check(fixture.DirectCalls == calls); // Duplicate requests are coalesced.
        fixture.Front.SwitchNewConnectionsTo(22087, "FixtureNextSlot");
        release.TrySetResult(new(true, true));
        await lateDiagnosis.WaitAsync(TimeSpan.FromSeconds(3));
        Check(fixture.Detail() == "DirectInternetUnconfirmed"); // Old slot diagnosis not published.
        fixture.Direct = _ => Task.FromResult(new DirectInternetEvidence(true, true));
        await ctl.ProbeAndRecordRouteAsync(22080, token);
        await ctl.UpdateConnectionDiagnosisAsync(22080, false, false, token);
        Check(fixture.DirectCalls == calls); // New slot starts a new streak.

        // A slow failure finishes after a newer successful check of the same slot.
        var oldRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Probe = (_, ct) => oldRelease.Task.WaitAsync(ct);
        var oldProbe = ctl.ProbeAndRecordRouteAsync(22080, token);
        await Task.Delay(2);
        fixture.Probe = (_, _) => Task.FromResult(true);
        await ctl.ProbeAndRecordRouteAsync(22080, token);
        var newest = File.ReadAllText(fixture.HealthPath);
        oldRelease.TrySetResult(false);
        await oldProbe.WaitAsync(TimeSpan.FromSeconds(3));
        Check(File.ReadAllText(fixture.HealthPath) == newest);
        await ctl.UpdateConnectionDiagnosisAsync(22080, false, true, token);
        Check(fixture.DirectCalls == calls);
        await ctl.UpdateConnectionDiagnosisAsync(22080, true, false, token);
        Check(fixture.Detail() == "Resolved");

        // Recovery while the direct probe is still waiting invalidates its result.
        fixture.Probe = (_, _) => Task.FromResult(false);
        await ctl.ProbeAndRecordRouteAsync(22080, token);
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Direct = async ct => { entered.TrySetResult(); return await release.Task.WaitAsync(ct); };
        lateDiagnosis = ctl.UpdateConnectionDiagnosisAsync(22080, false, true, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Probe = (_, _) => Task.FromResult(true);
        await ctl.ProbeAndRecordRouteAsync(22080, token);
        release.TrySetResult(new(true, true));
        await lateDiagnosis.WaitAsync(TimeSpan.FromSeconds(3));
        Check(fixture.Detail() == "Resolved");

        // Another endpoint's failed preflight cannot overwrite active-route details.
        fixture.Probe = (_, _) => Task.FromResult(false);
        await ctl.ProbeAndRecordRouteAsync(2080, token);
        calls = fixture.DirectCalls;
        await ctl.UpdateConnectionDiagnosisAsync(2080, false, true, token);
        Check(fixture.DirectCalls == calls && fixture.Detail() == "Resolved");
        await ctl.ProbeAndRecordRouteAsync(22080, token);
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lateDiagnosis = ctl.UpdateConnectionDiagnosisAsync(22080, false, true, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Proxy.Current = fixture.Proxy.Current with { ProxyServer = "127.0.0.1:2080" };
        release.TrySetResult(new(false, false));
        await lateDiagnosis.WaitAsync(TimeSpan.FromSeconds(3));
        Check(fixture.Detail() == "Resolved");
        Check(fixture.Proxy.Writes == 0 && !fixture.Front.IsServing);
        return checks;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "art-vpn-diagnosis-test-" + Guid.NewGuid().ToString("N"));
        internal readonly MemorySystemProxyStore Proxy = new(new(1, "127.0.0.1:22080", "<local>", null));
        internal readonly StableFrontHost Front;
        internal readonly ProtectedController Controller;
        internal Func<int, CancellationToken, Task<bool>> Probe = (_, _) => Task.FromResult(false);
        internal Func<CancellationToken, Task<DirectInternetEvidence>> Direct = _ => Task.FromResult(new DirectInternetEvidence(true, true));
        internal int DirectCalls;
        internal string HealthPath => Path.Combine(_root, "state", "route-health-22080.v1.json");
        internal Fixture()
        {
            var options = RuntimeOptions.Test(_root, "ARTSPORT.ARTVpn.DiagnosisTest." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "state"));
            var install = new InstallState(1, "art-vpn-consumer-install", "Configured", Environment.MachineName,
                "S-1-5-21-111-222-333", "S-1-5-21-111-222-333-1001", Guid.NewGuid().ToString("N"),
                "1.0.11", new string('E', 64), DateTimeOffset.UtcNow.ToString("o"), false, false);
            Front = new StableFrontHost(options, restorePersistedTarget: false);
            Front.SwitchNewConnectionsTo(22086, "FixtureInitialSlot");
            Controller = new ProtectedController(options, install, (_, _) => throw new InvalidOperationException("UnexpectedProviderFetch"),
                front: Front, enforcePrivateAcl: false, systemProxyStore: Proxy,
                modeProbe: (port, ct) => Probe(port, ct), internetProbe: ct => { DirectCalls++; return Direct(ct); });
        }
        internal string Detail()
        {
            var path = Path.Combine(_root, "state", "connection-diagnosis.v1.json");
            if (!File.Exists(path)) return "";
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.GetProperty("code").GetString()!;
        }
        public void Dispose()
        {
            var exact = Path.GetFullPath(_root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!exact.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(exact).StartsWith("art-vpn-diagnosis-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("FixtureCleanupBoundary");
            Directory.Delete(exact, true);
        }
    }
}
