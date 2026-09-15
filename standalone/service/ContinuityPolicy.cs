namespace ArtSport.ArtVpn.Service;

internal enum ProbeOutcome
{
    Healthy,
    LatencyDegraded,
    CoreFailure,
    TlsIntegrityFailure
}

internal sealed record CandidateSnapshot(
    string Id,
    string Country,
    double Score,
    int ConsecutiveCorePasses,
    DateTimeOffset QualifiedAtUtc,
    DateTimeOffset? QuarantinedUntilUtc = null)
{
    public bool IsReady(DateTimeOffset now) =>
        ConsecutiveCorePasses >= 4 &&
        (QuarantinedUntilUtc is null || QuarantinedUntilUtc <= now);
}

internal sealed record HealthObservation(
    DateTimeOffset AtUtc,
    string CheckId,
    ProbeOutcome Outcome);

internal sealed record ContinuityDecision(
    string Action,
    string ReasonCode,
    string ActiveId,
    string ActiveCountry,
    bool ExistingConnectionsInterrupted,
    string? QuarantinedId = null);

internal sealed class ContinuityPolicy
{
    private readonly List<HealthObservation> _failures = [];
    private int _healthyAfterFailure;

    public ContinuityPolicy(string activeId, string activeCountry, DateTimeOffset selectedAtUtc)
    {
        ActiveId = activeId;
        ActiveCountry = activeCountry;
        SelectedAtUtc = selectedAtUtc;
        LastCountryChangeUtc = selectedAtUtc;
    }

    public string ActiveId { get; private set; }
    public string ActiveCountry { get; private set; }
    public DateTimeOffset SelectedAtUtc { get; private set; }
    public DateTimeOffset LastCountryChangeUtc { get; private set; }

    public ContinuityDecision Observe(HealthObservation observation, CandidateSnapshot? reserve)
    {
        if (observation.Outcome == ProbeOutcome.LatencyDegraded)
            return Hold("BriefPerformanceDrop");

        if (observation.Outcome == ProbeOutcome.Healthy)
        {
            if (_failures.Count > 0 && ++_healthyAfterFailure >= 2)
            {
                _failures.Clear();
                _healthyAfterFailure = 0;
            }
            return Hold("CurrentChannelHealthy");
        }

        _healthyAfterFailure = 0;
        _failures.RemoveAll(item => observation.AtUtc - item.AtUtc > TimeSpan.FromMinutes(2));
        if (_failures.All(item => !string.Equals(item.CheckId, observation.CheckId, StringComparison.Ordinal)))
            _failures.Add(observation);

        var quarantine = observation.Outcome == ProbeOutcome.TlsIntegrityFailure ? ActiveId : null;
        if (_failures.Count < 3 || _failures[^1].AtUtc - _failures[0].AtUtc < TimeSpan.FromSeconds(30))
            return Hold(observation.Outcome == ProbeOutcome.TlsIntegrityFailure
                ? "TlsFailureObservedAndQuarantined"
                : "FailureThresholdNotReached", quarantine);

        if (reserve is null || !reserve.IsReady(observation.AtUtc) || string.Equals(reserve.Id, ActiveId, StringComparison.Ordinal))
            return Hold("NoPrequalifiedReserve", quarantine);

        var previousCountry = ActiveCountry;
        ActiveId = reserve.Id;
        ActiveCountry = reserve.Country;
        SelectedAtUtc = observation.AtUtc;
        if (!string.Equals(previousCountry, ActiveCountry, StringComparison.OrdinalIgnoreCase))
            LastCountryChangeUtc = observation.AtUtc;
        _failures.Clear();
        return new ContinuityDecision(
            "SwitchNewConnections",
            "ConfirmedCoreFailure",
            ActiveId,
            ActiveCountry,
            ExistingConnectionsInterrupted: false,
            quarantine);
    }

    public ContinuityDecision ConsiderReRanking(
        DateTimeOffset now,
        double currentScore,
        CandidateSnapshot candidate)
    {
        if (!candidate.IsReady(now)) return Hold("CandidateNotQualified");
        if (now - SelectedAtUtc < TimeSpan.FromMinutes(30)) return Hold("MinimumResidenceNotReached");
        if (!string.Equals(candidate.Country, ActiveCountry, StringComparison.OrdinalIgnoreCase) &&
            now - LastCountryChangeUtc < TimeSpan.FromHours(2))
            return Hold("CountryCooldownActive");
        if (candidate.Score > currentScore * 0.75d) return Hold("ImprovementBelowTwentyFivePercent");

        var previousCountry = ActiveCountry;
        ActiveId = candidate.Id;
        ActiveCountry = candidate.Country;
        SelectedAtUtc = now;
        if (!string.Equals(previousCountry, ActiveCountry, StringComparison.OrdinalIgnoreCase))
            LastCountryChangeUtc = now;
        return new ContinuityDecision(
            "SwitchNewConnections",
            "QualifiedMaterialImprovement",
            ActiveId,
            ActiveCountry,
            ExistingConnectionsInterrupted: false);
    }

    private ContinuityDecision Hold(string reason, string? quarantine = null) =>
        new("KeepCurrent", reason, ActiveId, ActiveCountry, ExistingConnectionsInterrupted: false, quarantine);
}

internal sealed class GenerationPromotionGate
{
    private string? _stagedId;
    private string? _stagedHash;
    private int _independentPasses;

    public GenerationPromotionGate(string activeId, string activeHash)
    {
        ActiveId = activeId;
        ActiveHash = activeHash;
    }

