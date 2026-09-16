using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

// This is control-plane state, not a health result. Only this controller writes
// an explicit route choice. A UI label or a responding local port is not proof.
internal sealed record RouteModeState(int Schema, string Kind, string RequestId,
    string Mode, string Phase, SystemProxySnapshot Expected, string AtUtc,
    string PreviousMode, SystemProxySnapshot? PreviousExpected, bool ContainsProviderSecret,
    string ReserveClient = "Throne");

internal sealed record RouteModeResult(string Status, string DetailCode, string Mode, bool Changed);

internal sealed class RouteModeControl
{
    private readonly RuntimeOptions _options;
    private readonly string _owner;
    private readonly ISystemProxyStore _store;
    private readonly Func<int, CancellationToken, Task<bool>> _probe;
    private readonly Action<string, RouteModeState> _writeState;
    private readonly Func<string?> _externalTunnel;
    private readonly IHappRouteSession? _happ;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private volatile RouteModeState? _state;
    private int _pending;
    private string StatePath => Path.Combine(_options.DataRoot, "state", "route-mode.v1.json");

    public RouteModeControl(RuntimeOptions options, string owner, ISystemProxyStore store,
        Func<int, CancellationToken, Task<bool>> probe,
        Action<string, RouteModeState>? writeState = null, Func<string?>? externalTunnel = null,
        IHappRouteSession? happ = null)
    {
        _options = options;
        _owner = SystemProxyLeaseCoordinator.ValidateOwnerSid(owner);
        _store = store;
        _probe = probe;
        _writeState = writeState ?? ((path, value) => AtomicFile.ReplaceJson(path, value));
        _externalTunnel = externalTunnel ?? (() => null);
        _happ = happ;
    }

    public string Mode => Current.Mode;
    public ExternalProxyEndpoint? SelectedExternal => ExternalProxyEndpoint.ForMode(Mode) ??
        (Mode == "Auto" && Current.Expected.ProxyEnable == 1
            ? ExternalProxyEndpoint.ForPort(ExternalProxyEndpoint.KnownProxyPort(Current.Expected.ProxyServer)) : null);
    public int ExternalReservePort => (ExternalProxyEndpoint.ForMode(Current.ReserveClient) ?? ExternalProxyEndpoint.Throne).Port;
    public bool ManualRequestPending => Volatile.Read(ref _pending) != 0;
    public RouteModeState Current => _state ??= Load();
    public bool AllowsBackgroundMutation
    {
        get
        {
            if (ManualRequestPending) return false;
            try
            {
                var state = Current;
                return state.Phase == "Committed" && state.Mode is "Auto" or "ArtVpn" or "Standby" &&
                    _store.Read() == state.Expected && SelectedExternal is null && _externalTunnel() is null;
            }
            catch { return false; }
        }
    }

    private RouteModeState NewState(string mode, SystemProxySnapshot current, string requestId = "") =>
        new(1, "art-vpn-route-mode", requestId, mode, "Committed", current,
            DateTimeOffset.UtcNow.ToString("o"), "", null, false,
            _state?.ReserveClient ?? "Throne");

