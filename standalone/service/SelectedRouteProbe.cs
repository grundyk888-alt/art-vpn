using System.Diagnostics;

namespace ArtSport.ArtVpn.Service;

// Deliberately excludes response bodies, URLs, provider identifiers and egress IPs.
internal sealed record ProbeEndpointEvidence(string Name, bool Passed, int HttpStatus, double ElapsedMs, string Code);
internal sealed record SelectedRouteObservation(int Schema, string Kind, int Port, bool Ready, string AtUtc,
    double ElapsedMs, string Code, ProbeEndpointEvidence[] Checks, string RemoteFunctional = "NotTested");

internal static class SelectedRouteProbe
{
    private static readonly string[] Names = ["OpenAI", "ChatGPT", "Telegram", "Integrity"];

    public static async Task<SelectedRouteObservation> ObserveAsync(int port, ProbeRoundTransport transport,
        CancellationToken cancellationToken)
    {
        if (port is not (2080 or 10809 or 22080)) throw new ArgumentOutOfRangeException(nameof(port));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var watch = Stopwatch.StartNew();
        try
        {
            var sample = await transport(port, 65_536, budget.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var readiness = new[] { sample.OpenAi, sample.ChatGpt, sample.Telegram, sample.Integrity };
            var checks = Names.Select((name, index) =>
            {
                var source = sample.Endpoints?.FirstOrDefault(item => item.Name == name);
                var passed = readiness[index];
                return new ProbeEndpointEvidence(name, passed, Math.Clamp(source?.HttpStatus ?? 0, 0, 599),
                    Duration(source?.ElapsedMs ?? 0), passed ? "Passed" : SafeCode(source?.Code ?? sample.FailureClass));
            }).ToArray();
            // All advertised core services must pass. Google and mtalk are diagnostic-only;
            // their success is never presented as authenticated Codex Remote acceptance.
            var ready = readiness.All(value => value) && !sample.HardDegradation;
            var code = ready ? "Passed" : checks.FirstOrDefault(item => !item.Passed)?.Code ?? SafeCode(sample.FailureClass);
            return new(1, "art-vpn-selected-route-observation", port, ready, DateTimeOffset.UtcNow.ToString("o"),
                Duration(watch.Elapsed.TotalMilliseconds), code, checks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(1, "art-vpn-selected-route-observation", port, false, DateTimeOffset.UtcNow.ToString("o"),
                Duration(watch.Elapsed.TotalMilliseconds), ex is OperationCanceledException ? "Timeout" : "ProbeFailed", []);
        }
    }

    public static void Record(string root, SelectedRouteObservation observation)
    {
        // Failure evidence survives later successful probes. Only two fixed-size receipts per
        // endpoint plus the existing bounded journal are retained; telemetry never breaks routing.
        try
        {
            if (observation.Port is not (2080 or 10809 or 22080)) return;
            var path = Path.Combine(root, "state", $"route-probe-{observation.Port}.v1.json");
            AtomicFile.ReplaceJson(path, observation);
            if (!observation.Ready)
            {
                AtomicFile.ReplaceJson(Path.Combine(root, "state", $"route-probe-{observation.Port}.last-failure.v1.json"), observation);
                var failed = observation.Checks.Where(item => !item.Passed).Select(item => item.Name);
                SafeLog.Write(root, $"RouteProbe.{observation.Port}.{observation.Code}.{string.Join('.', failed)}");
            }
        }
        catch { SafeLog.Write(root, "RouteProbeReceiptWriteFailed"); }
    }

    private static double Duration(double value) => double.IsFinite(value) ? Math.Round(Math.Clamp(value, 0, 60_000), 1) : 0;
    private static string SafeCode(string value) => value switch
    {
        "Timeout" or "TlsBadRecordMac" or "TlsIntegrityFailure" or "ConnectionReset" or "PartialTransfer" or
        "TransportFailure" or "ResponseRejected" => value,
        _ => "ProbeFailed"
    };
}
