using System.Text.Json;

namespace ArtSport.ArtVpn.Service;

internal static class SelectedRouteProbeTests
{
    internal static async Task<string[]> RunAsync(string root)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("RouteProbeInvariantFailed:" + name);
            checks.Add(name);
        }
        var sample = new ProbeRoundSample(true, true, true, true, false, 100, 100, 100, 100, 5,
            "do-not-persist-egress", "DE", "Passed", false, "Unavailable");
        Task<SelectedRouteObservation> Observe(ProbeRoundSample value) => SelectedRouteProbe.ObserveAsync(2080,
            (_, _, _) => Task.FromResult(value), CancellationToken.None);
        var good = await Observe(sample);
        Check(good.Ready && good.Checks.Length == 4 && good.RemoteFunctional == "NotTested",
            "route-probe-core-success-not-remote-proof");
        foreach (var name in new[] { "OpenAI", "ChatGPT", "Telegram", "Integrity" })
        {
            var failed = name switch
            {
                "OpenAI" => sample with { OpenAi = false },
                "ChatGPT" => sample with { ChatGpt = false },
                "Telegram" => sample with { Telegram = false },
                _ => sample with { Integrity = false }
            };
            failed = failed with { Endpoints = [new(name, false, 403, 234, "ResponseRejected")] };
            var observed = await Observe(failed);
            Check(!observed.Ready && observed.Checks.Single(item => !item.Passed) is
                { HttpStatus: 403, ElapsedMs: 234, Code: "ResponseRejected" }, "route-probe-requires-" + name);
        }
        var tls = await Observe(sample with { Integrity = false, HardDegradation = true, FailureClass = "TlsBadRecordMac" });
        Check(!tls.Ready && tls.Code == "TlsBadRecordMac", "route-probe-tls-fails-closed");
        var malicious = await Observe(sample with { ChatGpt = false, FailureClass = "https://private.invalid/subscription" });
        var serialized = JsonSerializer.Serialize(malicious);
        Check(!serialized.Contains("private.invalid") && !serialized.Contains("do-not-persist-egress") &&
            malicious.Code == "ProbeFailed", "route-probe-excludes-secrets-and-egress");
        var timeout = await SelectedRouteProbe.ObserveAsync(2080, (_, _, _) => throw new OperationCanceledException(), CancellationToken.None);
        Check(!timeout.Ready && timeout.Code == "Timeout", "route-probe-timeout-recorded");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await SelectedRouteProbe.ObserveAsync(2080, (_, _, token) => Task.FromCanceled<ProbeRoundSample>(token), cancelled.Token);
                throw new InvalidOperationException("CancellationSwallowed");
            }
            catch (OperationCanceledException) { checks.Add("route-probe-caller-cancellation-propagated"); }
        }
        try
        {
            await SelectedRouteProbe.ObserveAsync(443, (_, _, _) => Task.FromResult(sample), CancellationToken.None);
            throw new InvalidOperationException("UnexpectedEndpointAccepted");
        }
        catch (ArgumentOutOfRangeException) { checks.Add("route-probe-only-owned-loopback-endpoints"); }

        Directory.CreateDirectory(Path.Combine(root, "state"));
        SelectedRouteProbe.Record(root, tls);
        var failurePath = Path.Combine(root, "state", "route-probe-2080.last-failure.v1.json");
        var failedReceipt = File.ReadAllText(failurePath);
        SelectedRouteProbe.Record(root, good);
        Check(File.ReadAllText(failurePath) == failedReceipt &&
            File.ReadAllText(Path.Combine(root, "state", "route-probe-2080.v1.json")).Contains("\"ready\":true"),
            "route-probe-recovery-preserves-last-failure");
        var blockedRoot = Path.Combine(root, "file-not-directory");
        File.WriteAllText(blockedRoot, "fixture");
        SelectedRouteProbe.Record(blockedRoot, good);
        checks.Add("route-probe-telemetry-failure-isolated");
        return checks.ToArray();
    }
}