    private RouteModeState Load()
    {
        var observed = _store.Read();
        SystemProxyLeaseCoordinator.ValidateSnapshot(observed);
        var lease = ReadLease();
        if (!File.Exists(StatePath))
        {
            // Upgrade from the older native service: retain Auto only with
            // matching, committed ownership. Never infer it from status.json.
            return NewState(lease is { Phase: "Applied" } && observed == lease.Applied &&
                SystemProxyLeaseCoordinator.IsArtVpnEndpoint(observed) ? "Auto" : "Standby", observed);
        }
        RouteModeState value;
        try
        {
            value = JsonSerializer.Deserialize<RouteModeState>(File.ReadAllText(StatePath), JsonSettings.Strict)
                    ?? throw new InvalidDataException("RouteModeEmpty");
            if (value.Schema != 1 || value.Kind != "art-vpn-route-mode" || value.ContainsProviderSecret ||
                value.Mode is not ("Auto" or "ArtVpn" or "Throne" or "Happ" or "External" or "Standby") ||
                ExternalProxyEndpoint.ForMode(value.ReserveClient) is null ||
                value.Phase is not ("Committed" or "Prepared") ||
                !DateTimeOffset.TryParse(value.AtUtc, out _))
                throw new InvalidDataException("RouteModeRejected");
            SystemProxyLeaseCoordinator.ValidateSnapshot(value.Expected);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            // Corrupt control state is not permission to seize the proxy.
            SafeLog.Write(_options.DataRoot, "RouteModeStateRejected");
            return NewState("External", observed);
        }
        if (value.Phase == "Prepared")
        {
            if (lease is { Phase: "Applied" } && observed == lease.Applied &&
                observed == value.Expected && DateTimeOffset.Parse(lease.AppliedAtUtc) >= DateTimeOffset.Parse(value.AtUtc))
                value = value with { Phase = "Committed", PreviousMode = "", PreviousExpected = null,
                    ReserveClient = ExternalProxyEndpoint.ForMode(value.Mode)?.Mode ?? value.ReserveClient };
            else if (value.PreviousExpected == observed && value.PreviousMode is "Auto" or "ArtVpn" or "Throne" or "Happ" or "Standby" or "External")
                value = NewState(value.PreviousMode, observed, value.RequestId) with { ReserveClient = value.ReserveClient };
            else value = NewState("External", observed, value.RequestId);
            _writeState(StatePath, value); // Recovery never changes the network.
        }
        return value.Expected == observed ? value : NewState("External", observed, value.RequestId);
    }

    private SystemProxyLease? ReadLease()
    {
        var lease = SystemProxyLeaseCoordinator.TryReadLease(_options.SystemProxyLeasePath);
        if (lease is not null) SystemProxyLeaseCoordinator.ValidateLease(lease, _owner);
        return lease;
    }

    public bool ObserveExternalChoice()
    {
        if (ManualRequestPending) return false;
        var state = Current;
        var actual = _store.Read();
        if (state.Expected == actual) return false;
        var next = NewState("External", actual, state.RequestId);
        _state = next; // Fail closed even if persisting fails.
        _writeState(StatePath, next);
        return true;
    }

