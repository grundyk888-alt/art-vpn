using System.Text;
using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

internal interface ICoreLease : IAsyncDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
}

internal delegate Task<ICoreLease> CoreLaunch(
    string corePath,
    string configPath,
    IReadOnlyCollection<int> expectedPorts,
    CancellationToken cancellationToken);

internal delegate Task CoreConfigCheck(
    string corePath,
    string configPath,
    CancellationToken cancellationToken);

internal delegate VerifiedRuntimeAssets RuntimeAssetsOpen(RuntimeOptions options);

internal sealed class SlotRuntimeManager : IAsyncDisposable
{
    private readonly RuntimeOptions _options;
    private readonly StableFrontHost _front;
    private readonly CoreLaunch _launch;
    private readonly CoreConfigCheck _checkConfig;
    private readonly RuntimeAssetsOpen _openAssets;
    private readonly bool _verifyReferencedRules;
    private readonly ProbeRoundTransport _acceptance;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<int, CancellationToken, Task<bool>> _tcpReady;
    private readonly Func<bool> _automaticAllowed;
    private readonly Func<bool> _throneFallbackAllowed;
    private readonly Func<int> _externalFallbackPort;
    private int _observedExternalPort;
    private readonly Func<int, bool> _hasDirectConnections;
    private readonly Dictionary<string, ICoreLease> _leases = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly WatchdogContinuityGate _watchdogGate = new();
    private ActiveGenerationState? _activeState;
    private ActiveGenerationState? _reserveState;
    private int _activePort;
    private DateTimeOffset _nextActiveRestartAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextDeepProbeAtUtc = DateTimeOffset.MinValue;

    private int ObservedActivePort
    {
        get
        {
            var frontPort = _front.TargetPort;
            return ExternalProxyEndpoint.ForPort(frontPort) is not null ? frontPort : Volatile.Read(ref _activePort);
        }
    }
    public bool HasActiveChannel => ObservedActivePort != 0;

