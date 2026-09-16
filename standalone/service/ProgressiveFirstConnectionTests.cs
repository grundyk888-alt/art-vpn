namespace ArtSport.ArtVpn.Service;

// Controlled completions, not real network timing. A stalled neighbouring
// node cannot hold a fully admitted first route or leak probes after return.
internal static class ProgressiveFirstConnectionTests
{
    internal static async Task<string[]> RunAsync()
    {
        var checks = new List<string>();
        void Check(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); checks.Add(code); }
        var nodes = Enumerable.Range(0, 12).Select(i => new ProviderNodeView("race" + i, "Fixture", "NL", "test",
            "server" + i, 443, 23100 + i, new string('b', 32))).ToArray();
        NodeProbeEvidence Good(ProviderNodeView n) => new(n.Tag, n.ProbeIdentity, n.Country, n.ServerKey,
            n.DestinationPort, true, false, 6, 6, 100, 100, 100, 100, 0, true, 0, 80, 100, 5, 20, 100, "NL", "Passed", 100);
        Task<NodeProbeEvidence[]> Ready(IReadOnlyList<ProviderNodeView> batch, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(batch.Select(Good).ToArray()); }

        var releaseGood = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0; var finished = 0; var cancelled = 0;
        var race = FirstConnectionQualification.FirstPassAsync(nodes, "",
            async (batch, token) =>
            {
                Interlocked.Increment(ref started);
                try
                {
                    if (batch.Single().Tag == "race1") await releaseGood.Task.WaitAsync(token);
                    else await Task.Delay(Timeout.Infinite, token);
                    return batch.Select(Good).ToArray();
                }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
                finally { Interlocked.Increment(ref finished); }
            }, Ready, CancellationToken.None);
        await Until(() => Volatile.Read(ref started) == 8);
        releaseGood.SetResult(true);
        var first = await race.WaitAsync(TimeSpan.FromSeconds(5));
        Check(first.Nodes.Single().Tag == "race1", "FastQualifiedNodeDoesNotWaitForPrefilterNeighbours");
        Check(started == 8 && finished == 8 && cancelled == 7, "FirstWinnerCancelsAndJoinsRemainingProbes");
        Check(first.Deep.Single().RoundsCompleted == 6, "ProgressiveWinnerKeepsDeepAdmission");

        var deepRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        started = finished = cancelled = 0;
        var deepRace = FirstConnectionQualification.FirstPassAsync(nodes, "", Ready,
            async (batch, token) =>
            {
                Interlocked.Increment(ref started);
                try
                {
                    if (batch.Single().Tag == "race1") await deepRelease.Task.WaitAsync(token);
                    else await Task.Delay(Timeout.Infinite, token);
                    return batch.Select(Good).ToArray();
                }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
                finally { Interlocked.Increment(ref finished); }
            }, CancellationToken.None);
        await Until(() => Volatile.Read(ref started) == 4);
        deepRelease.SetResult(true);
        first = await deepRace.WaitAsync(TimeSpan.FromSeconds(5));
        Check(first.Nodes.Single().Tag == "race1", "FastQualifiedNodeDoesNotWaitForDeepNeighbours");
        Check(finished == started && started <= 8 && cancelled == started - 1, "DeepWorkIsJoinedAfterWinner");

        foreach (var mutate in new Func<NodeProbeEvidence, NodeProbeEvidence>[] {
            e => e with { RoundsCompleted = 1 }, e => e with { TlsIntegrityFailure = true },
            e => e with { OpenAiPct = 0 }, e => e with { ChatGptPct = 0 }, e => e with { IntegrityPct = 0 } })
        {
            var result = await FirstConnectionQualification.FirstPassAsync(nodes.Take(2).ToArray(), "", Ready,
                (batch, token) => Task.FromResult(batch.Select(n => n.Tag == "race0" ? mutate(Good(n)) : Good(n)).ToArray()),
                CancellationToken.None);
            Check(result.Nodes.Single().Tag == "race1", "UnqualifiedEarlyCompletionSkipped" + checks.Count);
        }
        var noRoute = false;
        try { await FirstConnectionQualification.FirstPassAsync(nodes, "", Ready,
            (batch, token) => Task.FromResult(batch.Select(n => Good(n) with { Clean = false }).ToArray()), CancellationToken.None); }
        catch (InvalidDataException ex) when (ex.Message == "QualifiedPoolEmpty") { noRoute = true; }
        Check(noRoute, "ProgressiveAllRejectedNeverClaimsConnected");

        using var manual = new CancellationTokenSource();
        started = finished = 0;
        var interrupted = FirstConnectionQualification.FirstPassAsync(nodes, "",
            async (batch, token) =>
            {
                Interlocked.Increment(ref started);
                try { await Task.Delay(Timeout.Infinite, token); return batch.Select(Good).ToArray(); }
                finally { Interlocked.Increment(ref finished); }
            }, Ready, manual.Token);
        await Until(() => Volatile.Read(ref started) == 8);
        manual.Cancel(); var didCancel = false;
        try { await interrupted.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { didCancel = true; }
        Check(didCancel && started == finished, "ManualChoiceCancelsAndJoinsFirstConnectionRace");
        var ranked = new[] { Good(nodes[0]) with { ScoreMs = 120, GoogleAvailabilityPct = 0 },
            Good(nodes[1]) with { ScoreMs = 300, GoogleAvailabilityPct = 100 } };
        Check(QualificationPolicy.Select(nodes.Take(2).ToArray(), ranked).PrimaryTag == "race0", "CodexScoreWinsOverUnrelatedGooglePreference");
        ranked[0] = ranked[0] with { ScoreMs = 350 };
        Check(QualificationPolicy.Select(nodes.Take(2).ToArray(), ranked, "race0").PrimaryTag == "race0", "SmallRefinementKeepsCurrentRoute");
        ranked[0] = ranked[0] with { ScoreMs = 650 };
        Check(QualificationPolicy.Select(nodes.Take(2).ToArray(), ranked, "race0").PrimaryTag == "race1", "SustainedLargeRefinementCanReplaceCurrentRoute");
        var sample = new ProbeRoundSample(true, true, true, true, false, 100, 100, 100, 100, 20,
            new string('a', 64), "NL", "Passed", false);
        var baseScore = NodeQualificationRunner.Summarize(nodes[0], 6, Enumerable.Repeat(sample, 6).ToArray()).ScoreMs;
        var slowerTelegram = NodeQualificationRunner.Summarize(nodes[0], 6, Enumerable.Repeat(sample with { TelegramMs = 3000 }, 6).ToArray()).ScoreMs;
        var slowerAi = NodeQualificationRunner.Summarize(nodes[0], 6, Enumerable.Repeat(sample with { OpenAiMs = 800, ChatGptMs = 800 }, 6).ToArray()).ScoreMs;
        Check(baseScore == slowerTelegram && slowerAi > baseScore, "ScoreMeasuresAiLatencyWithoutWeakeningAdmission");
        return checks.ToArray();
    }

    private static async Task Until(Func<bool> condition)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, bound.Token);
    }
}
