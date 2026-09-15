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
    private readonly SemaphoreSlim _operation = new(1, 1);
    private volatile RouteModeState? _state;
    private int _pending;
    private string StatePath => Path.Combine(_options.DataRoot, "state", "route-mode.v1.json");

    public RouteModeControl(RuntimeOptions options, string owner, ISystemProxyStore store,
        Func<int, CancellationToken, Task<bool>> probe,
        Action<string, RouteModeState>? writeState = null, Func<string?>? externalTunnel = null)
    {
        _options = options;
        _owner = SystemProxyLeaseCoordinator.ValidateOwnerSid(owner);
        _store = store;
        _probe = probe;
        _writeState = writeState ?? ((path, value) => AtomicFile.ReplaceJson(path, value));
        _externalTunnel = externalTunnel ?? (() => null);
    }

    public string Mode => Current.Mode;
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
                    _store.Read() == state.Expected;
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
        bool rememberObservedReserve = false)
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
            if (!string.IsNullOrWhiteSpace(before.AutoConfigUrl))
                return new("Rejected", "SystemProxyPacConflict", previousState.Mode, false);
            if (mode is "Auto" or "ArtVpn" && _externalTunnel() is { } tunnelCode)
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
            try
            {
                if (external is null && !await prepareArt(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("StableFrontNotReady");
                var reserve = external?.Mode ?? previousState.ReserveClient;
                // First-run handover remembers an already working external
                // endpoint only after a real probe, never merely an open port.
                var observedReserve = before.ProxyEnable == 1
                    ? ExternalProxyEndpoint.ForPort(ExternalProxyEndpoint.KnownProxyPort(before.ProxyServer)) : null;
                if (rememberObservedReserve && mode == "Auto" && observedReserve is not null &&
                    await _probe(observedReserve.Port, cancellationToken).ConfigureAwait(false))
                    reserve = observedReserve.Mode;
                if (!await _probe(targetPort, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("SelectedRoutePreflightFailed");
                if (_store.Read() != before) throw new InvalidOperationException("SystemProxyChangedDuringCheck");
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
                if (_store.Read() != before) throw new InvalidOperationException("SystemProxyChangedBeforeCommit");
                cancellationToken.ThrowIfCancellationRequested();
                if (before != expected)
                {
                    _store.Write(expected);
                    changed = true;
                    if (_store.Read() != expected) throw new InvalidOperationException("SystemProxyApplyNotVerified");
                    if (!await _probe(targetPort, cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("SelectedRoutePostflightFailed");
                }
                if (_store.Read() != expected) throw new InvalidOperationException("SystemProxyChangedAfterCommit");
                SystemProxyLeaseCoordinator.WriteLease(_options.SystemProxyLeasePath, lease with
                {
                    Phase = "Applied", AppliedAtUtc = DateTimeOffset.UtcNow.ToString("o")
                });
                var committed = pending with { Phase = "Committed", PreviousMode = "", PreviousExpected = null,
                    ReserveClient = reserve };
                _writeState(StatePath, committed);
                _state = committed;
                DecisionJournal.Write(_options.DataRoot, requestId, mode, "Committed");
                return new("Completed", changed ? "SelectedRouteVerified" : "SelectedRouteAlreadyVerified", mode, changed);
            }
            catch (Exception ex)
            {
                var code = ErrorCodes.From(ex);
                try
                {
                    var current = _store.Read();
                    // A registry key cannot supply a cross-program atomic CAS;
                    // check ownership immediately before every restoration.
                    if (wroteLease && current == expected && current != before) _store.Write(before);
                    if (_store.Read() == before)
                    {
                        if (wroteLease)
                        {
                            if (previousLease is null) File.Delete(_options.SystemProxyLeasePath);
                            else SystemProxyLeaseCoordinator.WriteLease(_options.SystemProxyLeasePath, previousLease);
                        }
                        _state = previousState.Expected == before ? previousState : NewState("External", before);
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