    public async Task<RouteModeResult> SelectAsync(string requestId, string mode,
        Func<CancellationToken, Task<bool>> prepareArt, CancellationToken cancellationToken,
        bool rememberObservedReserve = false, bool automaticFallback = false)
    {
        if (mode is not ("Auto" or "ArtVpn" or "Throne" or "Happ"))
            return new("Rejected", "ControllerModeRejected", Mode, false);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _pending, 1);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            // OS-held lock cannot remain held after a crash. Do not delete it
            // based on its existence or PID text.
            using var held = new FileStream(Path.Combine(_options.DataRoot, "state", "route-control.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var previousState = Current;
            var before = _store.Read();
            var previousLease = ReadLease();
            if (automaticFallback && (previousState.Mode != "Auto" || previousState.Expected != before))
                return new("Rejected", "ManualChoicePreserved", previousState.Mode, false);
            if (!string.IsNullOrWhiteSpace(before.AutoConfigUrl))
                return new("Rejected", "SystemProxyPacConflict", previousState.Mode, false);
            var releaseHapp = mode is "Auto" or "ArtVpn" or "Throne" && _happ is { Available: true, OwnsTunnel: true };
            if (mode is "Auto" or "ArtVpn" && _externalTunnel() is { } tunnelCode && !releaseHapp)
                return new("Rejected", tunnelCode, previousState.Mode, false);
            var external = ExternalProxyEndpoint.ForMode(mode);
            var targetPort = external?.Port ?? 22080;
            var expected = before with
            {
                ProxyEnable = 1, ProxyServer = $"127.0.0.1:{targetPort}",
                ProxyOverride = external is not null ? before.ProxyOverride : SystemProxyLeaseCoordinator.MergeBypass(before.ProxyOverride)
            };
            var pending = NewState(mode, expected, requestId) with
            {
                Phase = "Prepared", PreviousMode = previousState.Mode, PreviousExpected = before
            };
            _writeState(StatePath, pending);
            _state = pending;
            DecisionJournal.Write(_options.DataRoot, requestId, mode, "Prepared");
            var wroteLease = false;
            var changed = false;
            var baseline = before;
            var handover = _happ is null ? null : new HappRouteHandover(_options, _store, _happ);
            try
            {
                // An already healthy proxy-mode HAPP does not need a native
                // reconnect (some clients ignore connect while already on).
                var startHapp = mode == "Happ" && _happ is { Available: true, OwnsTunnel: false } &&
                    !await _probe(10809, cancellationToken).ConfigureAwait(false);
                if (releaseHapp || startHapp)
                {
                    baseline = await handover!.ChangeAsync(requestId, mode == "Happ", before, cancellationToken).ConfigureAwait(false);
                    changed = baseline != before;
                }
                if (external is null && !await prepareArt(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("StableFrontNotReady");
                var reserve = external?.Mode ?? previousState.ReserveClient;
                if (handover?.Released == true) reserve = "Happ";
                // First-run handover remembers an already working external
                // endpoint only after a real probe, never merely an open port.
                var observedReserve = before.ProxyEnable == 1
                    ? ExternalProxyEndpoint.ForPort(ExternalProxyEndpoint.KnownProxyPort(before.ProxyServer)) : null;
                if (rememberObservedReserve && mode == "Auto" && observedReserve is not null &&
                    await _probe(observedReserve.Port, cancellationToken).ConfigureAwait(false))
                    reserve = observedReserve.Mode;
                var preflightReady = handover?.CanRetryStart == true
                    ? await HappWarmupRetry.ConfirmAsync(
                        (attempt, token) => ProbeForSelectionAsync(targetPort, true, token, attempt == 1 ? 30 : 45),
                        async token => { baseline = await handover.RetryFailedStartAsync(token).ConfigureAwait(false); },
                        cancellationToken).ConfigureAwait(false)
                    : await ProbeForSelectionAsync(targetPort, handover?.Started == true, cancellationToken).ConfigureAwait(false);
                if (!preflightReady)
                    throw new InvalidOperationException("SelectedRoutePreflightFailed");
                if (_store.Read() != baseline) throw new InvalidOperationException("SystemProxyChangedDuringCheck");
                if (external is null && _externalTunnel() is { } appearedTunnel)
                    throw new InvalidOperationException(appearedTunnel);
                // A bypass-only edit does not make our own endpoint a valid
                // pre-install fallback: retain the actual pre-ART endpoint.
                var original = previousLease is null ? before : previousLease.Applied == before
                    ? previousLease.Previous
                    : SystemProxyLeaseCoordinator.RestoreAfterBypassOnlyChange(previousLease, before) ?? before;
                var lease = new SystemProxyLease(1, "art-vpn-system-proxy-lease", _owner, "Prepared",
                    original, expected, pending.AtUtc, "", false);
                SystemProxyLeaseCoordinator.WriteLease(_options.SystemProxyLeasePath, lease);
                wroteLease = true;
                if (_store.Read() != baseline) throw new InvalidOperationException("SystemProxyChangedBeforeCommit");
                cancellationToken.ThrowIfCancellationRequested();
                if (baseline != expected)
                {
                    _store.Write(expected);
                    changed = true;
                    if (_store.Read() != expected) throw new InvalidOperationException("SystemProxyApplyNotVerified");
                    if (!await ProbeForSelectionAsync(targetPort, handover?.Started == true, cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("SelectedRoutePostflightFailed");
                }
                if (_store.Read() != expected) throw new InvalidOperationException("SystemProxyChangedAfterCommit");
                SystemProxyLeaseCoordinator.WriteLease(_options.SystemProxyLeasePath, lease with
                {
                    Phase = "Applied", AppliedAtUtc = DateTimeOffset.UtcNow.ToString("o")
                });
                var committed = pending with { Phase = "Committed", PreviousMode = "", PreviousExpected = null,
                    ReserveClient = reserve, Mode = automaticFallback ? "Auto" : mode };
                _writeState(StatePath, committed);
                _state = committed;
                try { handover?.Complete(); } catch { SafeLog.Write(_options.DataRoot, "HappReceiptCleanupPending"); }
                DecisionJournal.Write(_options.DataRoot, requestId, mode, "Committed");
                return new("Completed", changed ? "SelectedRouteVerified" : "SelectedRouteAlreadyVerified", mode, changed);
            }
            catch (Exception ex)
            {
                var code = ErrorCodes.From(ex);
                try
                {
                    var rollbackRouteUnconfirmed = false;
                    var current = _store.Read();
                    // A registry key cannot supply a cross-program atomic CAS;
                    // check ownership immediately before every restoration.
                    if (wroteLease && current == expected && current != baseline) _store.Write(baseline);
                    if (handover is not null)
                    {
                        var verifyHappReturn = handover.Released;
                        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(65));
                        await handover.RollbackAsync(recovery.Token).ConfigureAwait(false);
                        // A native connect acknowledgement proves neither TLS
                        // nor internet access. Warm up the returned client before
                        // describing the previous connection as restored.
                        if (verifyHappReturn)
                        {
                            try
                            {
                                rollbackRouteUnconfirmed = !await ProbeForSelectionAsync(10809, true, recovery.Token).ConfigureAwait(false);
                            }
                            catch { rollbackRouteUnconfirmed = true; }
                        }
                    }
                    if (_store.Read() == before)
                    {
                        if (wroteLease)
                        {
                            if (previousLease is null) File.Delete(_options.SystemProxyLeasePath);
                            else SystemProxyLeaseCoordinator.WriteLease(_options.SystemProxyLeasePath, previousLease);
                        }
                        _state = !rollbackRouteUnconfirmed && previousState.Expected == before
                            ? previousState : NewState("External", before);
                        if (rollbackRouteUnconfirmed) code = "HappRollbackRouteUnconfirmed";
                        _writeState(StatePath, _state);
                    }
                    else
                    {
                        _state = NewState("External", _store.Read(), requestId);
                        _writeState(StatePath, _state);
                        code = "ExternalRoutePreservedAfterFailedSwitch";
                    }
                }
                catch
                {
                    _state = NewState("External", _store.Read(), requestId);
                    code = "RouteSwitchNeedsReconciliation";
                }
                DecisionJournal.Write(_options.DataRoot, requestId, _state.Mode, code);
                return new("Rejected", code, _state.Mode, _store.Read() != before);
            }
        }
        finally { Interlocked.Exchange(ref _pending, 0); _operation.Release(); }
    }

    private async Task<bool> ProbeForSelectionAsync(int port, bool warmingClient, CancellationToken token, int seconds = 65)
    {
        if (!warmingClient) return await _probe(port, token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            while (true)
            {
                if (await _probe(port, deadline.Token).ConfigureAwait(false)) return true;
                await Task.Delay(750, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { return false; } // A warmup timeout is a failed check, not an opaque InternalFailure.
    }

    internal Task RecoverHandoverAsync(CancellationToken token) => _happ is null ? Task.CompletedTask :
        new HappRouteHandover(_options, _store, _happ).RecoverAsync(token);

    internal async Task<RouteModeResult> RestorePreviousAsync(CancellationToken token)
    {
        var lease = ReadLease();
        if (_happ is not null && lease is not null && HappRouteHandover.IsHapp(lease.Previous) &&
            (_store.Read() == lease.Applied || SystemProxyLeaseCoordinator.RestoreAfterBypassOnlyChange(lease, _store.Read()) is not null))
        {
            if (!_happ.Available && !await _probe(10809, token).ConfigureAwait(false))
                return new("Rejected", "HappControlUnavailable", Mode, false);
            var result = await SelectAsync(Guid.NewGuid().ToString("N"), "Happ", _ => Task.FromResult(false), token).ConfigureAwait(false);
            if (result.Status != "Completed") return result;
        }
        return RestorePrevious();
    }

    public RouteModeResult RestorePrevious()
    {
        // Called by the same serialized controller request loop as SelectAsync.
        var result = SystemProxyLeaseCoordinator.Restore(_options.SystemProxyLeasePath, _owner, _store);
        _state = NewState("External", _store.Read());
        try { _writeState(StatePath, _state); }
        catch { SafeLog.Write(_options.DataRoot, "ExternalModeReceiptPending"); }
        return new(result.Status, result.DetailCode, _state.Mode, result.Changed);
    }
}

internal static class DecisionJournal
{
    // Only fixed protocol fields; no provider URLs, headers or arbitrary messages.
    public static void Write(string root, string requestId, string mode, string phase) =>
        SafeLog.Write(root, $"Route.{requestId}.{mode}.{phase}");
}
