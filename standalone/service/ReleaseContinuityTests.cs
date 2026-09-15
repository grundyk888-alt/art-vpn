using System.Text.Json;

namespace ArtSport.ArtVpn.Service;

// Exercises production activation, not the old CommitForTest approximation.
internal static class ReleaseContinuityTests
{
    public static async Task<object> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-release-test-" + Guid.NewGuid().ToString("N"));
        var results = new List<object>();
        try
        {
            using var lifeStop = new CancellationTokenSource();
            var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var managementStarted = false;
            var waitingRestoreCancelled = false;
            var life = RuntimeLifetime.RunAsync(lifeStop.Token,
                async ct => { managementStarted = true; await fault.Task.WaitAsync(ct); },
                async ct => { try { await Task.Delay(Timeout.Infinite, ct); } finally { waitingRestoreCancelled = true; } });
            results.Add(new { name = "LocalManagementStartsBeforeNetworkRestore", passed = managementStarted && !life.IsCompleted });
            fault.SetException(new InvalidOperationException("InjectedLocalListenerFailure"));
            var firstFaultVisible = false;
            try { await life.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (InvalidOperationException ex) { firstFaultVisible = ex.Message == "InjectedLocalListenerFailure"; }
            results.Add(new { name = "ComponentFailureCancelsSiblingsAndReachesRecovery", passed = firstFaultVisible && waitingRestoreCancelled });
            using var oversize = new StreamContent(new NonSeekableFixture(new byte[65]));
            var bounded = false;
            try { await BoundedHttpContent.ReadAsync(oversize, 64, CancellationToken.None); }
            catch (InvalidDataException) { bounded = true; }
            results.Add(new { name = "UnknownLengthHttpBodyIsBoundedDuringRead", passed = bounded });
            var options = RuntimeOptions.Test(root, "ARTSPORT.ARTVpn.Release." + Guid.NewGuid().ToString("N"));
            var front = new StableFrontHost(options);
            front.SwitchNewConnectionsTo(22086, "TestInitial");
            var path = Path.Combine(root, "state", "front-target.v1.json");
            var saved = File.ReadAllBytes(path);
            File.Delete(path);
            Directory.CreateDirectory(path); // deterministic failed atomic rename
            var before = front.Revision;
            try { front.SwitchNewConnectionsTo(22087, "TestWriteFailure"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            results.Add(new { name = "FrontWriteFailurePreservesTarget", passed = front.TargetPort == 22086 && front.Revision == before });
            Directory.Delete(path);
            File.WriteAllBytes(path, saved);

            var assets = new VerifiedRuntimeAssets("fixture-core", new string('A', 64), [], new string('B', 64));
            var leases = new List<TestLease>();
            await using var manager = new SlotRuntimeManager(options, front,
                (_, _, _, _) => { var lease = new TestLease(); leases.Add(lease); return Task.FromResult<ICoreLease>(lease); },
                (_, _, _) => Task.FromResult(new ProbeRoundSample(true, true, true, true, false,
                    50, 50, 50, 50, 10, "test-egress", "NL", "Passed", false)),
                (_, _, _) => Task.CompletedTask, _ => assets);
            // A fresh front starts with no active backend; test all real activation writes.
            var freshOptions = RuntimeOptions.Test(Path.Combine(root, "activation"), options.PipeName + ".Activation");
            var freshFront = new StableFrontHost(freshOptions);
            await using var activation = new SlotRuntimeManager(freshOptions, freshFront,
                (_, _, _, _) => { var lease = new TestLease(); leases.Add(lease); return Task.FromResult<ICoreLease>(lease); },
                (_, _, _) => Task.FromResult(new ProbeRoundSample(true, true, true, true, false,
                    50, 50, 50, 50, 10, "test-egress", "NL", "Passed", false)),
                (_, _, _) => Task.CompletedTask, _ => assets);
            var a = Candidate(freshOptions, assets, "A", "20260905T120000Z-aaaaaaaaaaaa");
            await activation.ActivateAsync(a, CancellationToken.None);
            var activeLease = leases[^1];
            var revision = freshFront.Revision;
            var b = Candidate(freshOptions, assets, "B", "20260905T120000Z-bbbbbbbbbbbb");
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(b.ConfigPath)!, "activation.v1.json"));
            try { await activation.ActivateAsync(b, CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            results.Add(new { name = "ActivationEvidenceWriteFailurePreservesChannel", passed =
                freshFront.TargetPort == 22086 && freshFront.Revision == revision && !activeLease.Disposed &&
                ActiveGenerationStore.TryRead(freshOptions)?.GenerationId == a.GenerationId });

            var repeated = Candidate(freshOptions, assets, "A", "20260905T120000Z-cccccccccccc");
            var rejected = false;
            var launchCount = leases.Count;
            try { await activation.ActivateAsync(repeated, CancellationToken.None); }
            catch (InvalidOperationException ex) when (ex.Message == "ActiveSlotReplacementRejected") { rejected = true; }
            results.Add(new { name = "StaleCandidateCannotReplaceActiveSlot", passed = rejected && !activeLease.Disposed && leases.Count == launchCount });
            var current = SanitizedServiceStatus.Initial("0.1.0-beta.31") with
            {
                Health = "Unavailable", Country = "Нидерланды", SelectedAtUtc = "new-selection",
                LastSwitchAtUtc = "new-switch", LastSwitchReason = "watchdog-new-reason", SafeRepairAvailable = true
            };
            var merged = ProtectedController.MergeRefreshFailure(current, true, "подписка недоступна", DateTimeOffset.UtcNow);
            results.Add(new { name = "SubscriptionFailurePreservesLatestHealthAndHistory", passed =
                merged.Health == current.Health && merged.Country == current.Country && merged.LastSwitchAtUtc == current.LastSwitchAtUtc &&
                merged.LastSwitchReason == current.LastSwitchReason && merged.SafeRepairAvailable });
            results.Add(new { name = "SemverUpgradeOrder", passed =
                ProductVersion.Compare("0.1.0-beta.31", "0.1.0-beta.9") > 0 &&
                ProductVersion.Compare("1.0.0", "1.0.0-rc.1") > 0 &&
                ProductVersion.Compare("1.0.0", "1.0.0") == 0 &&
                ProductVersion.Compare("1.0.0-rc.9", "1.0.0-rc.10") < 0 });
            var scheduleNow = DateTimeOffset.UtcNow;
            var scheduler = new SubscriptionRefreshScheduler(new string('a', 32));
            scheduler.Initialize(scheduleNow, activeGenerationReady: false);
            var initial = scheduler.NextRunUtc;
            scheduler.Failed(scheduleNow, activeGenerationReady: false);
            results.Add(new { name = "FirstSetupFailureRetriesWithoutActiveGeneration", passed =
                initial >= scheduleNow.AddMinutes(2) && initial <= scheduleNow.AddMinutes(3) &&
                scheduler.NextRunUtc >= scheduleNow.AddMinutes(2) && scheduler.NextRunUtc <= scheduleNow.AddMinutes(3) });
            var all = results.All(item => JsonSerializer.SerializeToElement(item).GetProperty("passed").GetBoolean());
            return new { status = all ? "Passed" : "Failed", scenarios = results.Count, results, productionNetworkChanged = false };
        }
        finally
        {
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full).StartsWith("art-vpn-release-test-", StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }

    private static QualifiedGenerationCandidate Candidate(RuntimeOptions options, VerifiedRuntimeAssets assets, string slot, string generation)
    {
        var folder = Path.Combine(options.PrivateGenerationRoot, generation, "runtime", slot.ToLowerInvariant());
        Directory.CreateDirectory(folder);
        var config = Path.Combine(folder, "config.json");
        File.WriteAllText(config, "{}");
        var pool = new QualifiedPool("n-1", ["n-1", "n-2", "n-3"], ["n-1"], [], 3);
        return new QualifiedGenerationCandidate(generation, slot, slot == "A" ? 22086 : 22087,
            slot == "A" ? 22096 : 22097, config, "n-1", "Нидерланды", pool,
            assets.CoreSha256, assets.RulesManifestSha256, DateTimeOffset.UtcNow.ToString("o"));
    }

    private sealed class NonSeekableFixture(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class TestLease : ICoreLease
    {
        public int ProcessId => 42;
        public bool Disposed { get; private set; }
        public bool HasExited => Disposed;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
