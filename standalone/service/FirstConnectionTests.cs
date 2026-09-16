using System.Text;

namespace ArtSport.ArtVpn.Service;

internal static class FirstConnectionTests
{
    internal static async Task<object> RunAsync()
    {
        var checks = new List<string>();
        void Check(bool passed, string code) { if (!passed) throw new InvalidOperationException(code); checks.Add(code); }
        var scheduler = new SubscriptionRefreshScheduler("11111111111111111111111111111111");
        var now = DateTimeOffset.UtcNow;
        scheduler.AfterFirstConnection(now);
        Check(scheduler.NextRunUtc == now.AddSeconds(30) && !scheduler.ShouldRun(now) &&
            !scheduler.ShouldRun(now.AddSeconds(29)) && scheduler.ShouldRun(now.AddSeconds(30)),
            "CommittedFirstConnectionSchedulesOneEarlyRefinement");
        scheduler.Succeeded(now.AddMinutes(2));
        Check(scheduler.NextRunUtc >= now.AddHours(12), "RefinementReturnsToRegularSchedule");
        scheduler.ObserveResult(SubscriptionRefreshScheduler.WaitingForDrain, now, true);
        Check(!scheduler.ShouldRun(now.AddSeconds(29)) && scheduler.ShouldRun(now.AddSeconds(30)),
            "DrainingConnectionsDeferRatherThanRejectSubscription");
        for (var i = 0; i < 10; i++)
            scheduler.ObserveResult(SubscriptionRefreshScheduler.WaitingForDrain, now, true);
        var untouched = new SubscriptionRefreshScheduler("11111111111111111111111111111111");
        untouched.Failed(now, true);
        scheduler.ObserveResult("QualifiedPoolEmpty", now, true);
        Check(scheduler.NextRunUtc == untouched.NextRunUtc, "DrainWaitDoesNotConsumeFailureBackoff");
        scheduler.ObserveResult("ProviderGenerationQualifiedAndActivated", now, true);
        Check(scheduler.NextRunUtc >= now.AddHours(12), "SuccessfulDrainRetryRestoresRegularSchedule");
        scheduler.ObserveResult("CoreCheckTimedOut", now, true);
        Check(scheduler.NextRunUtc == now.AddSeconds(30), "ConfigCheckTimeoutRetriesWithoutThirtyMinuteStall");
        for (var i = 0; i < 8; i++) scheduler.ObserveResult("CoreCheckTimedOut", now, true);
        Check(scheduler.NextRunUtc == now.AddMinutes(4), "RepeatedConfigTimeoutBackoffIsBounded");
        scheduler.Succeeded(now);
        scheduler.ObserveResult("CoreCheckTimedOut", now, true);
        Check(scheduler.NextRunUtc == now.AddSeconds(30), "SuccessfulRefinementResetsConfigTimeoutBackoff");
        scheduler.Failed(now);
        Check(scheduler.RequestRecovery(now) && scheduler.ShouldRun(now), "DeadSoleChannelRequestsImmediateFullRecovery");
        Check(!scheduler.RequestRecovery(now.AddSeconds(30)) && !scheduler.RequestRecovery(now.AddSeconds(119)) &&
            scheduler.RequestRecovery(now.AddSeconds(120)), "SustainedFailureDoesNotFloodSubscriptionRequests");
        var failedChannel = new WatchdogDecision("KeepCurrent", "NoPrequalifiedReserve", 22086, 0, 3, false, now.ToString("o"));
        Check(ProtectedController.NeedsPoolRecovery(failedChannel) &&
            !ProtectedController.NeedsPoolRecovery(failedChannel with { FailureCount = 2 }) &&
            !ProtectedController.NeedsPoolRecovery(failedChannel with { ReasonCode = "CurrentChannelHealthy" }) &&
            !ProtectedController.NeedsPoolRecovery(failedChannel with { Action = "SwitchNewConnections" }),
            "PoolRecoveryRequiresConfirmedFailureAndNoReserve");
        Check(ProtectedController.RecoveryPreemptsRefinement(true, true, "Auto") &&
            !ProtectedController.RecoveryPreemptsRefinement(false, true, "Auto") &&
            !ProtectedController.RecoveryPreemptsRefinement(true, false, "Auto") &&
            !ProtectedController.RecoveryPreemptsRefinement(true, true, "ArtVpn") &&
            !ProtectedController.RecoveryPreemptsRefinement(true, true, "Happ"),
            "ConfirmedAutoFailurePreemptsBackgroundOnly");
        var nodes = Enumerable.Range(0, 9).Select(i => new ProviderNodeView(
            "node" + i, "Node " + i, i % 2 == 0 ? "NL" : "DE", "test", "server" + i, 443, 23000 + i, new string('a', 32))).ToArray();
        NodeProbeEvidence Good(ProviderNodeView n) => new(n.Tag, n.ProbeIdentity, n.Country, n.ServerKey,
            n.DestinationPort, true, false, 6, 6, 100, 100, 100, 100, 0, true, 0, 80, 100, 5, 20, 100, n.Country, "Passed", 100);
        var batches = new List<int>();
        Task<NodeProbeEvidence[]> Probe(IReadOnlyList<ProviderNodeView> batch, CancellationToken token)
        { token.ThrowIfCancellationRequested(); batches.Add(batch.Count); return Task.FromResult(batch.Select(Good).ToArray()); }
        var deepCalls = 0;
        Task<NodeProbeEvidence[]> BootstrapProbe(IReadOnlyList<ProviderNodeView> batch, CancellationToken token) =>
            Task.FromResult(batch.Select(n => Good(n) with { RoundsRequested = 1, RoundsCompleted = 1 }).ToArray());
        var bootstrap = await FirstConnectionQualification.FirstPassAsync(nodes, "", BootstrapProbe,
            (batch, token) => { deepCalls++; return Probe(batch, token); }, CancellationToken.None, bootstrapOnly: true);
        Check(deepCalls == 0 && bootstrap.Nodes.Length == 1 && bootstrap.Deep.Single().RoundsCompleted == 1,
            "InstallerConnectsFirstVerifiedNodeWithoutSixRoundDeepTest");
        foreach (var bad in new Func<NodeProbeEvidence, NodeProbeEvidence>[] {
            e => e with { TlsIntegrityFailure = true }, e => e with { IntegrityPct = 0 },
            e => e with { OpenAiPct = 0 }, e => e with { ChatGptPct = 0 }, e => e with { RoundsCompleted = 0 } })
        {
            var picked = await FirstConnectionQualification.FirstPassAsync(nodes.Take(2).ToArray(), "",
                (batch, token) => Task.FromResult(batch.Select(n => {
                    var e = Good(n) with { RoundsRequested = 1, RoundsCompleted = 1 };
                    return n.Tag == "node0" ? bad(e) : e;
                }).ToArray()), Probe, CancellationToken.None, bootstrapOnly: true);
            Check(picked.Nodes.Single().Tag == "node1", "BootstrapRejectsBadEvidence" + checks.Count);
        }
        var quick = await FirstConnectionQualification.RunAsync(nodes, "", true, Probe, CancellationToken.None);
        Check(batches.SequenceEqual(new[] {4}) && quick.Nodes.Length == 4, "FirstConnectionStopsAfterQualifiedBatch");
        Check(quick.Evidence.All(x => x.RoundsCompleted == 6), "DeepAdmissionRoundsUnchanged");
        batches.Clear();
        var fastPass = await FirstConnectionQualification.FirstPassAsync(nodes, "", Probe, Probe, CancellationToken.None);
        Check(batches.SequenceEqual(new[] { 1, 1 }) && fastPass.Prefilter.Length == 1 && fastPass.Nodes.Length == 1,
            "FirstConnectionDoesNotWaitForWholeSubscriptionPrefilter");
        var batchIndex = 0;
        var nextPass = await FirstConnectionQualification.FirstPassAsync(nodes, "", (batch, _) =>
        {
            batchIndex++;
            return Task.FromResult(batch.Select(n => batchIndex == 1 ? Good(n) with { Clean = false } : Good(n)).ToArray());
        }, Probe, CancellationToken.None);
        Check(batchIndex == 2 && nextPass.Nodes.Single().Tag == "node1", "FailedFirstNodeTriesNextCandidate");
        batches.Clear();
        var full = await FirstConnectionQualification.RunAsync(nodes, "", false, Probe, CancellationToken.None);
        Check(batches.SequenceEqual(new[] {9}) && full.Nodes.Length == 9, "BackgroundQualificationUnchanged");
        var calls = 0;
        var retry = await FirstConnectionQualification.RunAsync(nodes, "", true, (batch, _) =>
        {
            calls++;
            return Task.FromResult(batch.Select(n => calls == 1 ? Good(n) with { Clean = false } : Good(n)).ToArray());
        }, CancellationToken.None);
        Check(calls == 2 && retry.Nodes.Length == 8, "FailedFastBatchTriesNextCandidates");
        Check(QualificationPolicy.Select(retry.Nodes, retry.Evidence).Pool.All(n => n.Tag != "node0"), "FailedNodesNeverPromoted");
        foreach (var bad in new Func<NodeProbeEvidence, NodeProbeEvidence>[] {
            n => n with { Clean = false }, n => n with { TlsIntegrityFailure = true },
            n => n with { RoundsCompleted = 2 }, n => n with { IntegrityPct = 0 }, n => n with { EgressIpChanges = 1 } })
        {
            try
            {
                await FirstConnectionQualification.RunAsync(nodes, "", true,
                    (batch, _) => Task.FromResult(batch.Select(n => bad(Good(n))).ToArray()), CancellationToken.None);
                throw new InvalidOperationException("UnqualifiedFirstConnectionAccepted");
            }
            catch (InvalidDataException ex) when (ex.Message == "QualifiedPoolEmpty") { checks.Add("BadEvidenceRejected" + checks.Count); }
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var previous = batches.Count;
            try { await FirstConnectionQualification.RunAsync(nodes, "", true, Probe, cancelled.Token); throw new InvalidOperationException("CancelledConnectRan"); }
            catch (OperationCanceledException) { Check(batches.Count == previous, "ManualCancellationStopsBeforeNextBatch"); }
        }
        var value = Encoding.UTF8.GetBytes("https://example.invalid/subscription-test");
        foreach (var connect in new[] { false, true })
        {
            using var pipe = new MemoryStream();
            await ProvisioningProtocol.WriteRequestAsync(pipe, value, value, CancellationToken.None, connect);
            pipe.Position = 0;
            var request = await ProvisioningProtocol.ReadRequestAsync(pipe, CancellationToken.None);
            Check(request.Connect == connect && request.First.SequenceEqual(value), connect ? "P2ExplicitConnect" : "P1StillSaveOnly");
            Array.Clear(request.First); Array.Clear(request.Second);
        }
        using (var invalid = new MemoryStream("ARTVPNP3"u8.ToArray()))
        {
            try { await ProvisioningProtocol.ReadRequestAsync(invalid, CancellationToken.None); throw new InvalidOperationException("UnknownProvisionAccepted"); }
            catch (InvalidDataException ex) when (ex.Message == "ProvisionProtocolRejected") { checks.Add("UnknownProtocolRejected"); }
        }
        Check(ProviderProvisionResponse.Success().RequestId is null, "P1ResponseCompatible");
        var id = Guid.NewGuid().ToString("N");
        Check(ProviderProvisionResponse.Success(id).RequestId == id && ProviderProvisionResponse.Success(id).DetailCode == "ProviderConnectQueued", "ConnectReceiptCorrelated");
        checks.AddRange(await ProgressiveFirstConnectionTests.RunAsync());
        return new { status = "Passed", scenarios = checks.Count, checks, secretDisplayed = false, productionNetworkChanged = false };
    }
}