    public string ActiveId { get; private set; }
    public string ActiveHash { get; private set; }
    public string? StagedId => _stagedId;

    public void Stage(string generationId, string manifestHash)
    {
        if (string.IsNullOrWhiteSpace(generationId) || manifestHash.Length != 64)
            throw new InvalidDataException("GenerationRejected");
        _stagedId = generationId;
        _stagedHash = manifestHash;
        _independentPasses = 0;
    }

    public bool RecordAcceptance(string generationId, string manifestHash, bool backendReady, bool passed)
    {
        if (!string.Equals(generationId, _stagedId, StringComparison.Ordinal) ||
            !string.Equals(manifestHash, _stagedHash, StringComparison.Ordinal))
            throw new InvalidDataException("GenerationIdentityChanged");
        if (!backendReady || !passed)
        {
            _independentPasses = 0;
            return false;
        }
        if (++_independentPasses < 2) return false;

        ActiveId = _stagedId!;
        ActiveHash = _stagedHash!;
        _stagedId = null;
        _stagedHash = null;
        _independentPasses = 0;
        return true;
    }

    public void RejectStaged()
    {
        _stagedId = null;
        _stagedHash = null;
        _independentPasses = 0;
    }
}

internal static class ContinuityPolicyTests
{
    public static ContinuityTestReceipt Run()
    {
        var scenarios = 0;
        var origin = DateTimeOffset.Parse("2026-08-31T00:00:00Z");
        var reserve = new CandidateSnapshot("reserve-1", "Netherlands", 60, 4, origin);

        var policy = new ContinuityPolicy("current-1", "Estonia", origin);
        Assert(policy.Observe(new(origin.AddSeconds(1), "latency-1", ProbeOutcome.LatencyDegraded), reserve).Action == "KeepCurrent");
        scenarios++;
        Assert(policy.Observe(new(origin.AddSeconds(2), "core-1", ProbeOutcome.CoreFailure), reserve).Action == "KeepCurrent");
        Assert(policy.Observe(new(origin.AddSeconds(20), "core-2", ProbeOutcome.CoreFailure), reserve).Action == "KeepCurrent");
        scenarios++;
        var failover = policy.Observe(new(origin.AddSeconds(32), "core-3", ProbeOutcome.CoreFailure), reserve);
        Assert(failover.Action == "SwitchNewConnections" && !failover.ExistingConnectionsInterrupted && policy.ActiveId == reserve.Id);
        scenarios++;

        var unreadyPolicy = new ContinuityPolicy("current-2", "Estonia", origin);
        var unready = reserve with { ConsecutiveCorePasses = 3 };
        _ = unreadyPolicy.Observe(new(origin, "f1", ProbeOutcome.CoreFailure), unready);
        _ = unreadyPolicy.Observe(new(origin.AddSeconds(15), "f2", ProbeOutcome.CoreFailure), unready);
        Assert(unreadyPolicy.Observe(new(origin.AddSeconds(31), "f3", ProbeOutcome.CoreFailure), unready).ReasonCode == "NoPrequalifiedReserve");
        scenarios++;

        var tlsPolicy = new ContinuityPolicy("tls-node", "Latvia", origin);
        var tls = tlsPolicy.Observe(new(origin, "tls-1", ProbeOutcome.TlsIntegrityFailure), reserve);
        Assert(tls.Action == "KeepCurrent" && tls.QuarantinedId == "tls-node");
        scenarios++;

        var rankPolicy = new ContinuityPolicy("rank-current", "Estonia", origin);
        var faster = new CandidateSnapshot("rank-new", "Netherlands", 70, 4, origin);
        Assert(rankPolicy.ConsiderReRanking(origin.AddMinutes(20), 100, faster).ReasonCode == "MinimumResidenceNotReached");
        scenarios++;
        Assert(rankPolicy.ConsiderReRanking(origin.AddMinutes(40), 100, faster).ReasonCode == "CountryCooldownActive");
        scenarios++;
        Assert(rankPolicy.ConsiderReRanking(origin.AddHours(2), 100,
            faster with { Score = 76 }).ReasonCode == "ImprovementBelowTwentyFivePercent");
        scenarios++;
        var rerank = rankPolicy.ConsiderReRanking(origin.AddHours(2), 100, faster with { Score = 75 });
        Assert(rerank.Action == "SwitchNewConnections" && !rerank.ExistingConnectionsInterrupted);
        scenarios++;

        var gate = new GenerationPromotionGate("generation-old", new string('A', 64));
        gate.Stage("generation-new", new string('B', 64));
        Assert(gate.ActiveId == "generation-old" && !gate.RecordAcceptance("generation-new", new string('B', 64), true, true));
        Assert(gate.ActiveId == "generation-old" && gate.RecordAcceptance("generation-new", new string('B', 64), true, true));
        Assert(gate.ActiveId == "generation-new");
        scenarios++;

        gate.Stage("generation-bad", new string('C', 64));
        Assert(!gate.RecordAcceptance("generation-bad", new string('C', 64), true, false));
        gate.RejectStaged();
        Assert(gate.ActiveId == "generation-new" && gate.StagedId is null);
        scenarios++;

        return new ContinuityTestReceipt("Passed", scenarios, false, false);
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("ContinuityPolicyInvariantFailed");
    }
}

internal sealed record ContinuityTestReceipt(
    string Status,
    int Scenarios,
    bool ActiveGenerationLost,
    bool ExistingConnectionsInterrupted);
