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
        Check(scheduler.NextRunUtc == now.AddMinutes(5) && !scheduler.ShouldRun(now),
            "CommittedFirstConnectionSchedulesWithinAllowedBounds");
        var nodes = Enumerable.Range(0, 9).Select(i => new ProviderNodeView(
            "node" + i, "Node " + i, i % 2 == 0 ? "NL" : "DE", "test", "server" + i, 443, 23000 + i, new string('a', 32))).ToArray();
        NodeProbeEvidence Good(ProviderNodeView n) => new(n.Tag, n.ProbeIdentity, n.Country, n.ServerKey,
            n.DestinationPort, true, false, 6, 6, 100, 100, 100, 100, 0, true, 0, 80, 100, 5, 20, 100, n.Country, "Passed", 100);
        var batches = new List<int>();
        Task<NodeProbeEvidence[]> Probe(IReadOnlyList<ProviderNodeView> batch, CancellationToken token)
        { token.ThrowIfCancellationRequested(); batches.Add(batch.Count); return Task.FromResult(batch.Select(Good).ToArray()); }
        var quick = await FirstConnectionQualification.RunAsync(nodes, "", true, Probe, CancellationToken.None);
        Check(batches.SequenceEqual(new[] {4}) && quick.Nodes.Length == 4, "FirstConnectionStopsAfterQualifiedBatch");
        Check(quick.Evidence.All(x => x.RoundsCompleted == 6), "DeepAdmissionRoundsUnchanged");
        batches.Clear();
        var fastPass = await FirstConnectionQualification.FirstPassAsync(nodes, "", Probe, Probe, CancellationToken.None);
        Check(batches.SequenceEqual(new[] { 8, 4 }) && fastPass.Prefilter.Length == 8,
            "FirstConnectionDoesNotWaitForWholeSubscriptionPrefilter");
        var batchIndex = 0;
        var nextPass = await FirstConnectionQualification.FirstPassAsync(nodes, "", (batch, _) =>
        {
            batchIndex++;
            return Task.FromResult(batch.Select(n => batchIndex == 1 ? Good(n) with { Clean = false } : Good(n)).ToArray());
        }, Probe, CancellationToken.None);
        Check(batchIndex == 2 && nextPass.Nodes.Single().Tag == "node8", "NoCleanFirstBatchTriesRemainingSubscription");
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
        return new { status = "Passed", scenarios = checks.Count, checks, secretDisplayed = false, productionNetworkChanged = false };
    }
}