    // A refresh must not spend minutes probing a generation that cannot yet
    // replace its physical slot. This is only an early read-only check; the
    // activation guard below remains authoritative if connections change.
    public async Task<bool> CanStageReplacementAsync(CancellationToken cancellationToken)
    {
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAutomaticOwnership();
            var active = ActiveGenerationStore.TryRead(_options);
            var slot = active?.Slot == "A" ? "B" : "A";
            var port = slot == "A" ? 22086 : 22087;
            return !_leases.ContainsKey(slot) ||
                (_front.ActiveConnectionsFor(port) == 0 && !_hasDirectConnections(port));
        }
        finally { _mutation.Release(); }
    }

    public SlotRuntimeManager(
        RuntimeOptions options,
        StableFrontHost front,
        CoreLaunch? launch = null,
        ProbeRoundTransport? acceptance = null,
        CoreConfigCheck? checkConfig = null,
        RuntimeAssetsOpen? openAssets = null,
        Func<DateTimeOffset>? now = null,
        Func<int, CancellationToken, Task<bool>>? tcpReady = null,
        Func<bool>? automaticAllowed = null,
        Func<bool>? throneFallbackAllowed = null,
        Func<int, bool>? hasDirectConnections = null,
        Func<int>? externalFallbackPort = null)
    {
        _options = options;
        _front = front;
        _launch = launch ?? (async (core, config, ports, token) =>
            await CoreProcessLease.StartAsync(core, config, ports, token).ConfigureAwait(false));
        _acceptance = acceptance ?? LiveProbeTransport.RunAsync;
        _checkConfig = checkConfig ?? CoreProcessLease.CheckConfigAsync;
        _openAssets = openAssets ?? (runtimeOptions => RuntimeAssetVerifier.Open(runtimeOptions));
        _verifyReferencedRules = openAssets is null;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _tcpReady = tcpReady ?? IsTcpReadyAsync;
        _automaticAllowed = automaticAllowed ?? (() => true);
        _throneFallbackAllowed = throneFallbackAllowed ?? (() => true);
        _externalFallbackPort = externalFallbackPort ?? (() => 2080);
        _hasDirectConnections = hasDirectConnections ?? HasDirectConnections;
    }

    public async Task<ActivationReceipt> ActivateAsync(
        QualifiedGenerationCandidate candidate,
        CancellationToken cancellationToken,
        Func<bool>? explicitOwnershipCheck = null)
    {
        void RequireOwnership()
        {
            if (explicitOwnershipCheck is null) RequireAutomaticOwnership();
            else if (!explicitOwnershipCheck()) throw new OperationCanceledException("ManualRouteHasPriority");
        }
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        RequireOwnership();
        ValidateCandidate(candidate);
        if (candidate.ProxyPort == _front.TargetPort || candidate.ProxyPort == _activePort)
            throw new InvalidOperationException("ActiveSlotReplacementRejected");
        if (_leases.TryGetValue(candidate.Slot, out var previousInSlot))
        {
            // Some older clients cached 22086/22087 rather than the permanent
            // front. Counting only front-owned tunnels would kill their live
            // sessions during a third subscription promotion. Fail closed if
            // the OS connection table cannot be inspected.
            if (_front.ActiveConnectionsFor(candidate.ProxyPort) != 0 ||
                _hasDirectConnections(candidate.ProxyPort))
                throw new InvalidOperationException("InactiveSlotStillDraining");
            await previousInSlot.DisposeAsync().ConfigureAwait(false);
            _leases.Remove(candidate.Slot);
        }

        var assets = _openAssets(_options);
        if (_verifyReferencedRules)
            assets = RuntimeAssetVerifier.ForConfig(_options, candidate.ConfigPath, assets);
        if (assets.CoreSha256 != candidate.CoreSha256 || assets.RulesManifestSha256 != candidate.RulesManifestSha256)
            throw new InvalidDataException("ActivationAssetsChanged");
        await _checkConfig(assets.CorePath, candidate.ConfigPath, cancellationToken)
            .ConfigureAwait(false);
        var lease = await _launch(assets.CorePath, candidate.ConfigPath,
            [candidate.ProxyPort, candidate.ApiPort], cancellationToken).ConfigureAwait(false);
        try
        {
            var samples = new[]
            {
                await _acceptance(candidate.ProxyPort, 1_048_576, cancellationToken).ConfigureAwait(false),
                await _acceptance(candidate.ProxyPort, 1_048_576, cancellationToken).ConfigureAwait(false)
            };
            var identity = new ProviderNodeView(candidate.PrimaryTag, candidate.PrimaryTag, candidate.PrimaryCountry,
                "runtime", "sealed-runtime", candidate.ProxyPort, candidate.ProxyPort, new string('a', 32));
            var accepted = NodeQualificationRunner.Summarize(identity, 2, samples);
            if (!accepted.Clean || accepted.EgressIpChanges != 0)
            {
                // Retain bounded, secret-free evidence for installation
                // diagnostics; a rejection must not look like an unexplained
                // timeout. No subscription/server URL or public IP is stored.
                SafeLog.Write(_options.DataRoot, $"ActivationRejected.Clean={accepted.Clean}.EgressChanges={accepted.EgressIpChanges}");
                foreach (var sample in samples)
                    SafeLog.Write(_options.DataRoot, $"ActivationSample.OpenAi={sample.OpenAi}.ChatGpt={sample.ChatGpt}.Telegram={sample.Telegram}.Integrity={sample.Integrity}.RemoteConnect={sample.RemoteConnect}");
                throw new InvalidDataException("InactiveSlotAcceptanceRejected");
            }
            cancellationToken.ThrowIfCancellationRequested();
            RequireOwnership();

            var before = ActiveGenerationStore.TryRead(_options);
            var nextRevision = _front.Revision + 1;
            var active = new ActiveGenerationState(
                1,
                "art-vpn-active-generation",
                candidate.GenerationId,
                candidate.Slot,
                candidate.ProxyPort,
                candidate.ApiPort,
                candidate.ConfigPath,
                candidate.PrimaryTag,
                candidate.PrimaryCountry,
                DateTimeOffset.UtcNow.ToString("o"),
                nextRevision,
                false);
            var receipt = new ActivationReceipt(
                1, "art-vpn-generation-activation", candidate.GenerationId, candidate.Slot,
                candidate.ProxyPort, candidate.PrimaryTag, candidate.PrimaryCountry,
                accepted.LatencyP95Ms, accepted.ThroughputFloorMbps, nextRevision,
                active.ActivatedAtUtc, false, false);
            // All fallible evidence writes precede the live cutover. The
            // authoritative active receipt plus front state define activation;
            // an evidence file alone is never proof of a committed route.
            AtomicFile.ReplaceJson(Path.Combine(Path.GetDirectoryName(candidate.ConfigPath)!, "activation.v1.json"), receipt);
            if (before is not null) AtomicFile.ReplaceJson(PreviousGenerationStore.PathFor(_options), before);
            AtomicFile.ReplaceJson(ActiveGenerationStore.PathFor(_options), active);
            try { _front.SwitchNewConnectionsTo(candidate.ProxyPort, "AcceptedGenerationPromotion"); }
            catch
            {
                RestoreActiveReceipt(before);
                throw;
            }
            if (before is not null)
            {
                _reserveState = before;
            }
            _activeState = active;
            _activePort = active.ProxyPort;
            ResetWatchdogCounters();
            _leases[candidate.Slot] = lease;
            return receipt;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        }
        finally { _mutation.Release(); }
    }

    public async Task<bool> RevertFirstConnectionAsync(string preparedGeneration,
        ActiveGenerationState? previous, int previousTarget, CancellationToken cancellationToken)
    {
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = ActiveGenerationStore.TryRead(_options);
            if (current?.GenerationId != preparedGeneration || _front.TargetPort != current.ProxyPort)
                return false; // A later selection belongs to its newer owner.
            var restored = previous is null ? null : previous with { FrontRevision = _front.Revision + 1 };
            RestoreActiveReceipt(restored);
            try
            {
                if (previousTarget == 0) _front.PauseNewConnections();
                else _front.SwitchNewConnectionsTo(previousTarget, "RejectedFirstConnectionRestored");
            }
            catch { RestoreActiveReceipt(current); throw; }
            _activeState = restored;
            _activePort = previousTarget;
            // Activation may have recycled the older reserve's physical slot.
            // Do not promote either the rejected candidate or a stale receipt.
            _reserveState = null;
            var reservePath = PreviousGenerationStore.PathFor(_options);
            if (File.Exists(reservePath)) File.Delete(reservePath);
            ResetWatchdogCounters();
            // Leases remain until normal draining; do not sever old sessions.
            return true;
        }
        finally { _mutation.Release(); }
    }

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken, bool explicitSelection = false)
    {
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        if (!explicitSelection) RequireAutomaticOwnership();
        var active = ActiveGenerationStore.TryRead(_options);
        if (active is null) return false;
        if (_leases.TryGetValue(active.Slot, out var existing) && !existing.HasExited)
        {
            var current = await _acceptance(active.ProxyPort, 65_536, cancellationToken).ConfigureAwait(false);
            if (!IsHealthy(current)) return false;
            if (!explicitSelection) RequireAutomaticOwnership();
            cancellationToken.ThrowIfCancellationRequested();
            _front.SwitchNewConnectionsTo(active.ProxyPort, "ExistingGenerationResume");
            _activeState = active;
            _activePort = active.ProxyPort;
            ResetWatchdogCounters();
            return true;
        }
        ValidateActivePath(active);
        var assets = _openAssets(_options);
        if (_verifyReferencedRules) assets = RuntimeAssetVerifier.ForConfig(_options, active.ConfigPath, assets);
        await _checkConfig(assets.CorePath, active.ConfigPath, cancellationToken).ConfigureAwait(false);
        var lease = await _launch(assets.CorePath, active.ConfigPath, [active.ProxyPort, active.ApiPort], cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ProbeRoundSample? sample = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                sample = await _acceptance(active.ProxyPort, 262_144, cancellationToken).ConfigureAwait(false);
                if (IsHealthy(sample)) break;
                if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            if (sample is null || !IsHealthy(sample))
                throw new InvalidDataException("RestoredSlotAcceptanceRejected");
            if (!explicitSelection) RequireAutomaticOwnership();
            cancellationToken.ThrowIfCancellationRequested();
            var before = active;
            active = active with { FrontRevision = _front.Revision + 1 };
            AtomicFile.ReplaceJson(ActiveGenerationStore.PathFor(_options), active);
            try { _front.SwitchNewConnectionsTo(active.ProxyPort, "AcceptedGenerationRestore"); }
            catch { RestoreActiveReceipt(before); throw; }
            _leases[active.Slot] = lease;
            _activeState = active;
            _activePort = active.ProxyPort;
            _reserveState = PreviousGenerationStore.TryRead(_options);
            if (_reserveState is not null && _reserveState.Slot != active.Slot)
                await TryRestoreReserveAsync(assets, _reserveState, cancellationToken).ConfigureAwait(false);
            ResetWatchdogCounters();
            return true;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        }
        finally { _mutation.Release(); }
    }

    public async Task RunWatchdogAsync(
        Func<WatchdogDecision, Task> report,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            WatchdogDecision? decision = null;
            await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_automaticAllowed()) continue;
                decision = await ObserveRuntimeOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                decision = new WatchdogDecision("ObservationFailed", ErrorCodes.From(ex), _activePort, 0, 0,
                    false, DateTimeOffset.UtcNow.ToString("o"));
            }
            finally { _mutation.Release(); }
            if (decision is not null)
                await ReportSafelyAsync(report, decision, _options.DataRoot, cancellationToken).ConfigureAwait(false);
        }
    }

    // The same iteration is exercised by the regression harness. The caller
    // holds _mutation, so a test does not bypass production decision ordering.
    internal async Task<WatchdogDecision?> ObserveRuntimeOnceAsync(CancellationToken cancellationToken)
    {
        RequireAutomaticOwnership();
        var decision = await TryRecoverExitedActiveAsync(cancellationToken).ConfigureAwait(false);
        // A failed launch is NOT a usable route. Even when launching took
        // longer than the retry interval, keep qualifying the external reserve.
        // Otherwise every iteration retries the dead core and never reaches
        // failover. A successful recovery can retain its own health result.
        if ((decision is null || decision.Action == "ActiveRestartFailed") && _now() >= _nextDeepProbeAtUtc)
        {
            _nextDeepProbeAtUtc = _now().AddSeconds(30);
            decision = await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        return decision;
    }

    internal static async Task ReportSafelyAsync(Func<WatchdogDecision, Task> report,
        WatchdogDecision decision, string dataRoot, CancellationToken cancellationToken)
    {
        try { await report(decision).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A failed status/receipt write must not silently terminate the
            // background watchdog while the proxy keeps accepting traffic.
            SafeLog.Write(dataRoot, "WatchdogReportFailed:" + ErrorCodes.From(ex));
        }
    }

    /// <summary>
    /// Performs a real point-in-time probe without changing watchdog counters,
    /// selected slots, or the stable-front target.  Operator checks must never
    /// become an accidental failover button.
    /// </summary>
    public async Task<WatchdogDecision> CheckNowAsync(CancellationToken cancellationToken)
    {
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var observedPort = ObservedActivePort;
            if (observedPort == 0)
                return new WatchdogDecision("KeepCurrent", "NoActiveChannel", 0, 0,
                    _watchdogGate.FailureCount, false, now.ToString("o"));

            var active = await _acceptance(observedPort, 65_536, cancellationToken).ConfigureAwait(false);
            var reservePort = _reserveState is not null && _leases.TryGetValue(_reserveState.Slot, out var reserveLease) &&
                              !reserveLease.HasExited
                ? _reserveState.ProxyPort
                : 0;
            return new WatchdogDecision(
                "KeepCurrent",
                IsHealthy(active) ? "ManualCheckHealthy" : "ManualCheckDegradedNoCutover",
                observedPort,
                reservePort,
                _watchdogGate.FailureCount,
                false,
                now.ToString("o"));
        }
        finally { _mutation.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lease in _leases.Values)
            try { await lease.DisposeAsync().ConfigureAwait(false); } catch { }
        _leases.Clear();
        _mutation.Dispose();
    }

    private void RequireAutomaticOwnership()
    {
        if (!_automaticAllowed()) throw new OperationCanceledException("ManualRouteHasPriority");
    }

    internal static async Task<string[]> VerifyExternalFallbackAsync(string root)
    {
        var options = RuntimeOptions.Test(root, "Fixture.ExternalFallback");
        var front = new StableFrontHost(options, restorePersistedTarget: false);
        front.SwitchNewConnectionsTo(22086, "FixtureNative");
        var now = DateTimeOffset.Parse("2026-09-09T00:00:00Z");
        var externalPort = 2080;
        var nativeHealthy = true;
        var enabled = true;
        var sample = new ProbeRoundSample(true, true, true, true, false, 80, 80, 80, 80, 5,
            "fixture-only", "DE", "Passed", false, "Available");
        await using var manager = new SlotRuntimeManager(options, front,
            acceptance: (port, _, _) => Task.FromResult(port != 22086 || nativeHealthy ? sample : sample with { OpenAi = false, ChatGpt = false }),
            now: () => now, tcpReady: (_, _) => Task.FromResult(true),
            automaticAllowed: () => enabled, externalFallbackPort: () => externalPort);
        manager._activePort = 22086;
        var checks = new List<string>();
        void Check(bool passed, string name)
        { if (!passed) throw new InvalidOperationException("ExternalWatchdogInvariantFailed:" + name); checks.Add(name); }
        for (var i = 0; i < 4; i++) { now = now.AddSeconds(15); await manager.CheckOnceAsync(CancellationToken.None); }
        nativeHealthy = false;
        externalPort = 10809;
        for (var i = 0; i < 3; i++) { now = now.AddSeconds(15); await manager.CheckOnceAsync(CancellationToken.None); }
        Check(front.TargetPort == 22086 && manager._watchdogGate.ThronePasses == 3,
            "happ-cannot-inherit-throne-health-proof");
        now = now.AddSeconds(15);
        var decision = await manager.CheckOnceAsync(CancellationToken.None);
        Check(front.TargetPort == 10809 && decision.ActivePort == 10809 && decision.Action == "SwitchNewConnections",
            "happ-fallback-requires-four-own-passes-and-confirmed-failure");
        Check(await manager.TryRecoverExitedActiveAsync(CancellationToken.None) is null,
            "external-happ-is-not-restarted-as-owned-art-core");
        manager._activePort = 0; // Startup fallback can be selected by the controller.
        var beforeCheck = front.Revision;
        var snapshot = await manager.CheckNowAsync(CancellationToken.None);
        Check(manager.HasActiveChannel && snapshot.ActivePort == 10809 && manager._activePort == 0 && front.Revision == beforeCheck,
            "external-startup-front-is-observed-without-mutation");
        await manager.CheckOnceAsync(CancellationToken.None);
        Check(manager._activePort == 10809 && front.Revision == beforeCheck,
            "watchdog-follows-actual-external-front-not-stale-slot");
        enabled = false;
        var revision = front.Revision;
        try { await manager.CheckOnceAsync(CancellationToken.None); throw new InvalidOperationException("ManualChoiceIgnored"); }
        catch (OperationCanceledException) { Check(front.Revision == revision, "manual-choice-blocks-background-switch"); }
        return checks.ToArray();
    }

    // Never kill an owned core with draining sockets, including old clients
    // that connected straight to a backend instead of the permanent front.
    public async Task<int> StopDrainedAsync(CancellationToken cancellationToken,
        Func<int, bool>? hasDirectConnections = null)
    {
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_automaticAllowed()) return _leases.Count;
            hasDirectConnections ??= _hasDirectConnections;
            foreach (var pair in _leases.ToArray())
            {
                var port = pair.Key switch { "A" => 22086, "B" => 22087, "C" => 22088, _ => 0 };
                if (port == 0 || _front.TargetPort == port || _front.ActiveConnectionsFor(port) > 0 ||
                    hasDirectConnections(port)) continue;
                await pair.Value.DisposeAsync().ConfigureAwait(false);
                _leases.Remove(pair.Key);
            }
            if (_leases.Count == 0) _activePort = 0;
            return _leases.Count;
        }
        finally { _mutation.Release(); }
    }

    private async Task<WatchdogDecision> CheckOnceAsync(CancellationToken cancellationToken)
    {
        RequireAutomaticOwnership();
        var observedPort = ObservedActivePort;
        if (ExternalProxyEndpoint.ForPort(observedPort) is not null && _activePort != observedPort)
        {
            _activePort = observedPort;
            ResetWatchdogCounters();
        }
        if (_activePort == 0)
            return new WatchdogDecision("NoActiveChannel", "NoActiveChannel", 0, 0, 0, false,
                DateTimeOffset.UtcNow.ToString("o"));
        var now = _now();
        RequireAutomaticOwnership();
        var active = await _acceptance(_activePort, 65_536, cancellationToken).ConfigureAwait(false);
        var activeHealthy = CanPreserveWorkingOpenAi(active);

        var reservePort = 0;
        if (_reserveState is not null && _leases.TryGetValue(_reserveState.Slot, out var reserveLease) && !reserveLease.HasExited)
        {
            reservePort = _reserveState.ProxyPort;
            var reserve = await _acceptance(reservePort, 65_536, cancellationToken).ConfigureAwait(false);
            _watchdogGate.ObserveReserve(IsHealthy(reserve), reserve.GoogleStatus == "Available");
        }
        else _watchdogGate.ObserveReserve(false);

        var externalPort = _externalFallbackPort();
        // Proof for Throne must never qualify HAPP (or the reverse) after an
        // operator changes the preferred reserve. Four fresh passes are required.
        if (_observedExternalPort != externalPort)
        {
            _watchdogGate.ObserveThrone(false);
            _observedExternalPort = externalPort;
        }
        if (_throneFallbackAllowed() && ExternalProxyEndpoint.ForPort(externalPort) is not null &&
            await _tcpReady(externalPort, cancellationToken).ConfigureAwait(false))
        {
            var throne = await _acceptance(externalPort, 65_536, cancellationToken).ConfigureAwait(false);
            _watchdogGate.ObserveThrone(IsHealthy(throne), throne.GoogleStatus == "Available");
        }
        else _watchdogGate.ObserveThrone(false);

        if (activeHealthy)
        {
            _watchdogGate.ObserveActive(true, now);
            return new WatchdogDecision("KeepCurrent", !IsHealthy(active) ? "CurrentChannelSecondaryDegraded" :
                active.GoogleStatus is "Available" or "NotChecked"
                    ? "CurrentChannelHealthy" : "CurrentChannelBrowserDegraded", _activePort, reservePort,
                _watchdogGate.FailureCount, false, now.ToString("o"));
        }

        if (!_watchdogGate.ObserveActive(false, now))
            return new WatchdogDecision("KeepCurrent", active.FailureClass == "TlsBadRecordMac"
                ? "TlsFailureObservedAndQuarantined" : "FailureThresholdNotReached", _activePort, reservePort,
                _watchdogGate.FailureCount, false, now.ToString("o"));

        if (reservePort != 0 && _watchdogGate.PreferReserve && _reserveState is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireAutomaticOwnership();
            var previous = _activeState;
            var promoted = _reserveState with { FrontRevision = _front.Revision + 1, ActivatedAtUtc = now.ToString("o") };
            if (previous is not null) AtomicFile.ReplaceJson(PreviousGenerationStore.PathFor(_options), previous);
            AtomicFile.ReplaceJson(ActiveGenerationStore.PathFor(_options), promoted);
            try { _front.SwitchNewConnectionsTo(reservePort, "ConfirmedSlotFailure"); }
            catch { RestoreActiveReceipt(previous); throw; }
            _activeState = promoted;
            _reserveState = previous;
            _activePort = reservePort;
            ResetWatchdogCounters();
            return new WatchdogDecision("SwitchNewConnections", "ConfirmedSlotFailure", _activePort,
                previous?.ProxyPort ?? 0, 3, false, now.ToString("o"));
        }
        if (_throneFallbackAllowed() && _externalFallbackPort() == externalPort &&
            ExternalProxyEndpoint.ForPort(externalPort) is not null &&
            _watchdogGate.ThronePasses >= 4 && _activePort != externalPort)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireAutomaticOwnership();
            var reason = externalPort == 2080 ? "ConfirmedSlotFailureThroneFallback" : "ConfirmedSlotFailureHappFallback";
            _front.SwitchNewConnectionsTo(externalPort, reason);
            _activePort = externalPort;
            ResetWatchdogCounters();
            return new WatchdogDecision("SwitchNewConnections", reason, externalPort,
                reservePort, 3, false, now.ToString("o"));
        }
        // HTTP errors/timeouts at remote services do not prove that the live
        // local core is broken. Restarting it would destroy existing sessions
        // without a proven alternative. Exited-process recovery is separate.
        return new WatchdogDecision("KeepCurrent", "NoPrequalifiedReserve", _activePort, reservePort,
            _watchdogGate.FailureCount, false, now.ToString("o"));
    }

    private async Task<WatchdogDecision?> TryRecoverExitedActiveAsync(CancellationToken cancellationToken)
    {
        RequireAutomaticOwnership();
        if (_activeState is null || _activePort == 0 || ExternalProxyEndpoint.ForPort(ObservedActivePort) is not null) return null;
        if (_leases.TryGetValue(_activeState.Slot, out var lease) && !lease.HasExited) return null;
        if (_now() < _nextActiveRestartAtUtc) return null;
        return await RestartActiveAsync("ActiveProcessExited", cancellationToken).ConfigureAwait(false);
    }

    private async Task<WatchdogDecision?> RestartActiveAsync(string trigger, CancellationToken cancellationToken)
    {
        if (_activeState is null || _activePort == 0 || ExternalProxyEndpoint.ForPort(ObservedActivePort) is not null) return null;
        var now = _now();
        if (now < _nextActiveRestartAtUtc) return null;
        _nextActiveRestartAtUtc = now.AddSeconds(15);
        if (_leases.TryGetValue(_activeState.Slot, out var previousLease))
        {
            try { await previousLease.DisposeAsync().ConfigureAwait(false); } catch { }
            _leases.Remove(_activeState.Slot);
        }
        ICoreLease? replacement = null;
        try
        {
            ValidateActivePath(_activeState);
            var assets = _openAssets(_options);
            if (_verifyReferencedRules) assets = RuntimeAssetVerifier.ForConfig(_options, _activeState.ConfigPath, assets);
            await _checkConfig(assets.CorePath, _activeState.ConfigPath, cancellationToken).ConfigureAwait(false);
            replacement = await _launch(assets.CorePath, _activeState.ConfigPath,
                [_activeState.ProxyPort, _activeState.ApiPort], cancellationToken).ConfigureAwait(false);
            var sample = await _acceptance(_activeState.ProxyPort, 262_144, cancellationToken).ConfigureAwait(false);
            if (!IsHealthy(sample)) throw new InvalidDataException("RestartedActiveAcceptanceRejected");
            RequireAutomaticOwnership();
            _leases[_activeState.Slot] = replacement;
            replacement = null;
            _nextActiveRestartAtUtc = DateTimeOffset.MinValue;
            ResetWatchdogCounters();
            return new WatchdogDecision("RestartActive", "ActiveProcessRecovered", _activePort,
                _reserveState?.ProxyPort ?? 0, 0, true, DateTimeOffset.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            if (replacement is not null)
                try { await replacement.DisposeAsync().ConfigureAwait(false); } catch { }
            // Back off from completion, not from the beginning of a potentially
            // slow failed launch. Manual selection still wins via ownership.
            _nextActiveRestartAtUtc = _now().AddSeconds(15);
            var code = ErrorCodes.From(ex);
            SafeLog.Write(_options.DataRoot, code);
            return new WatchdogDecision("ActiveRestartFailed", trigger + ":" + code, _activePort,
                _reserveState?.ProxyPort ?? 0, _watchdogGate.FailureCount, true,
                DateTimeOffset.UtcNow.ToString("o"));
        }
    }

    internal async Task<WatchdogDecision?> RecoverExitedActiveForTestAsync(
        ActiveGenerationState active,
        ICoreLease exitedLease,
        CancellationToken cancellationToken)
    {
        _activeState = active;
        _activePort = active.ProxyPort;
        _leases[active.Slot] = exitedLease;
        return await TryRecoverExitedActiveAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task<WatchdogDecision> CheckActiveForTestAsync(ActiveGenerationState active, ICoreLease lease)
    {
        _activeState = active;
        _activePort = active.ProxyPort;
        _leases[active.Slot] = lease;
        return CheckOnceAsync(CancellationToken.None);
    }

    private async Task TryRestoreReserveAsync(
        VerifiedRuntimeAssets assets,
        ActiveGenerationState reserve,
        CancellationToken cancellationToken)
    {
        ICoreLease? pending = null;
        try
        {
            if (_leases.TryGetValue(reserve.Slot, out var existing))
            {
                if (!existing.HasExited)
                {
                    var current = await _acceptance(reserve.ProxyPort, 65_536, cancellationToken).ConfigureAwait(false);
                    _watchdogGate.ObserveReserve(IsHealthy(current));
                    return; // A live owner is never replaced merely to resume Auto.
                }
                await existing.DisposeAsync().ConfigureAwait(false);
                _leases.Remove(reserve.Slot);
            }
            ValidateActivePath(reserve);
            if (_verifyReferencedRules) assets = RuntimeAssetVerifier.ForConfig(_options, reserve.ConfigPath, assets);
            await _checkConfig(assets.CorePath, reserve.ConfigPath, cancellationToken).ConfigureAwait(false);
            pending = await _launch(assets.CorePath, reserve.ConfigPath, [reserve.ProxyPort, reserve.ApiPort], cancellationToken)
                .ConfigureAwait(false);
            var sample = await _acceptance(reserve.ProxyPort, 262_144, cancellationToken).ConfigureAwait(false);
            if (!IsHealthy(sample)) return;
            _leases[reserve.Slot] = pending;
            pending = null;
            _watchdogGate.ObserveReserve(true);
        }
        catch { }
        finally { if (pending is not null) await pending.DisposeAsync().ConfigureAwait(false); }
    }

    private static bool IsHealthy(ProbeRoundSample sample) =>
        sample.OpenAi && sample.ChatGpt && sample.Telegram && sample.Integrity &&
        !sample.HardDegradation && sample.FailureClass == "Passed";

    internal static bool CanPreserveWorkingOpenAi(ProbeRoundSample sample) =>
        sample.OpenAi && sample.ChatGpt &&
        sample.FailureClass is not ("TlsBadRecordMac" or "TlsIntegrityFailure");

    private static async Task<bool> IsTcpReadyAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(350));
        using var client = new System.Net.Sockets.TcpClient();
        try { await client.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false); return true; }
        catch { return false; }
    }

    private static bool HasDirectConnections(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpConnections().Any(c => c.LocalEndPoint.Port == port &&
                c.State is System.Net.NetworkInformation.TcpState.Established or
                    System.Net.NetworkInformation.TcpState.CloseWait);

    private void ResetWatchdogCounters()
    {
        _watchdogGate.Reset();
    }

    private void RestoreActiveReceipt(ActiveGenerationState? previous)
    {
        var path = ActiveGenerationStore.PathFor(_options);
        if (previous is not null) AtomicFile.ReplaceJson(path, previous);
        else if (File.Exists(path)) File.Delete(path);
    }

    private void ValidateCandidate(QualifiedGenerationCandidate candidate)
    {
        if (candidate.Slot is not ("A" or "B") ||
            candidate.ProxyPort != (candidate.Slot == "A" ? 22086 : 22087) ||
            candidate.ApiPort != (candidate.Slot == "A" ? 22096 : 22097) ||
            candidate.Pool.PoolTags.Length < 1 || candidate.PrimaryTag != candidate.Pool.PrimaryTag)
            throw new InvalidDataException("ActivationCandidateRejected");
        ValidateConfigPath(candidate.GenerationId, candidate.ConfigPath);
    }

    private void ValidateActivePath(ActiveGenerationState active) => ValidateConfigPath(active.GenerationId, active.ConfigPath);

    private void ValidateConfigPath(string generationId, string configPath)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(generationId,
                "^[0-9]{8}T[0-9]{6}Z-[a-f0-9]{12}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("ActivationGenerationRejected");
        var generationRoot = Path.GetFullPath(Path.Combine(_options.PrivateGenerationRoot, generationId))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(configPath);
        if (!resolved.StartsWith(generationRoot, StringComparison.OrdinalIgnoreCase) ||
            !resolved.EndsWith("\\config.json", StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
            throw new InvalidDataException("ActivationConfigPathRejected");
    }
}

internal sealed record ActivationReceipt(
    int Schema,
    string Kind,
    string GenerationId,
    string Slot,
    int ProxyPort,
    string PrimaryTag,
    string PrimaryCountry,
    double LatencyP95Ms,
    double ThroughputFloorMbps,
    long FrontRevision,
    string ActivatedAtUtc,
    bool ExistingConnectionsInterrupted,
    bool ContainsProviderSecret);

internal static class PreviousGenerationStore
{
    public static string PathFor(RuntimeOptions options) => Path.Combine(options.DataRoot, "state", "previous-generation.v1.json");

    public static ActiveGenerationState? TryRead(RuntimeOptions options)
    {
        var path = PathFor(options);
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<ActiveGenerationState>(File.ReadAllText(path, Encoding.UTF8), JsonSettings.Strict);
            if (state is null || state.Schema != 1 || state.Kind != "art-vpn-active-generation" ||
                state.Slot is not ("A" or "B") || state.ProxyPort != (state.Slot == "A" ? 22086 : 22087) ||
                state.ApiPort != (state.Slot == "A" ? 22096 : 22097) || state.ContainsProviderSecret)
                return null;
            return state;
        }
        catch { return null; }
    }
}

internal sealed record WatchdogDecision(
    string Action,
    string ReasonCode,
    int ActivePort,
    int ReservePort,
    int FailureCount,
    bool ExistingConnectionsInterrupted,
    string AtUtc);

internal static class SlotRuntimeManagerTests
{
    public static async Task<SlotRuntimeManagerTestReceipt> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-slot-test-" + Guid.NewGuid().ToString("N"));
        var options = RuntimeOptions.Test(root, "ARTSPORT.ARTVpn.SlotTest." + Guid.NewGuid().ToString("N"));
        var frontPort = FreePort();
        var apiPort = FreePort([frontPort]);
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var front = new StableFrontHost(options, frontPort, apiPort, [22086, 22087]);
        var frontTask = front.RunAsync(stopping.Token);
        var fakeLease = new FakeCoreLease();
        try
        {
            Directory.CreateDirectory(options.RuntimeRoot);
            // The manager's asset verification is separately covered; this
            // test uses a candidate-specific synthetic runtime root and calls
            // the seam below after preparing a valid fixture asset set.
            PrepareFixtureAssets(options);
            var assets = RuntimeAssetVerifier.Open(options,
                Hash(Path.Combine(options.RuntimeRoot, "ARTVpnCore.exe")),
                new FileInfo(Path.Combine(options.RuntimeRoot, "ARTVpnCore.exe")).Length);
            var generationId = "20260831T120000Z-abcdef123456";
            var configRoot = Path.Combine(options.PrivateGenerationRoot, generationId, "runtime", "a");
            Directory.CreateDirectory(configRoot);
            var config = Path.Combine(configRoot, "config.json");
            File.WriteAllText(config, "{}", Encoding.UTF8);
            var evidence = new NodeProbeEvidence("n-1", new string('a',32), "Нидерланды", "server", 443,
                true,false,6,6,100,100,100,100,100,true,0,70,80,5,20,100,"NL","Passed");
            var pool = new QualifiedPool("n-1", ["n-1","n-2","n-3"], ["n-1"], [evidence], 3);
            var candidate = new QualifiedGenerationCandidate(generationId,"A",22086,22096,config,"n-1","Нидерланды",
                pool,assets.CoreSha256,assets.RulesManifestSha256,DateTimeOffset.UtcNow.ToString("o"));
            // Core config check cannot run against a synthetic executable, so
            // the deterministic manager test validates the state transition
            // through its extracted commit seam.
            var committed = SlotActivationCommit.CommitForTest(options, front, candidate, 80, 20);
            Assert(committed.Slot == "A" && front.TargetPort == 22086 && !committed.ExistingConnectionsInterrupted);
            var active = ActiveGenerationStore.TryRead(options);
            Assert(active?.GenerationId == generationId && active.FrontRevision == front.Revision);
            await using (var rollback = new SlotRuntimeManager(options, front))
            {
                var later = active! with { GenerationId = "20260831T130000Z-abcdef123456", Slot = "B", ProxyPort = 22087, ApiPort = 22097 };
                AtomicFile.ReplaceJson(ActiveGenerationStore.PathFor(options), later);
                front.SwitchNewConnectionsTo(22087, "FirstConnectionPostflightFailureFixture");
                Assert(await rollback.RevertFirstConnectionAsync(later.GenerationId, active, 22086, CancellationToken.None));
                Assert(front.TargetPort == 22086 && ActiveGenerationStore.TryRead(options)?.GenerationId == active!.GenerationId);
                Assert(!await rollback.RevertFirstConnectionAsync(later.GenerationId, null, 0, CancellationToken.None));
                Assert(await rollback.RevertFirstConnectionAsync(active!.GenerationId, null, 0, CancellationToken.None));
                Assert(front.TargetPort == 0 && ActiveGenerationStore.TryRead(options) is null);
                AtomicFile.ReplaceJson(ActiveGenerationStore.PathFor(options), active);
                front.SwitchNewConnectionsTo(22086, "RestoreFixtureAfterRollbackTests");
            }
            await using (var emptyManager = new SlotRuntimeManager(options, front,
                             (_, _, _, _) => Task.FromResult<ICoreLease>(new FakeCoreLease()),
                             (_, _, _) => Task.FromResult(new ProbeRoundSample(
                                 true, true, true, true, false, 1, 1, 1, 1, 10, "ip", "NL", "Passed", false))))
            {
                var manual = await emptyManager.CheckNowAsync(CancellationToken.None);
                Assert(manual.ReasonCode == "NoActiveChannel" && !manual.ExistingConnectionsInterrupted);
            }
            front.SwitchNewConnectionsTo(22086, "TestActiveProcessRecovery");
            var activeForRecovery = new ActiveGenerationState(1, "art-vpn-active-generation", generationId,
                "A", 22086, 22096, config, "n-1", "Нидерланды", DateTimeOffset.UtcNow.ToString("o"),
                front.Revision, false);
            AtomicFile.ReplaceJson(ActiveGenerationStore.PathFor(options), activeForRecovery);
            var launches = 0;
            var exited = new FakeCoreLease(true);
            await using (var recoveryManager = new SlotRuntimeManager(options, front,
                             (_, _, _, _) =>
                             {
                                 launches++;
                                 return Task.FromResult<ICoreLease>(new FakeCoreLease());
                             },
                             (_, _, _) => Task.FromResult(new ProbeRoundSample(
                                 true, true, true, true, false, 1, 1, 1, 1, 10, "ip", "NL", "Passed", false)),
                             (_, _, _) => Task.CompletedTask,
                             fixtureOptions => RuntimeAssetVerifier.Open(fixtureOptions,
                                 Hash(Path.Combine(fixtureOptions.RuntimeRoot, "ARTVpnCore.exe")),
                                 new FileInfo(Path.Combine(fixtureOptions.RuntimeRoot, "ARTVpnCore.exe")).Length)))
            {
                var beforeRevision = front.Revision;
                var recovered = await recoveryManager.RecoverExitedActiveForTestAsync(
                    activeForRecovery, exited, CancellationToken.None);
                if (recovered?.Action != "RestartActive" || recovered.ReasonCode != "ActiveProcessRecovered")
                    throw new InvalidOperationException("SlotRuntimeManagerRecoveryDecisionFailed:" +
                                                        recovered?.Action + ":" + recovered?.ReasonCode);
                if (launches != 1 || !exited.Disposed || front.Revision != beforeRevision)
                    throw new InvalidOperationException($"SlotRuntimeManagerRecoveryInvariantFailed:{launches}:{exited.Disposed}:{front.Revision}:{beforeRevision}");
            }
            // A slow failed process restart must not starve reserve checks.
            // Use the actual production iteration and advance only its clock;
            // no network, Windows proxy, or installed process is changed.
            var recoveryClock = DateTimeOffset.UtcNow;
            var externalChecks = 0;
            var recoveryEnabled = true;
            front.SwitchNewConnectionsTo(22086, "TestFailedRuntimeFallback");
            var fallbackFront = new StableFrontHost(options, frontPort, apiPort,
                [22086, 2080], restorePersistedTarget: false);
            fallbackFront.SwitchNewConnectionsTo(22086, "TestFailedRuntimeFallback");
            await using (var failedRecovery = new SlotRuntimeManager(options, fallbackFront,
                (_, _, _, _) =>
                {
                    recoveryClock = recoveryClock.AddSeconds(20);
                    throw new IOException("FixtureSlowProcessStartFailure");
                },
                (port, _, _) =>
                {
                    var ok = port == 2080;
                    if (ok) externalChecks++;
                    return Task.FromResult(new ProbeRoundSample(ok, ok, ok, ok, false,
                        80, 80, 80, 80, 5, "fixture", "DE", ok ? "Passed" : "Timeout", !ok));
                },
                (_, _, _) => Task.CompletedTask,
                _ => assets, now: () => recoveryClock,
                tcpReady: (_, _) => Task.FromResult(true),
                automaticAllowed: () => recoveryEnabled))
            {
                await failedRecovery.RecoverExitedActiveForTestAsync(activeForRecovery,
                    new FakeCoreLease(true), CancellationToken.None);
                for (var i = 0; i < 4; i++)
                {
                    recoveryClock = recoveryClock.AddSeconds(30);
                    await failedRecovery.ObserveRuntimeOnceAsync(CancellationToken.None);
                }
                if (externalChecks < 4 || fallbackFront.TargetPort != 2080)
                    throw new InvalidOperationException("VPN20260912SlowFailedRestartStarvesVerifiedFallback");
                var revision = fallbackFront.Revision;
                recoveryEnabled = false;
                try { await failedRecovery.ObserveRuntimeOnceAsync(CancellationToken.None); }
                catch (OperationCanceledException) { }
                Assert(fallbackFront.Revision == revision);
            }
            // Execute the real watchdog branch, with isolated ports and no
            // probes against the user's Throne. Three failures must not kill
            // a still-running core when every reserve is unavailable.
            var observedAt = DateTimeOffset.UtcNow;
            var sample = new ProbeRoundSample(false,false,false,false,false,
                10,10,10,10,0,"","","Timeout",true);
            var alive = new FakeCoreLease();
            await using (var continuityManager = new SlotRuntimeManager(options, front,
                (_,_,_,_) => throw new InvalidOperationException("UnexpectedLiveCoreRestart"),
                (_,_,_) => Task.FromResult(sample), now: () => observedAt,
                tcpReady: (_,_) => Task.FromResult(false)))
            {
                var revision = front.Revision;
                WatchdogDecision? decision = null;
                foreach (var i in Enumerable.Range(0, 4))
                {
                    decision = await continuityManager.CheckActiveForTestAsync(activeForRecovery, alive);
                    observedAt = observedAt.AddSeconds(30);
                }
                Assert(decision?.ReasonCode == "NoPrequalifiedReserve" && !alive.Disposed && front.Revision == revision);
                sample = sample with {OpenAi=true, ChatGpt=true};
                decision = await continuityManager.CheckActiveForTestAsync(activeForRecovery, alive);
                Assert(decision.ReasonCode == "CurrentChannelSecondaryDegraded" && decision.FailureCount == 0 && !alive.Disposed);
                sample = sample with {FailureClass="TlsIntegrityFailure"};
                Assert(!CanPreserveWorkingOpenAiForTest(sample));
            }
            // Run the actual activation path, not only the commit seam. A
            // legacy direct-backend session must veto replacing its owned core
            // even though the permanent front has zero tunnels for that port.
            var directHeld = false;
            var directInspectionFailed = false;
            var owned = new List<FakeCoreLease>();
            front.SwitchNewConnectionsTo(22087, "TestDirectDrainInitial");
            await using (var drainManager = new SlotRuntimeManager(options, front,
                (_, _, _, _) =>
                {
                    var lease = new FakeCoreLease();
                    owned.Add(lease);
                    return Task.FromResult<ICoreLease>(lease);
                },
                (_, _, _) => Task.FromResult(new ProbeRoundSample(
                    true, true, true, true, false, 80, 80, 80, 80, 20, "ip", "NL", "Passed", false)),
                (_, _, _) => Task.CompletedTask,
                _ => assets,
                hasDirectConnections: port => directInspectionFailed
                    ? throw new IOException("TestConnectionTableUnavailable") : directHeld && port == 22086))
            {
                await drainManager.ActivateAsync(candidate, CancellationToken.None);
                var bRoot = Path.Combine(options.PrivateGenerationRoot, generationId, "runtime", "b");
                Directory.CreateDirectory(bRoot);
                var bPath = Path.Combine(bRoot, "config.json");
                File.WriteAllText(bPath, "{}", Encoding.UTF8);
                var nextCandidate = candidate with { Slot = "B", ProxyPort = 22087, ApiPort = 22097, ConfigPath = bPath };
                await drainManager.ActivateAsync(nextCandidate, CancellationToken.None);
                Assert(await drainManager.CanStageReplacementAsync(CancellationToken.None));
                directHeld = true;
                var revision = front.Revision;
                Assert(!await drainManager.CanStageReplacementAsync(CancellationToken.None) &&
                       owned.Count == 2 && !owned[0].Disposed && front.Revision == revision);
                try
                {
                    await drainManager.ActivateAsync(candidate, CancellationToken.None);
                    throw new InvalidDataException("DirectDrainGuardDidNotReject");
                }
                catch (InvalidOperationException ex) when (ex.Message == "InactiveSlotStillDraining") { }
                Assert(!owned[0].Disposed && !owned[1].Disposed && owned.Count == 2 &&
                       front.Revision == revision && front.TargetPort == 22087);
                directHeld = false;
                directInspectionFailed = true;
                try
                {
                    await drainManager.CanStageReplacementAsync(CancellationToken.None);
                    throw new InvalidDataException("UnknownDrainPreflightDidNotReject");
                }
                catch (IOException ex) when (ex.Message == "TestConnectionTableUnavailable") { }
                Assert(!owned[0].Disposed && front.Revision == revision);
                try
                {
                    await drainManager.ActivateAsync(candidate, CancellationToken.None);
                    throw new InvalidDataException("UnknownDirectDrainDidNotReject");
                }
                catch (IOException ex) when (ex.Message == "TestConnectionTableUnavailable") { }
                Assert(!owned[0].Disposed && front.Revision == revision && owned.Count == 2);
                directInspectionFailed = false;
                Assert(await drainManager.CanStageReplacementAsync(CancellationToken.None));
                await drainManager.ActivateAsync(candidate, CancellationToken.None);
                Assert(owned[0].Disposed && !owned[1].Disposed && owned.Count == 3 && front.TargetPort == 22086);
            }
            return new SlotRuntimeManagerTestReceipt("Passed", 31, true, true, true, false, false);
        }
        finally
        {
            await fakeLease.DisposeAsync();
            stopping.Cancel();
            try { await frontTask.ConfigureAwait(false); } catch { }
            var resolved = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("art-vpn-slot-test-", StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static void PrepareFixtureAssets(RuntimeOptions options)
    {
        Directory.CreateDirectory(Path.Combine(options.RuntimeRoot, "rules"));
        File.WriteAllBytes(Path.Combine(options.RuntimeRoot, "ARTVpnCore.exe"), Encoding.ASCII.GetBytes("fixture-core"));
        var entries = new List<object>();
        foreach (var pair in new[]
                 {
                     ("art-geoip-ru", "geoip.srs"), ("art-geosite-category-ru", "category.srs"),
                     ("art-geosite-ru", "ru.srs"), ("art-geosite-ru-inside", "inside.srs")
                 })
        {
            var path = Path.Combine(options.RuntimeRoot, "rules", pair.Item2);
            File.WriteAllBytes(path, [(byte)'S',(byte)'R',(byte)'S',1,1,2,3]);
            entries.Add(new { tag=pair.Item1,repository="fixture/repo",branch="main",path="fixture",git_blob=new string('a',40),
                installed_name=pair.Item2,bytes=7,sha256=Hash(path) });
        }
        File.WriteAllText(Path.Combine(options.RuntimeRoot,"rules","manifest.json"),JsonSerializer.Serialize(new
        {
            source="fixture",policy="immutable and hash-pinned; replacement requires isolated candidate validation",files=entries
        },JsonSettings.Output),Encoding.UTF8);
    }

    private static string Hash(string path)
    {
        using var stream=File.OpenRead(path);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static int FreePort(IEnumerable<int>? excluded=null)
    {
        var denied=new HashSet<int>(excluded??[]);
        while(true){var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
            var port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();if(!denied.Contains(port))return port;}
    }

    private static void Assert(bool condition){if(!condition)throw new InvalidOperationException("SlotRuntimeManagerInvariantFailed");}

    private static bool CanPreserveWorkingOpenAiForTest(ProbeRoundSample sample) =>
        SlotRuntimeManager.CanPreserveWorkingOpenAi(sample);

    private sealed class FakeCoreLease(bool exited = false) : ICoreLease
    {
        public bool Disposed { get; private set; }
        public int ProcessId=>42;
        public bool HasExited=>exited || Disposed;
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
}

internal static class SlotActivationCommit
{
    internal static ActivationReceipt CommitForTest(RuntimeOptions options, StableFrontHost front,
        QualifiedGenerationCandidate candidate,double latency,double throughput)
    {
        var next=front.Revision+1;
        var active=new ActiveGenerationState(1,"art-vpn-active-generation",candidate.GenerationId,candidate.Slot,
            candidate.ProxyPort,candidate.ApiPort,candidate.ConfigPath,candidate.PrimaryTag,candidate.PrimaryCountry,
            DateTimeOffset.UtcNow.ToString("o"),next,false);
        AtomicFile.ReplaceJson(ActiveGenerationStore.PathFor(options),active);
        front.SwitchNewConnectionsTo(candidate.ProxyPort,"AcceptedGenerationPromotion");
        if(front.Revision!=next)throw new InvalidDataException("FrontRevisionRejected");
        return new ActivationReceipt(1,"art-vpn-generation-activation",candidate.GenerationId,candidate.Slot,candidate.ProxyPort,
            candidate.PrimaryTag,candidate.PrimaryCountry,latency,throughput,front.Revision,active.ActivatedAtUtc,false,false);
    }
}

internal sealed record SlotRuntimeManagerTestReceipt(string Status,int Scenarios,bool AtomicCommit,bool FrontSwitched,
    bool ActiveReceiptBound,bool ExistingConnectionsInterrupted,bool SecretDisplayed);
