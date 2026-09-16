using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

internal delegate Task<byte[]> ProviderFetch(Uri uri, CancellationToken cancellationToken);
internal delegate Task ClientAssetFetch(ClientCatalogEntry entry, string destinationPath, CancellationToken cancellationToken);

internal sealed class ProtectedController
{
    private readonly RuntimeOptions _options;
    private readonly InstallState _install;
    private readonly ProviderFetch _fetch;
    private readonly ClientAssetFetch _clientFetch;
    private readonly bool _enforcePrivateAcl;
    private readonly UpdateManifestFetch _updateFetch;
    private readonly StableFrontHost? _front;
    private readonly SlotRuntimeManager? _slots;
    private readonly StartupRestoreRetryPolicy _startupRestore = new();
    private readonly SubscriptionRefreshScheduler _subscriptionRefresh;
    private readonly ISystemProxyStore _systemProxyStore;
    private readonly RouteModeControl? _routes;
    private readonly Func<int, CancellationToken, Task<bool>> _modeProbe;
    private readonly object _statusSync = new();
    private readonly object _routeObservationSync = new();
    private readonly Func<CancellationToken, Task<DirectInternetEvidence>> _internetProbe;
    private readonly SemaphoreSlim _diagnosisGate = new(1, 1);
    private DateTimeOffset _nextDiagnosisAt;
    private int _diagnosisPort;
    private int _diagnosisFailures;
    private long _diagnosisRevision;
    private DateTimeOffset _diagnosisObservationAt;
    private int _initialized;
    private int _prepared;
    private volatile string? _connectingRequestId;
    private sealed record PoolRecoverySignal(long FrontRevision);
    private PoolRecoverySignal? _pendingPoolRecovery;

    public ProtectedController(
        RuntimeOptions options,
        InstallState install,
        ProviderFetch fetch,
        ClientAssetFetch? clientFetch = null,
        UpdateManifestFetch? updateFetch = null,
        StableFrontHost? front = null,
        bool enforcePrivateAcl = true,
        ISystemProxyStore? systemProxyStore = null,
        Func<int, CancellationToken, Task<bool>>? modeProbe = null,
        Func<CancellationToken, Task<DirectInternetEvidence>>? internetProbe = null)
    {
        _options = options;
        _install = install;
        _fetch = fetch;
        _updateFetch = updateFetch ?? UpdateManifestFetcher.DownloadAsync;
        _enforcePrivateAcl = enforcePrivateAcl;
        _front = front;
        _subscriptionRefresh = new SubscriptionRefreshScheduler(install.InstallId);
        _systemProxyStore = systemProxyStore ?? new RegistrySystemProxyStore(install.OwnerSid);
        _clientFetch = clientFetch ?? ((entry, path, token) => ClientAssetFetcher.DownloadAsync(entry, path, token,
            ClientDownloadRoutePolicy.Select(_systemProxyStore.Read())));
        _modeProbe = modeProbe ?? ProbeSelectedRouteAsync;
        _internetProbe = internetProbe ?? DirectInternetProbe.RunAsync;
        _routes = front is null && systemProxyStore is null ? null : new RouteModeControl(options, install.OwnerSid,
            _systemProxyStore, ProbeAndRecordRouteAsync,
            externalTunnel: enforcePrivateAcl ? ExternalTunnelDiagnosis.Check : () => null,
            happ: enforcePrivateAcl ? new HappRouteSession(install.OwnerSid) : null);
        _slots = front is null ? null : new SlotRuntimeManager(options, front,
            automaticAllowed: () => (_routes?.AllowsBackgroundMutation ?? false) && !HasPriorityRequest(),
            throneFallbackAllowed: () => _routes?.Mode is "Auto" or "Standby",
            externalFallbackPort: () => _routes?.ExternalReservePort ?? 2080);
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _prepared, 1) != 0) return;
        Initialize();
        while (true)
        {
            await WaitForOwnerSettingsAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PrepareWithOwnerAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SystemProxyOwnerUnavailableException)
            {
                // Logoff can race the availability read. Retry reconciliation
                // after logon, retaining the exact persisted route and lease.
            }
        }
    }

    private Task WaitForOwnerSettingsAsync(CancellationToken cancellationToken) =>
        _routes is null ? Task.CompletedTask : OwnerSessionAvailability.WaitAsync(_systemProxyStore,
            () => UpdateStatus(status => status with
            {
                Health = "Paused", Country = "—", Nodes = [], Latency = "—",
                Quality = "Ожидание входа в Windows", ServiceAvailable = true,
                Stability = "Служба запущена. Ожидаем настройки пользователя; сеть и выбранный режим сохранены",
                SafeRepairAvailable = false
            }), cancellationToken);

    private async Task PrepareWithOwnerAsync(CancellationToken cancellationToken)
    {
        if (_routes is not null) await _routes.RecoverHandoverAsync(cancellationToken).ConfigureAwait(false);
        if (_front is not null) UpdateStatus(RouteStatusPresentation.BeforeStartup);
        // Pending manual requests survive a crash and precede restoring Auto.
        while (HasPriorityRequest())
            await ProcessAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (_routes is not null && !_routes.AllowsBackgroundMutation)
        {
            if (_routes.SelectedExternal is { } external)
            {
                var ready = await ProbeAndRecordRouteAsync(external.Port, cancellationToken).ConfigureAwait(false);
                if (_routes.SelectedExternal != external || _systemProxyStore.Read() != _routes.Current.Expected)
                {
                    UpdateStatus(status => status with { Health = "Paused", Country = "—", Nodes = [],
                        Stability = "Подключение изменено вручную во время проверки; новый выбор сохранён" });
                    return;
                }
                if (ready && _front is not null && _front.TargetPort != external.Port)
                    _front.SwitchNewConnectionsTo(external.Port, "CommittedManualExternalStartup");
                UpdateStatus(status => status with
                {
                    Mode = _routes.Mode, Health = ready ? "Fallback" : "Unavailable", Country = external.DisplayName, Nodes = [],
                    Quality = ready ? "Внешний резерв проверен" : "Внешний канал не отвечает",
                    Stability = ready ? $"{external.DisplayName} проверен после запуска службы" : $"Проверьте подключение в {external.DisplayName}; ART VPN не возвращает свой прокси"
                });
            }
            else UpdateStatus(status => status with { Health = "Paused", Country = "—", Nodes = [],
                Stability = "Сохранено внешнее подключение; ART VPN не меняет ручной выбор" });
            return;
        }
        if (_slots is null || !RuntimeAssetVerifier.IsInstalled(_options) || ActiveGenerationStore.TryRead(_options) is null)
        {
            if (_front is not null) UpdateStatus(status => status with { Health = "Stopped", Country = "—", Nodes = [],
                Stability = "Готового встроенного канала нет. Настройте подписку или запустите проверку; текущая сеть не изменена" });
            return;
        }
        try
        {
            if (await _slots.RestoreAsync(cancellationToken).ConfigureAwait(false))
            {
                _startupRestore.Succeeded();
                UpdateStatus(status => status with
                {
                    Health = "Healthy",
                    LastSwitchReason = "Рабочий канал восстановлен после запуска службы",
                    ServiceAvailable = true
                });
                await SyncLiveSelectorStatusAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _startupRestore.Schedule(DateTimeOffset.UtcNow);
            if (_startupRestore.Failures >= 4) SafeLog.Write(_options.DataRoot, ErrorCodes.From(ex));
            if (!await TryThroneFallbackAsync(cancellationToken).ConfigureAwait(false))
                UpdateStatus(status => status with
                {
                    Health = "Unavailable",
                    LastSwitchReason = "Windows ещё поднимает сеть; ART VPN повторит восстановление автоматически",
                    SafeRepairAvailable = true
                });
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await PrepareAsync(stoppingToken).ConfigureAwait(false);
        _subscriptionRefresh.Initialize(DateTimeOffset.UtcNow,
            _slots is { HasActiveChannel: true } && HasCurrentCoreQualification());
        using var backgroundStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var watchdog = _slots is null
            ? Task.CompletedTask
            : _slots.RunWatchdogAsync(ApplyWatchdogDecisionAsync, backgroundStop.Token);
        // Observations must not queue behind a multi-minute qualification.
        // This loop never changes the proxy, slot, node or ownership mode.
        var observations = RunObservationsAsync(backgroundStop.Token);
        var nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(1);
        var nextBypassCheck = DateTimeOffset.UtcNow.AddMinutes(2);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_routes?.ObserveExternalChoice() == true)
                        await EnterExternalStandbyAsync(stoppingToken).ConfigureAwait(false);
                    if (HasPriorityRequest())
                    {
                        await ProcessAvailableAsync(stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                    if (_routes is not null && !_routes.AllowsBackgroundMutation && _slots is not null)
                    {
                        await ReconcileManualFrontAsync(stoppingToken).ConfigureAwait(false);
                        await _slots.StopDrainedAsync(stoppingToken).ConfigureAwait(false);
                    }
                    if ((_routes?.AllowsBackgroundMutation ?? true) && _slots is { HasActiveChannel: false } &&
                        !_startupRestore.Pending && RuntimeAssetVerifier.IsInstalled(_options) &&
                        ActiveGenerationStore.TryRead(_options) is not null)
                        _startupRestore.Schedule(DateTimeOffset.UtcNow);
                    if (_startupRestore.ShouldAttempt(DateTimeOffset.UtcNow))
                        await TryDeferredStartupRestoreAsync(stoppingToken).ConfigureAwait(false);
                    var recovery = Interlocked.Exchange(ref _pendingPoolRecovery, null);
                    if (recovery is not null && _front?.Revision == recovery.FrontRevision &&
                        (_routes?.AllowsBackgroundMutation ?? true))
                    {
                        // Only the existing monitor's confirmed failure may
                        // start a cold reserve. A healthy ART never starts HAPP.
                        if (await RunPreemptibleAsync(TryThroneFallbackAsync, stoppingToken).ConfigureAwait(false)) continue;
                        _subscriptionRefresh.RequestRecovery(DateTimeOffset.UtcNow);
                    }
                    if (DateTimeOffset.UtcNow >= nextUpdateCheck)
                    {
                        nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(6);
                        await CheckUpdatesSafeAsync(stoppingToken).ConfigureAwait(false);
                    }
                    var processed = await ProcessAvailableAsync(stoppingToken).ConfigureAwait(false);
                    if (!processed && (_routes?.AllowsBackgroundMutation ?? true) && _slots is not null && DateTimeOffset.UtcNow >= nextBypassCheck)
                    {
                        nextBypassCheck = DateTimeOffset.UtcNow.AddHours(1);
                        await BypassRuleRefresh.ForCandidateAsync(_options, stoppingToken).ConfigureAwait(false);
                    }
                    if (!processed && (_routes?.AllowsBackgroundMutation ?? true) && _subscriptionRefresh.ShouldRun(DateTimeOffset.UtcNow))
                        processed = await TryScheduledSubscriptionRefreshAsync(stoppingToken).ConfigureAwait(false);
                    if (!processed) await Task.Delay(750, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SystemProxyOwnerUnavailableException)
                {
                    await WaitForOwnerSettingsAsync(stoppingToken).ConfigureAwait(false);
                    // Re-enter ordinary observation: no proxy or slot change
                    // is authorized merely by the owner's next logon.
                }
                catch (Exception ex)
                {
                    SafeLog.Write(_options.DataRoot, ErrorCodes.From(ex));
                    try { await Task.Delay(1_000, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            backgroundStop.Cancel();
            try { await observations.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { await watchdog.ConfigureAwait(false); } catch { }
            if (_slots is not null) await _slots.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task RunObservationsAsync(CancellationToken cancellationToken)
    {
        var nextRouteCheck = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SyncLiveSelectorStatusAsync(cancellationToken).ConfigureAwait(false);
                if (_routes is not null && DateTimeOffset.UtcNow >= nextRouteCheck)
                {
                    nextRouteCheck = DateTimeOffset.UtcNow.AddSeconds(30);
                    var actual = _systemProxyStore.Read();
                    var port = actual.ProxyEnable == 1 && string.IsNullOrWhiteSpace(actual.AutoConfigUrl)
                        ? ExternalProxyEndpoint.KnownProxyPort(actual.ProxyServer) : 0;
                    if (port != 0)
                    {
                        var ready = await ProbeAndRecordRouteAsync(port, cancellationToken).ConfigureAwait(false);
                        await UpdateConnectionDiagnosisAsync(port, ready, false, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SystemProxyOwnerUnavailableException) { /* Expected before logon / during logoff. */ }
            catch (Exception ex) { SafeLog.Write(_options.DataRoot, "Observation." + ErrorCodes.From(ex)); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ReconcileManualFrontAsync(CancellationToken cancellationToken)
    {
        if (_front is null || _routes is null || _routes.ManualRequestPending) return;
        var state = _routes.Current;
        var external = _routes.SelectedExternal;
        if (external is null || _front.TargetPort == external.Port || state.Phase != "Committed" ||
            state.Expected.ProxyEnable != 1 || state.Expected.ProxyServer != $"127.0.0.1:{external.Port}" ||
            !string.IsNullOrWhiteSpace(state.Expected.AutoConfigUrl) || _systemProxyStore.Read() != state.Expected) return;
        if (await _modeProbe(external.Port, cancellationToken).ConfigureAwait(false) &&
            _routes.Current == state && _systemProxyStore.Read() == state.Expected)
            _front.SwitchNewConnectionsTo(external.Port, "CommittedManualExternalReconciled");
    }

    private bool HasCurrentCoreQualification()
    {
        var restored = ActiveGenerationStore.TryRead(_options);
        if (restored is null) return false;
        try
        {
            var path = Path.Combine(_options.PrivateGenerationRoot, restored.GenerationId, "qualification.v1.json");
            var qualification = JsonSerializer.Deserialize<QualificationReceipt>(File.ReadAllText(path), JsonSettings.Strict);
            using var config = JsonDocument.Parse(File.ReadAllText(restored.ConfigPath), JsonSettings.Document);
            var selector = config.RootElement.GetProperty("outbounds").EnumerateArray()
                .Single(item => item.GetProperty("tag").GetString() == "art-codex-auto");
            return qualification?.CoreSha256 == RuntimeAssetVerifier.ExpectedCoreSha256 &&
                qualification.Evidence.Length > 0 && qualification.Evidence.All(item => item.RoundsRequested >= 6) &&
                selector.GetProperty("expected").GetInt32() == selector.GetProperty("outbounds").GetArrayLength() &&
                selector.GetProperty("dial_failure_threshold").GetInt32() == 3;
        }
        catch { return false; } // Recheck soon, but retain the already working restored channel.
    }

    private async Task<bool> TryScheduledSubscriptionRefreshAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!File.Exists(_options.ProviderSecretPath) || !File.Exists(_options.ProviderEntropyPath))
        {
            _subscriptionRefresh.Defer(now, TimeSpan.FromHours(1));
            return false;
        }

        var request = new QueuedRequest(
            ProductContract.Schema,
            "art-vpn-controller-request",
            Guid.NewGuid().ToString("N"),
            "RefreshSubscription",
            null,
            null,
            _install.OwnerSid,
            0,
            now.ToString("o"));
        try
        {
            var result = await RunPreemptibleAsync(token => RefreshAsync(request, token), cancellationToken,
                requiresAutomaticMode: true).ConfigureAwait(false);
            _subscriptionRefresh.ObserveResult(result.DetailCode, DateTimeOffset.UtcNow,
                _slots is { HasActiveChannel: true });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _subscriptionRefresh.ObserveResult(ErrorCodes.From(ex), DateTimeOffset.UtcNow,
                _slots is { HasActiveChannel: true });
            SafeLog.Write(_options.DataRoot, ErrorCodes.From(ex));
        }
        return true;
    }

    internal void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        Directory.CreateDirectory(_options.RequestRoot);
        Directory.CreateDirectory(_options.ProcessingRoot);
        Directory.CreateDirectory(_options.ResultRoot);
        Directory.CreateDirectory(_options.PrivateIncomingRoot);
        Directory.CreateDirectory(_options.PrivateGenerationRoot);
        if (_enforcePrivateAcl) PrivateDirectorySecurity.Apply(_options.PrivateRoot);
        if (!File.Exists(_options.SanitizedStatusPath))
            AtomicFile.WriteJson(_options.SanitizedStatusPath, SanitizedServiceStatus.Initial(_install.Version));
        else
        {
            var status = ReadStatus();
            var mode = status.Mode == "ART VPN • системный прокси" ? "Авто" : status.Mode;
            if (!string.Equals(status.Version, _install.Version, StringComparison.Ordinal) || mode != status.Mode)
                AtomicFile.ReplaceJson(_options.SanitizedStatusPath, status with { Version = _install.Version, Mode = mode });
        }
    }

    private async Task TryDeferredStartupRestoreAsync(CancellationToken cancellationToken)
    {
        if (_routes is not null && !_routes.AllowsBackgroundMutation) return;
        if (_slots is null || !RuntimeAssetVerifier.IsInstalled(_options) ||
            ActiveGenerationStore.TryRead(_options) is null)
        {
            _startupRestore.Cancel();
            return;
        }
        if (_slots.HasActiveChannel)
        {
            _startupRestore.Succeeded();
            return;
        }
        try
        {
            if (!await _slots.RestoreAsync(cancellationToken).ConfigureAwait(false))
            {
                _startupRestore.Cancel();
                return;
            }
            _startupRestore.Succeeded();
            UpdateStatus(status => status with
            {
                Health = "Healthy",
                LastSwitchReason = "Сеть Windows поднялась; рабочий канал восстановлен автоматически",
                ServiceAvailable = true,
                SafeRepairAvailable = false
            });
        }
        catch (Exception ex)
        {
            _startupRestore.Schedule(DateTimeOffset.UtcNow);
            if (_startupRestore.Failures >= 4) SafeLog.Write(_options.DataRoot, ErrorCodes.From(ex));
            UpdateStatus(status => status with
            {
                Health = status.Health == "Fallback" ? status.Health : "Unavailable",
                LastSwitchReason = "Сеть или рабочий слот ещё недоступны; следующая попытка будет выполнена автоматически",
                SafeRepairAvailable = true
            });
        }
    }

    internal async Task<bool> ProcessAvailableAsync(CancellationToken cancellationToken)
    {
        Initialize();
        var priority = FindPriorityRequest();
        var processing = priority is not null && Path.GetDirectoryName(priority) == _options.ProcessingRoot ? priority : null;
        processing ??= priority is null ? Directory.EnumerateFiles(_options.ProcessingRoot, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
        if (processing is null)
        {
            var queued = priority ?? Directory.EnumerateFiles(_options.RequestRoot, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (queued is null) return false;
            var name = Path.GetFileName(queued);
            processing = Path.Combine(_options.ProcessingRoot, name);
            try { File.Move(queued, processing, overwrite: false); }
            catch (IOException) { return true; }
        }

        var requestId = Path.GetFileNameWithoutExtension(processing);
        var resultPath = Path.Combine(_options.ResultRoot, requestId + ".json");
        if (File.Exists(resultPath))
        {
            File.Delete(processing);
            return true;
        }

        ControllerRequestResult result;
        QueuedRequest? request = null;
        try
        {
            request = ControllerRequestReader.OpenAndValidate(processing, requestId, _install.OwnerSid);
            result = IsRouteRequest(request) ? await ExecuteAsync(request, cancellationToken).ConfigureAwait(false)
                : await RunPreemptibleAsync(token => ExecuteAsync(request, token), cancellationToken,
                    requiresAutomaticMode: request.Action == "RefreshSubscription").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var code = ErrorCodes.From(ex);
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested && request is not null)
                result = ControllerRequestResult.Deferred(request, "ControlRequestPreemptedByManualChoice");
            else
            {
                result = ControllerRequestResult.Failed(requestId, code);
                SafeLog.Write(_options.DataRoot, code);
            }
        }

        if (request?.Action == "RefreshSubscription")
        {
            _subscriptionRefresh.ObserveResult(result.DetailCode, DateTimeOffset.UtcNow,
                _slots is { HasActiveChannel: true });
        }
        AtomicFile.WriteJson(resultPath, result);
        File.Delete(processing);
        return true;
    }

    private async Task<ControllerRequestResult> ExecuteAsync(
        QueuedRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Action)
        {
            case "RefreshSubscription":
                return await RefreshAsync(request, cancellationToken).ConfigureAwait(false);
            case "ConnectSubscription":
                // Only explicit onboarding opts into taking ownership. P1 and
                // background refresh keep their existing read/prepare semantics.
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(TimeSpan.FromMinutes(6));
                    _connectingRequestId = request.RequestId;
                    try
                    {
                        var connected = await RunPreemptibleAsync(token => SelectModeAsync(request, "Auto", token),
                            deadline.Token).ConfigureAwait(false);
                        if (connected.Status == "Completed") _subscriptionRefresh.AfterFirstConnection(DateTimeOffset.UtcNow);
                        else if (deadline.IsCancellationRequested) connected = connected with { DetailCode = "FirstConnectionTimedOut" };
                        else if (HasPriorityRequest()) connected = connected with { DetailCode = "ControlRequestPreemptedByManualChoice" };
                        return connected;
                    }
                    finally { _connectingRequestId = null; }
                }
            case "CheckNow":
                if (_routes is not null)
                {
                    var actual = _systemProxyStore.Read();
                    var actualPort = actual.ProxyEnable == 1 && string.IsNullOrWhiteSpace(actual.AutoConfigUrl)
                        ? ExternalProxyEndpoint.KnownProxyPort(actual.ProxyServer) : 0;
                    if (actualPort == 0) return ControllerRequestResult.Deferred(request, "UnknownExternalRoute");
                    var ready = await ProbeAndRecordRouteAsync(actualPort, cancellationToken).ConfigureAwait(false);
                    await UpdateConnectionDiagnosisAsync(actualPort, ready, true, cancellationToken).ConfigureAwait(false);
                    return ready ? ControllerRequestResult.Completed(request, "ManualCheckHealthy")
                        : ControllerRequestResult.Deferred(request, "ManualCheckDegradedNoCutover");
                }
                if (_slots is null)
                    return ControllerRequestResult.Deferred(request, "HealthCheckRuntimeUnavailable");
                var health = await _slots.CheckNowAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus(status => status with
                {
                    Health = health.ReasonCode == "ManualCheckHealthy" ? "Healthy" : status.Health,
                    Stability = health.ReasonCode switch
                    {
                        "ManualCheckHealthy" => "ручная проверка подтверждает рабочий канал",
                        "ManualCheckDegradedNoCutover" => "ручная проверка заметила деградацию; канал не переключён, решение остаётся за watchdog",
                        _ => "рабочий канал ещё не запущен"
                    },
                    LastSwitchReason = health.ReasonCode == "ManualCheckHealthy"
                        ? "Канал проверен вручную; переключение не требовалось"
                        : "Проверка выполнена без изменения рабочего маршрута"
                });
                return ControllerRequestResult.Completed(request, health.ReasonCode);
            case "CheckUpdates":
                var update = await CheckUpdatesSafeAsync(cancellationToken).ConfigureAwait(false);
                return ControllerRequestResult.Completed(request, update.DetailCode, update.AvailableVersion);
            case "CheckBypassUpdates":
                await BypassRuleRefresh.ForCandidateAsync(_options, cancellationToken).ConfigureAwait(false);
                UpdateStatus(status => status with { Bypass = BypassRuleRefresh.DescribeActive(_options) });
                return ControllerRequestResult.Completed(request, "BypassCheckedForNextGeneration");
            case "CollectReport":
                SupportDiagnostics.Create(_options, _install);
                return ControllerRequestResult.Completed(request, "SanitizedReportReady");
            case "SubmitReport":
                SupportDiagnostics.Create(_options, _install);
                var delivery = await SupportDiagnostics.SubmitLatestAsync(
                    _options, userConfirmed: true, upload: null, cancellationToken).ConfigureAwait(false);
                return ControllerRequestResult.Completed(request, "SupportReportSent", delivery.CaseId);
            case "SetMode":
                return await SelectModeAsync(request, request.Mode!, cancellationToken).ConfigureAwait(false);
            case "EnableSystemProxy":
                return await SelectModeAsync(request, "Auto", cancellationToken).ConfigureAwait(false);
            case "RestoreSystemProxy":
                if (_routes is null) return ControllerRequestResult.Deferred(request, "HealthCheckRuntimeUnavailable");
                var restored = await _routes.RestorePreviousAsync(cancellationToken).ConfigureAwait(false);
                // A rejected native return may have safely retained ART.
                // Do not label that working connection paused/external.
                if (restored.Status == "Completed" || !SystemProxyLeaseCoordinator.IsArtVpnEndpoint(_systemProxyStore.Read()))
                {
                    try { await EnterExternalStandbyAsync(cancellationToken).ConfigureAwait(false); }
                    catch { SafeLog.Write(_options.DataRoot, "ExternalStandbyReconciliationPending"); }
                }
                return new ControllerRequestResult(1, "art-vpn-controller-result", request.RequestId,
                    restored.Status, restored.DetailCode, "", DateTimeOffset.UtcNow.ToString("o"), restored.Changed, false);
            case "Rollback":
                return ControllerRequestResult.Deferred(request, "RollbackRequiresAcceptedGeneration");
            case "AcquireClient":
                var clientEntry = request.Client == "Happ" ? ClientCatalog.Happ421 : ClientCatalog.Throne124;
                UpdateStatus(status => status with
                {
                    Update = $"{clientEntry.Product}: скачивание с официального сайта и проверка файлов…"
                });
                try
                {
                    var client = await ClientAcquisitionEngine.AcquireAsync(
                        _options, clientEntry, _clientFetch, _enforcePrivateAcl, cancellationToken)
                        .ConfigureAwait(false);
                    UpdateStatus(status => status with
                    {
                        Update = $"Файлы {client.Product} {client.Version} проверены • подключение резерва ещё не настроено"
                    });
                    return ControllerRequestResult.Completed(request,
                        clientEntry.PackageKind == ClientPackageKind.Installer ? "ClientInstallerDownloaded" : "ClientDownloadedAndReady", client.Version);
                }
                catch
                {
                    UpdateStatus(status => status with
                    {
                        Update = $"{clientEntry.Product} не скачан • текущий ART VPN не затронут • доступен официальный сайт"
                    });
                    throw;
                }
            default:
                throw new InvalidDataException("ControllerActionRejected");
        }
    }

    private static bool IsRouteRequest(QueuedRequest request) =>
        request.Action is "SetMode" or "EnableSystemProxy" or "RestoreSystemProxy" or "ConnectSubscription";

    private string? FindPriorityRequest(string? exceptRequestId = null)
    {
        var requests = new List<(string Path, QueuedRequest Request)>();
        foreach (var directory in new[] { _options.RequestRoot, _options.ProcessingRoot })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (File.Exists(Path.Combine(_options.ResultRoot, Path.GetFileName(path)))) continue;
                try
                {
                    var request = ControllerRequestReader.OpenAndValidate(path, Path.GetFileNameWithoutExtension(path), _install.OwnerSid);
                    if (IsRouteRequest(request) && request.RequestId != exceptRequestId) requests.Add((path, request));
                }
                catch { } // Invalid requests are rejected by the normal queue reader.
            }
        }
        return requests.OrderBy(value => value.Request.QueuedAtUtc, StringComparer.Ordinal)
            .ThenBy(value => value.Request.RequestId, StringComparer.Ordinal).Select(value => value.Path).FirstOrDefault();
    }

    private bool HasPriorityRequest() => FindPriorityRequest(_connectingRequestId) is not null;

    private async Task<T> RunPreemptibleAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken, bool requiresAutomaticMode = false)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var observer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var observe = Task.Run(async () =>
        {
            while (!observer.IsCancellationRequested)
            {
                if (HasPriorityRequest() || (requiresAutomaticMode && _routes is not null &&
                    (!_routes.AllowsBackgroundMutation || RecoveryPreemptsRefinement(requiresAutomaticMode,
                        Volatile.Read(ref _pendingPoolRecovery) is not null, _routes.Mode))))
                {
                    work.Cancel();
                    return;
                }
                await Task.Delay(250, observer.Token).ConfigureAwait(false);
            }
        }, observer.Token);
        try { return await operation(work.Token).ConfigureAwait(false); }
        finally
        {
            observer.Cancel();
            try { await observe.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { SafeLog.Write(_options.DataRoot, "ControlObserverReadFailed"); }
        }
    }

    internal static bool RecoveryPreemptsRefinement(bool background, bool confirmedFailure, string mode) =>
        background && confirmedFailure && mode == "Auto";

    private async Task<bool> ProbeSelectedRouteAsync(int port, CancellationToken cancellationToken)
    {
        var observation = await SelectedRouteProbe.ObserveAsync(port, LiveProbeTransport.RunAsync, cancellationToken)
            .ConfigureAwait(false);
        SelectedRouteProbe.Record(_options.DataRoot, observation);
        return observation.Ready;
    }

    internal async Task<bool> ProbeAndRecordRouteAsync(int port, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var revision = _front?.Revision ?? 0;
        var ready = await _modeProbe(port, cancellationToken).ConfigureAwait(false);
        // A probe begun on the old slot cannot certify a newly selected slot.
        if (port == 22080 && revision != (_front?.Revision ?? 0)) return false;
        var health = new
        {
            schema = 1, kind = "art-vpn-route-health", proxy = $"127.0.0.1:{port}", ready,
            atUtc = started.ToString("o"), frontRevision = revision, remoteFunctional = "NotTested"
        };
        // Candidate failure is not active-route failure. Bind each observation
        // to its endpoint; a preflight must not overwrite the active health.
        lock (_routeObservationSync)
        {
            var path = Path.Combine(_options.DataRoot, "state", $"route-health-{port}.v1.json");
            if (File.Exists(path))
            {
                try
                {
                    using var previous = JsonDocument.Parse(File.ReadAllText(path));
                    if (previous.RootElement.GetProperty("atUtc").GetDateTimeOffset() > started) return ready;
                }
                catch { } // A malformed old observation is replaced by this real probe.
            }
            AtomicFile.ReplaceJson(path, health);
            var actual = _systemProxyStore.Read();
            if (actual.ProxyEnable == 1 && actual.ProxyServer == health.proxy && string.IsNullOrWhiteSpace(actual.AutoConfigUrl))
                AtomicFile.ReplaceJson(Path.Combine(_options.DataRoot, "state", "route-health.v1.json"), health);
        }
        return ready;
    }

    internal async Task UpdateConnectionDiagnosisAsync(int port, bool ready, bool force, CancellationToken token)
    {
        if (!await _diagnosisGate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            var actual = _systemProxyStore.Read();
            var endpoint = $"127.0.0.1:{port}";
            if (actual.ProxyEnable != 1 || actual.ProxyServer != endpoint || !string.IsNullOrWhiteSpace(actual.AutoConfigUrl)) return;
            var healthPath = Path.Combine(_options.DataRoot, "state", $"route-health-{port}.v1.json");
            var healthText = File.ReadAllText(healthPath);
            using var evidence = JsonDocument.Parse(healthText);
            var proof = evidence.RootElement;
            var at = proof.GetProperty("atUtc").GetDateTimeOffset();
            var revision = proof.GetProperty("frontRevision").GetInt64();
            if (proof.GetProperty("schema").GetInt32() != 1 || proof.GetProperty("kind").GetString() != "art-vpn-route-health" ||
                proof.GetProperty("proxy").GetString() != endpoint || proof.GetProperty("ready").GetBoolean() != ready ||
                at < DateTimeOffset.UtcNow.AddSeconds(-90) || at > DateTimeOffset.UtcNow.AddSeconds(5) ||
                (port == 22080 && revision != (_front?.Revision ?? 0))) return;
            if (_diagnosisPort != port || _diagnosisRevision != revision)
            {
                _diagnosisPort = port; _diagnosisRevision = revision; _diagnosisFailures = 0;
                _nextDiagnosisAt = default; _diagnosisObservationAt = default;
            }
            string code;
            if (ready)
            {
                _diagnosisFailures = 0; _nextDiagnosisAt = default; code = "Resolved";
            }
            else
            {
                if (at > _diagnosisObservationAt) _diagnosisFailures++;
                _diagnosisObservationAt = at;
                // One brief failure does not establish an outage or a whitelist.
                if (!force && (_diagnosisFailures < 2 || DateTimeOffset.UtcNow < _nextDiagnosisAt)) return;
                var direct = await _internetProbe(token).ConfigureAwait(false);
                // Subscription failures belong to their refresh result. They must
                // not explain a different provider/Throne route's later failure.
                code = ConnectionDiagnosis.Decide(direct, "", allTestedFailed: false);
                _nextDiagnosisAt = DateTimeOffset.UtcNow.AddSeconds(60);
            }
            lock (_routeObservationSync)
            {
                if (_systemProxyStore.Read() != actual || (port == 22080 && revision != (_front?.Revision ?? 0)) ||
                    File.ReadAllText(healthPath) != healthText) return;
                AtomicFile.ReplaceJson(Path.Combine(_options.DataRoot, "state", "connection-diagnosis.v1.json"), new
                {
                    schema = 1, kind = "art-vpn-connection-diagnosis", proxy = endpoint,
                    frontRevision = revision, atUtc = at.ToString("o"), code
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { SafeLog.Write(_options.DataRoot, "ConnectionDiagnosisUnavailable"); }
        finally { _diagnosisGate.Release(); }
    }

    private async Task<ControllerRequestResult> SelectModeAsync(QueuedRequest request, string mode, CancellationToken cancellationToken)
    {
        if (_routes is null) return ControllerRequestResult.Deferred(request, "StableFrontNotReady");
        var oldTarget = _front?.TargetPort ?? 0;
        var connectSubscription = request.Action == "ConnectSubscription";
        var previousGeneration = connectSubscription ? ActiveGenerationStore.TryRead(_options) : null;
        string? preparedGeneration = null;
        var result = await _routes.SelectAsync(request.RequestId, mode, async token =>
        {
            // Native HAPP disconnect may restore its prior proxy. The CAS for
            // ART preparation starts after that verified handover, not before.
            var initialProxy = _systemProxyStore.Read();
            if (_front is not { IsServing: true } || _slots is null) return false;
            if (connectSubscription)
            {
                var refreshed = await RefreshAsync(request, token, explicitConnection: true,
                    canConnect: () => _systemProxyStore.Read() == initialProxy && !HasPriorityRequest()).ConfigureAwait(false);
                if (refreshed.DetailCode != "ProviderGenerationQualifiedAndActivated")
                    throw new InvalidOperationException(refreshed.DetailCode);
                preparedGeneration = refreshed.GenerationId;
                return true;
            }
            if (_front.TargetPort is 22086 or 22087 or 22088 &&
                await _modeProbe(22080, token).ConfigureAwait(false)) return true;
            if (RuntimeAssetVerifier.IsInstalled(_options) && ActiveGenerationStore.TryRead(_options) is not null)
            {
                try { if (await _slots.RestoreAsync(token, explicitSelection: true).ConfigureAwait(false)) return true; }
                catch (OperationCanceledException) { throw; }
                catch { } // Auto may use a separately verified external reserve.
            }
            var externalPort = _routes.ExternalReservePort;
            if (mode == "Auto" && await _modeProbe(externalPort, token).ConfigureAwait(false))
            {
                if (_front.TargetPort != externalPort) _front.SwitchNewConnectionsTo(externalPort, "ManualAutoVerifiedFallback");
                return true;
            }
            return false;
        }, cancellationToken, rememberObservedReserve: connectSubscription).ConfigureAwait(false);
        if (result.Status != "Completed")
        {
            if (connectSubscription && preparedGeneration is not null && _slots is not null)
            {
                using var rollbackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await _slots.RevertFirstConnectionAsync(preparedGeneration, previousGeneration, oldTarget, rollbackDeadline.Token).ConfigureAwait(false); }
                catch
                {
                    SafeLog.Write(_options.DataRoot, "FirstConnectionRollbackNeedsReconciliation");
                    return ControllerRequestResult.Deferred(request, "RouteSwitchNeedsReconciliation");
                }
                return ControllerRequestResult.Deferred(request, result.DetailCode);
            }
            // Preparation may have resumed a backend in shadow. Put only the
            // front target back; never alter the operator's Windows proxy.
            if (_front is not null && _front.TargetPort != oldTarget &&
                (connectSubscription || !SystemProxyLeaseCoordinator.IsArtVpnEndpoint(_systemProxyStore.Read())))
            {
                if (oldTarget != 0) _front.SwitchNewConnectionsTo(oldTarget, "RejectedManualSelection");
                else _front.PauseNewConnections();
            }
            return ControllerRequestResult.Deferred(request, result.DetailCode);
        }
        try
        {
        _startupRestore.Cancel();
        if (ExternalProxyEndpoint.ForMode(mode) is { } external && _front is not null)
        {
            // Old sessions keep their backend; new users of the permanent
            // endpoint follow the verified Throne route as well.
            if (_front.TargetPort != external.Port) _front.SwitchNewConnectionsTo(external.Port, "VerifiedManualExternal");
            if (_slots is not null) await _slots.StopDrainedAsync(cancellationToken).ConfigureAwait(false);
        }
        UpdateStatus(status => RouteStatusPresentation.AfterCommit(status, mode, oldTarget,
            _front?.TargetPort ?? 0, result.Changed, DateTimeOffset.UtcNow));
        // Resolve the new country before returning the button result when possible.
        // Failed observation leaves an explicit "Уточняем" state, not stale Throne cards.
        if (ExternalProxyEndpoint.ForMode(mode) is null) await SyncLiveSelectorStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The verified route is already committed. A metadata/drain error
            // cannot turn this into a false "network unchanged" result.
            SafeLog.Write(_options.DataRoot, "SelectedRouteReconciliationPending");
            return ControllerRequestResult.Completed(request, "SelectedRouteReconciliationPending") with
                { ActiveChannelChanged = result.Changed };
        }
        return ControllerRequestResult.Completed(request, connectSubscription ? "ProviderConnectedAuto" : result.DetailCode)
            with { ActiveChannelChanged = result.Changed };
    }

    private async Task EnterExternalStandbyAsync(CancellationToken cancellationToken)
    {
        _startupRestore.Cancel();
        var actual = _systemProxyStore.Read();
        if (_front is not null)
        {
            var external = ExternalProxyEndpoint.ForPort(ExternalProxyEndpoint.KnownProxyPort(actual.ProxyServer));
            if (actual.ProxyEnable == 1 && string.IsNullOrWhiteSpace(actual.AutoConfigUrl) && external is not null &&
                await _modeProbe(external.Port, cancellationToken).ConfigureAwait(false) && _systemProxyStore.Read() == actual)
                _front.SwitchNewConnectionsTo(external.Port, "ObservedExternalClient");
            else if (!SystemProxyLeaseCoordinator.IsArtVpnEndpoint(_systemProxyStore.Read()))
                _front.PauseNewConnections();
        }
        UpdateStatus(status => status with
        {
            Mode = "Внешнее подключение • ручной выбор сохранён", Health = "Paused", Nodes = [],
            Country = "—", LastSwitchReason = "ART VPN не возвращает свой прокси и не перезапускает окно; предыдущие сессии завершаются"
        });
    }

    private async Task<ProductUpdateCheckReceipt> CheckUpdatesSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProductUpdateChecker.CheckAsync(_options, _install, _updateFetch,
                DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            UpdateStatus(status => status with
            {
                Update = result.Status switch
                {
                    "Available" => $"доступна сборка {result.AvailableVersion} • нажмите «Проверить обновления»",
                    "Current" => $"проверено • установлена актуальная версия {result.CurrentVersion}",
                    "Deferred" => "новая версия разворачивается постепенно • текущая сохранена",
                    "ChannelUnavailable" => "канал обновлений закрыт • текущая версия сохранена; связь VPN не затронута",
                    "Disabled" => "автопроверка отключена каналом выпуска",
                    _ => "канал обновлений будет подключён при публикации"
                }
            });
            return result;
        }
        catch (Exception ex)
        {
            SafeLog.Write(_options.DataRoot, ErrorCodes.From(ex));
            UpdateStatus(status => status with { Update = "проверка не удалась • текущая версия сохранена" });
            throw;
        }
    }

    private async Task<ControllerRequestResult> RefreshAsync(
        QueuedRequest request,
        CancellationToken cancellationToken,
        bool explicitConnection = false,
        Func<bool>? canConnect = null)
    {
        if (!explicitConnection && _routes is not null && !_routes.AllowsBackgroundMutation)
            return ControllerRequestResult.Deferred(request, "SubscriptionRefreshPausedByManualRoute");
        if (!explicitConnection && _slots is not null &&
            !await _slots.CanStageReplacementAsync(cancellationToken).ConfigureAwait(false))
            return DeferRefreshUntilDrained(request);
        var preserveHistory = _slots is { HasActiveChannel: true };
        UpdateStatus(status => status with
        {
            Health = status.Health == "Healthy" || status.Health == "Fallback" ? status.Health : "Checking",
            Subscription = "новый список загружается и проверяется отдельно",
            LastSwitchReason = preserveHistory ? status.LastSwitchReason : "Рабочий канал сохранён на время обновления подписки"
        });

        byte[]? secret = null;
        try
        {
            secret = ProviderSecretStore.Open(_options);
            var staged = await ProviderRefreshEngine.StageAsync(
                _options,
                secret,
                _fetch,
                _enforcePrivateAcl,
                cancellationToken).ConfigureAwait(false);
            var stagedCards = staged.Countries.Take(3).Select((country, index) => new SanitizedNodeStatus(
                index == 0 ? "◌ ПРОВЕРЯЕТСЯ" : $"◌ КАНДИДАТ {index + 1}",
                country,
                "узел из новой подписки",
                "идёт независимая проверка"))
                .ToArray();
            UpdateStatus(status => status with
            {
                Health = status.Health is "Healthy" or "Fallback" ? status.Health : "Checking",
                Country = status.Country == "—"
                    ? $"Проверяем {staged.Countries.Length} стран"
                    : status.Country,
                Nodes = status.Nodes.Length > 0 ? status.Nodes : stagedCards,
                Subscription = $"получена • {staged.SupportedNodes} узлов • страны: {string.Join(", ", staged.Countries)}",
                SubscriptionAtUtc = staged.StagedAtUtc,
                LastSwitchReason = preserveHistory ? status.LastSwitchReason : "Встроенный ART VPN проверяет узлы; Throne для запуска не требуется",
                Stability = status.Stability == "—" ? "проверка выполняется встроенным VPN-ядром" : status.Stability
            });
            if (_slots is not null && RuntimeAssetVerifier.IsInstalled(_options))
            {
                var candidate = await CoreQualificationEngine.QualifyAsync(
                    _options, staged, transport: null, cancellationToken, quickConnect: explicitConnection).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if ((!explicitConnection && _routes is not null && !_routes.AllowsBackgroundMutation) || HasPriorityRequest() ||
                    (explicitConnection && canConnect?.Invoke() != true))
                    throw new OperationCanceledException("ManualRouteHasPriority");
                var activated = await _slots.ActivateAsync(candidate, cancellationToken,
                    explicitConnection ? canConnect : null).ConfigureAwait(false);
                _startupRestore.Succeeded();
                var cards = candidate.Pool.Pool.Take(3).Select((node, index) => new SanitizedNodeStatus(
                    index == 0 ? "● ОСНОВНОЙ" : $"○ РЕЗЕРВ {index}",
                    NodeCountryDisplay.Resolve(node),
                    node.Tag,
                    index == 0 ? $"{node.LatencyP95Ms:0} мс • {node.ThroughputFloorMbps:0.0} Мбит/с" : "проверен в 6 раундах"))
                    .ToArray();
                UpdateStatus(status => status with
                {
                    Health = "Healthy",
                    Country = NodeCountryDisplay.Resolve(candidate.Pool.Pool.Single(node => node.Tag == candidate.Pool.PrimaryTag)),
                    SelectedAtUtc = activated.ActivatedAtUtc,
                    Quality = candidate.Pool.PoolTags.Length == 1
                        ? "канал проверен, но рабочего резерва пока нет"
                        : "прошёл две независимые приёмки",
                    BrowserDegraded = candidate.Pool.Pool[0].GoogleAvailabilityPct is >= 0 and < 83,
                    Latency = $"{activated.LatencyP95Ms:0} мс",
                    Stability = candidate.Pool.Pool[0].GoogleAvailabilityPct >= 83
                        ? "OpenAI, ChatGPT, Telegram и Google проверены; удалённый доступ проверяется в приложении"
                        : "OpenAI, ChatGPT и Telegram проверены; Google требует отдельной проверки",
                    LastSwitchAtUtc = activated.ActivatedAtUtc,
                    LastSwitchReason = "Проверенное поколение подключено только для новых соединений",
                    Nodes = cards,
                    Subscription = $"актуальна • {staged.SupportedNodes} узлов • рабочий пул: {candidate.Pool.PoolTags.Length}",
                    SubscriptionAtUtc = staged.StagedAtUtc,
                    Bypass = BypassRuleRefresh.DescribeActive(_options)
                });
                return ControllerRequestResult.Completed(request, "ProviderGenerationQualifiedAndActivated", staged.GenerationId);
            }
            UpdateStatus(status => status with
            {
                Health = status.Health == "Healthy" || status.Health == "Fallback" ? status.Health : "Checking",
                Subscription = $"получена • {staged.SupportedNodes} поддерживаемых узлов • ожидает независимой проверки",
                SubscriptionAtUtc = staged.StagedAtUtc,
                LastSwitchReason = "Новый список помещён в карантин; рабочий канал не изменён"
            });
            return ControllerRequestResult.Completed(request, "ProviderGenerationStagedForQualification", staged.GenerationId);
        }
        catch (InvalidOperationException ex) when (!explicitConnection && ex.Message == "InactiveSlotStillDraining")
        {
            // Connections can appear after the preflight. Preserve them and
            // retry later; this says nothing about subscription validity.
            return DeferRefreshUntilDrained(request);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                UpdateStatus(status => status with { Subscription = "проверка отложена: ручной выбор имеет приоритет; прежний пул сохранён" });
                throw;
            }
            var code = ErrorCodes.From(ex);
            var hasAcceptedGeneration = ActiveGenerationStore.TryRead(_options) is not null;
            var subscriptionMessage = code switch
            {
                "ProviderSubscriptionExpired" => "по данным провайдера срок подписки истёк • проверьте продление в личном кабинете",
                "ProviderSubscriptionQuotaExceeded" => "по данным провайдера исчерпан лимит трафика • проверьте пакет или дату восстановления лимита",
                "ProviderClockMismatch" => "время Windows отличается от времени сервера • срок подписки пока не подтверждён",
                "ProviderSubscriptionAccessRejected" => "сервер отклонил доступ к подписке • истечение срока не подтверждено, проверьте ссылку и личный кабинет",
                "ProviderSubscriptionLinkRejected" => "ссылка подписки больше не существует • запросите новую у провайдера",
                "ProviderSupportedNodesInsufficient" => "подписка получена, но в ней нет достаточного числа совместимых рабочих узлов",
                "ProviderPayloadEmpty" or "ProviderEncodingRejected" => "провайдер вернул пустой или неподдерживаемый список вместо VPN-узлов",
                "ProviderHttpRejected" => "сервер подписки временно не ответил • предыдущий пул сохранён",
                "QualifiedPoolEmpty" or "PrefilterEmpty" => "ни один проверенный узел пока не прошёл все проверки доступа и стабильности • повторите позже",
                "QualifiedPoolInsufficient" or "PrefilterInsufficient" => "проверку прошло недостаточно узлов для требуемого резерва • рабочий пул сохранён",
                "CoreCheckTimedOut" => "Проверка ядра задержалась. Повторим автоматически; настройки сохранены.",
                _ => "обновление отклонено • предыдущий рабочий пул сохранён"
            };
            if (!hasAcceptedGeneration && _slots is not null && (code is "QualifiedPoolEmpty" or "PrefilterEmpty"))
            {
                try
                {
                    var direct = await _internetProbe(cancellationToken).ConfigureAwait(false);
                    var problem = ConnectionProblemText.Describe(ConnectionDiagnosis.Decide(direct, code, allTestedFailed: true));
                    if (problem is not null) subscriptionMessage = problem.Headline + ". " + problem.Explanation;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { SafeLog.Write(_options.DataRoot, "ConnectionDiagnosisUnavailable"); }
            }
            // Merge into the latest state under the same lock as watchdog and
            // selector updates. A refresh failure must not resurrect an old
            // country, health result or switching timestamp.
            UpdateStatus(status => MergeRefreshFailure(status, hasAcceptedGeneration,
                subscriptionMessage, DateTimeOffset.UtcNow));
            throw;
        }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    private ControllerRequestResult DeferRefreshUntilDrained(QueuedRequest request)
    {
        UpdateStatus(status => status with
        {
            Subscription = "Подбор продолжится после завершения прежних соединений. VPN работает."
        });
        return ControllerRequestResult.Deferred(request, SubscriptionRefreshScheduler.WaitingForDrain);
    }

    private async Task<bool> TryThroneFallbackAsync(CancellationToken cancellationToken)
    {
        if (_front is null || (_routes is not null && (!_routes.AllowsBackgroundMutation || _routes.Mode == "ArtVpn")) || HasPriorityRequest()) return false;
        try
        {
            var externalPort = _routes?.ExternalReservePort ?? 2080;
            if (_routes is { Mode: "Auto" } && externalPort == 10809 && _enforcePrivateAcl)
            {
                var fallback = await _routes.SelectAsync(Guid.NewGuid().ToString("N"), "Happ",
                    _ => Task.FromResult(false), cancellationToken, automaticFallback: true).ConfigureAwait(false);
                if (fallback.Status != "Completed") return false;
                _front.SwitchNewConnectionsTo(10809, "VerifiedNativeAutoFallback");
                _startupRestore.Cancel();
                UpdateStatus(status => status with { Mode = "Auto", Health = "Fallback", Country = "HAPP", Nodes = [],
                    LastSwitchAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                    LastSwitchReason = "ART VPN не прошёл проверку. HAPP запущен и проверен как резерв.",
                    Stability = "Авто: работает HAPP. Кнопка «Авто» повторно проверит ART VPN." });
                return true;
            }
            var sample = await LiveProbeTransport.RunAsync(externalPort, 1_048_576, cancellationToken).ConfigureAwait(false);
            if (!(sample.OpenAi && sample.ChatGpt && sample.Telegram && sample.Integrity) ||
                sample.HardDegradation) return false;
            if ((_routes is not null && !_routes.AllowsBackgroundMutation) || HasPriorityRequest()) return false;
            _front.SwitchNewConnectionsTo(externalPort, "VerifiedExternalStartupFallback");
            UpdateStatus(status => status with
            {
                Health = "Fallback",
                Country = ProviderAdapter.CountryNameFromCode(sample.ObservedCountry) ?? "Страна не определена",
                SelectedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                LastSwitchAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                LastSwitchReason = $"Рабочий ART-слот не восстановился; включён заранее проверенный {ExternalProxyEndpoint.ForPort(externalPort)!.DisplayName}",
                Latency = $"{sample.ChatGptMs:0} мс",
                Stability = sample.RemoteConnect
                    ? "резерв прошёл контроль OpenAI, ChatGPT и Telegram; диагностический Remote-признак доступен"
                    : "резерв прошёл контроль OpenAI, ChatGPT и Telegram; Remote проверяется отдельно в приложении"
            });
            return true;
        }
        catch { return false; }
    }

    private Task ApplyWatchdogDecisionAsync(WatchdogDecision decision)
    {
        if (_routes is not null && !_routes.AllowsBackgroundMutation) return Task.CompletedTask;
        // The monitor only signals. The serial controller performs qualification
        // with the usual ownership, cancellation and commit guards; no new loop
        // or external watchdog/task and no killing an existing VPN process.
        if (NeedsPoolRecovery(decision))
            Interlocked.Exchange(ref _pendingPoolRecovery, new PoolRecoverySignal(_front?.Revision ?? 0));
        else if (decision.ReasonCode == "CurrentChannelHealthy" || decision.Action == "SwitchNewConnections")
            Interlocked.Exchange(ref _pendingPoolRecovery, null);
        if (decision.Action == "RestartActive")
        {
            UpdateStatus(status => status with
            {
                Health = "Healthy",
                LastSwitchAtUtc = decision.AtUtc,
                LastSwitchReason = "Watchdog восстановил активный VPN-процесс без смены страны",
                Stability = "аварийный процесс перезапущен; постоянный вход 22080 сохранён",
                SafeRepairAvailable = false
            });
        }
        else if (decision.Action == "ActiveRestartFailed")
        {
            UpdateStatus(status => status with
            {
                Health = "Unavailable",
                LastSwitchReason = "Активный VPN-процесс остановился; watchdog повторяет безопасное восстановление",
                Stability = "рабочий пул и настройки сохранены; повтор без смены страны через 15 секунд",
                SafeRepairAvailable = true
            });
        }
        else if (decision.Action == "SwitchNewConnections")
        {
            UpdateStatus(status => status with
            {
                Health = ExternalProxyEndpoint.ForPort(decision.ActivePort) is not null ? "Fallback" : "Healthy",
                LastSwitchAtUtc = decision.AtUtc,
                SelectedAtUtc = decision.AtUtc,
                LastSwitchReason = ExternalProxyEndpoint.ForPort(decision.ActivePort) is { } reserve
                    ? $"После подтверждённого отказа включён заранее проверенный {reserve.DisplayName}"
                    : "После подтверждённого отказа включён проверенный резервный ART-слот",
                Stability = "переключены только новые соединения; открытые сессии не прерывались"
            });
        }
        else if (decision.FailureCount > 0)
        {
            UpdateStatus(status => status with
            {
                Health = decision.FailureCount >= 3 ? "Unavailable" : status.Health,
                SafeRepairAvailable = decision.FailureCount >= 3 || status.SafeRepairAvailable,
                Stability = decision.FailureCount >= 3 ? "Связь не подтверждена; ищем рабочий канал"
                    : $"наблюдаем канал: подтверждений сбоя {decision.FailureCount} из 3",
                LastSwitchReason = decision.ReasonCode == "NoPrequalifiedReserve"
                    ? "Сбой подтверждён, но исправного резерва нет. Живой VPN не перезапускался; открытые соединения сохранены"
                    : decision.ReasonCode == "TlsFailureObservedAndQuarantined"
                    ? "Обнаружена ошибка TLS; watchdog ждёт повторного подтверждения и проверенного резерва"
                    : "Единичный сбой не вызвал переключение"
            });
        }
        else if (decision.Action == "KeepCurrent" && decision.ReasonCode == "CurrentChannelSecondaryDegraded")
        {
            UpdateStatus(status => status with
            {
                Stability = "Проверки OpenAI проходят; Telegram или контроль загрузки недоступен. Канал ChatGPT сохранён",
                SafeRepairAvailable = false
            });
        }
        else if (decision.Action == "KeepCurrent" && decision.ReasonCode == "CurrentChannelBrowserDegraded")
        {
            UpdateStatus(status => status with
            {
                Stability = "OpenAI работает; Google не прошёл проверку. Канал ChatGPT сохранён, напрямую поиск не переводился",
                BrowserDegraded = true,
                SafeRepairAvailable = false
            });
        }
        else if (decision.Action == "KeepCurrent" && decision.ReasonCode == "CurrentChannelHealthy")
        {
            UpdateStatus(status => status with
            {
                Health = ExternalProxyEndpoint.ForPort(decision.ActivePort) is not null ? "Fallback" : "Healthy",
                Stability = "канал стабилен • watchdog Healthy • подтверждений сбоя 0 из 3",
                BrowserDegraded = false,
                SafeRepairAvailable = false
            });
        }
        return Task.CompletedTask;
    }

    internal static bool NeedsPoolRecovery(WatchdogDecision decision) =>
        decision.Action == "KeepCurrent" && decision.ReasonCode == "NoPrequalifiedReserve" && decision.FailureCount >= 3;

    internal static SanitizedServiceStatus MergeRefreshFailure(
        SanitizedServiceStatus current, bool acceptedGeneration, string message, DateTimeOffset now)
    {
        var keepLive = acceptedGeneration || current.Health == "Fallback";
        return current with
        {
            Health = keepLive ? current.Health : "Unavailable",
            Country = keepLive ? current.Country : "—",
            Nodes = keepLive ? current.Nodes : [],
            Subscription = message,
            SubscriptionAtUtc = now.ToString("o"),
            // Channel history describes route events, not subscription errors.
            LastSwitchReason = keepLive ? current.LastSwitchReason : "Рабочий канал не создан. " + message,
            SafeRepairAvailable = keepLive ? current.SafeRepairAvailable : true
        };
    }

    private async Task SyncLiveSelectorStatusAsync(CancellationToken cancellationToken)
    {
        if ((_routes is not null && !_routes.AllowsBackgroundMutation) ||
            _front is null || _front.TargetPort is not (22086 or 22087 or 22088)) return;
        var frontRevision = _front.Revision;
        var live = await LiveSelectorStatusReader.TryReadAsync(_options, cancellationToken).ConfigureAwait(false);
        if (live is null) return;
        bool StillCurrent() => RouteStatusPresentation.CanApplySelector(frontRevision, _front.Revision,
            _front.TargetPort, _routes?.AllowsBackgroundMutation ?? true, ActiveGenerationStore.TryRead(_options), live);
        if (!StillCurrent()) return;
        AtomicFile.ReplaceJson(Path.Combine(_options.DataRoot, "state", "selector-live.v1.json"), live);
        UpdateStatus(status =>
        {
            // A manual switch may complete while the async core read is in flight.
            // Recheck under the status writer lock before replacing the visible cards.
            if (!StillCurrent()) return status;
            var changed = RouteStatusPresentation.SelectorNodeChanged(status, live);
            return status with
            {
                Country = live.CurrentCountry,
                Quality = live.Quality,
                Latency = live.Latency,
                Nodes = live.Nodes,
                SelectedAtUtc = changed ? live.ObservedAtUtc : status.SelectedAtUtc,
                LastSwitchAtUtc = changed ? live.ObservedAtUtc : status.LastSwitchAtUtc,
                LastSwitchReason = changed
                    ? "Встроенный автовыбор выбрал фактически используемый проверенный узел"
                    : status.LastSwitchReason,
                Stability = changed
                    ? "обновлён маршрут только для новых соединений; открытые сессии сохранены"
                    : status.Stability
            };
        });
    }

    private SanitizedServiceStatus ReadStatus()
    {
        try
        {
            return JsonSerializer.Deserialize<SanitizedServiceStatus>(
                       File.ReadAllText(_options.SanitizedStatusPath, Encoding.UTF8), JsonSettings.Strict)
                   ?? SanitizedServiceStatus.Initial(_install.Version);
        }
        catch { return SanitizedServiceStatus.Initial(_install.Version); }
    }

    private void UpdateStatus(Func<SanitizedServiceStatus, SanitizedServiceStatus> update)
    {
        lock (_statusSync)
            AtomicFile.ReplaceJson(_options.SanitizedStatusPath, update(ReadStatus()));
    }
}

internal static partial class ControllerRequestReader
{
    private static readonly string[] ExpectedProperties =
    [
        "schema", "kind", "requestId", "action", "mode", "client", "callerSid", "callerSessionId", "queuedAtUtc"
    ];

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex("^S-1-5-(?:18|21-(?:[0-9]+-){3}[0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CallerSidPattern();

    public static QueuedRequest OpenAndValidate(string path, string expectedRequestId, string ownerSid)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), JsonSettings.Document);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.EnumerateObject().Select(property => property.Name).SequenceEqual(ExpectedProperties, StringComparer.Ordinal))
            throw new InvalidDataException("ControllerRequestShapeRejected");
        var request = JsonSerializer.Deserialize<QueuedRequest>(root.GetRawText(), JsonSettings.Strict)
                      ?? throw new InvalidDataException("ControllerRequestEmpty");
        if (request.Schema != 1 || request.Kind != "art-vpn-controller-request" ||
            !RequestIdPattern().IsMatch(request.RequestId) || request.RequestId != expectedRequestId ||
            !ProductContract.Actions.Contains(request.Action, StringComparer.Ordinal) ||
            !CallerSidPattern().IsMatch(request.CallerSid) || request.CallerSessionId < 0)
            throw new InvalidDataException("ControllerRequestIdentityRejected");
        if (!DateTimeOffset.TryParseExact(request.QueuedAtUtc, "o", null,
                System.Globalization.DateTimeStyles.None, out var queued) ||
            queued > DateTimeOffset.UtcNow.AddMinutes(5) || queued < DateTimeOffset.UtcNow.AddDays(-7))
            throw new InvalidDataException("ControllerRequestTimeRejected");
        if (request.Action == "SetMode")
        {
            if (request.Client is not null || !ProductContract.Modes.Contains(request.Mode, StringComparer.Ordinal))
                throw new InvalidDataException("ControllerModeRejected");
        }
        else if (request.Action == "AcquireClient")
        {
            if (request.Mode is not null || !ProductContract.Clients.Contains(request.Client, StringComparer.Ordinal))
                throw new InvalidDataException("ControllerClientRejected");
        }
        else if (request.Mode is not null || request.Client is not null)
            throw new InvalidDataException("ControllerArgumentsRejected");

        // The queue is service-written and ACL-protected. Retain the SID in
        // the receipt for audit, while rejecting malformed identities. Owner,
        // SYSTEM and an authenticated Administrator are admitted by the pipe.
        _ = ownerSid;
        return request;
    }
}

internal static class ProviderRefreshEngine
{
    public static async Task<StagedProviderGeneration> StageAsync(
        RuntimeOptions options,
        byte[] providerUtf8,
        ProviderFetch fetch,
        bool enforcePrivateAcl,
        CancellationToken cancellationToken)
    {
        ProviderSecretStore.ValidateProviderUtf8(providerUtf8);
        var providerText = new UTF8Encoding(false, true).GetString(providerUtf8);
        if (!Uri.TryCreate(providerText, UriKind.Absolute, out var providerUri))
            throw new InvalidDataException("ProviderValueRejected");

        var payload = await fetch(providerUri, cancellationToken).ConfigureAwait(false);
        try
        {
            var parsed = ProviderAdapter.Parse(payload, minimumNodes: 3);
            Directory.CreateDirectory(options.PrivateIncomingRoot);
            Directory.CreateDirectory(options.PrivateGenerationRoot);
            if (enforcePrivateAcl) PrivateDirectorySecurity.Apply(options.PrivateRoot);

            var generationId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" +
                               Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            var temporary = Path.Combine(options.PrivateIncomingRoot, generationId + ".tmp");
            var final = Path.Combine(options.PrivateGenerationRoot, generationId);
            Directory.CreateDirectory(temporary);
            try
            {
                var privateConfig = Encoding.UTF8.GetBytes(parsed.PrivateProbeConfig.ToJsonString(JsonSettings.Output));
                try { AtomicFile.WriteBytes(Path.Combine(temporary, "probe-config.json"), privateConfig); }
                finally { CryptographicOperations.ZeroMemory(privateConfig); }
                AtomicFile.WriteJson(Path.Combine(temporary, "manifest.v1.json"), parsed.SanitizedManifest);

                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(parsed.SanitizedManifest, JsonSettings.Output);
                string manifestHash;
                try { manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)); }
                finally { CryptographicOperations.ZeroMemory(manifestBytes); }
                var receipt = new StagedProviderGeneration(
                    1,
                    "art-vpn-staged-provider-generation",
                    generationId,
                    "Quarantined",
                    parsed.Adapter,
                    parsed.SourceLines,
                    parsed.Nodes.Length,
                    parsed.Nodes.Select(node => node.Country).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    manifestHash,
                    DateTimeOffset.UtcNow.ToString("o"),
                    false,
                    false);
                AtomicFile.WriteJson(Path.Combine(temporary, "generation.v1.json"), receipt);
                Directory.Move(temporary, final);
                AtomicFile.ReplaceJson(options.StagedGenerationPath, receipt);
                return receipt;
            }
            catch
            {
                DeleteOwnedTemporary(options.PrivateIncomingRoot, temporary);
                throw;
            }
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static void DeleteOwnedTemporary(string incomingRoot, string candidate)
    {
        var root = Path.GetFullPath(incomingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(candidate);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).EndsWith(".tmp", StringComparison.Ordinal) ||
            !Regex.IsMatch(Path.GetFileName(resolved), "^[0-9]{8}T[0-9]{6}Z-[a-f0-9]{12}\\.tmp$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("TemporaryGenerationPathRejected");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}

internal static class SubscriptionFetcher
{
    private const int MaxRedirects = 3;

    public static async Task<byte[]> DownloadAsync(Uri original, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            CheckCertificateRevocationList = true
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ART-VPN/0.1 provider-refresh");
        var current = ValidateUri(original);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirect == MaxRedirects || response.Headers.Location is null)
                    throw new InvalidDataException("ProviderRedirectRejected");
                current = ValidateUri(response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location));
                continue;
            }
            RequireUsableResponse(response, DateTimeOffset.UtcNow);
            if (response.Content.Headers.ContentLength is > ProviderAdapter.MaxPayloadBytes)
                throw new InvalidDataException("ProviderPayloadSizeRejected");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[32 * 1024];
            try
            {
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (output.Length + count > ProviderAdapter.MaxPayloadBytes)
                        throw new InvalidDataException("ProviderPayloadSizeRejected");
                    output.Write(buffer, 0, count);
                }
                if (output.Length == 0) throw new InvalidDataException("ProviderPayloadEmpty");
                return output.ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        throw new InvalidDataException("ProviderRedirectRejected");
    }

    internal static void RequireUsableResponse(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.IsSuccessStatusCode || (int)response.StatusCode is 401 or 402 or 403 or 410)
        {
            var condition = ProviderResponseCondition.Inspect(response, now);
            if (condition.Length > 0) throw new IOException(condition);
        }
        if ((int)response.StatusCode is 401 or 402 or 403 or 410)
            throw new IOException("ProviderSubscriptionAccessRejected");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new IOException("ProviderSubscriptionLinkRejected");
        if (!response.IsSuccessStatusCode) throw new IOException("ProviderHttpRejected");
    }

    private static Uri ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.DnsSafeHost) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            (!uri.IsDefaultPort && uri.Port != 443))
            throw new InvalidDataException("ProviderFetchUriRejected");
        return uri;
    }
}

internal sealed record SanitizedNodeStatus(string Role, string Country, string Label, string Quality);

internal sealed record SanitizedServiceStatus(
    int Schema,
    string Kind,
    string Health,
    string Mode,
    string Country,
    string SelectedAtUtc,
    string Quality,
    string Latency,
    string Stability,
    string LastSwitchAtUtc,
    string LastSwitchReason,
    SanitizedNodeStatus[] Nodes,
    string Subscription,
    string SubscriptionAtUtc,
    string Bypass,
    string Update,
    string Version,
    bool ServiceAvailable,
    bool SafeRepairAvailable,
    bool BrowserDegraded = false)
{
    public static SanitizedServiceStatus Initial(string version) => new(
        1,
        "art-vpn-sanitized-status",
        "Stopped",
        "Авто",
        "—",
        "—",
        "Не проверено",
        "—",
        "—",
        "—",
        "Настройка ещё не завершена; существующая сеть не изменена",
        [],
        "не настроена",
        "—",
        "будет проверен перед подключением",
        "обновления ещё не проверены",
        version,
        true,
        false);
}

internal sealed record StagedProviderGeneration(
    int Schema,
    string Kind,
    string GenerationId,
    string Status,
    string Adapter,
    int SourceLines,
    int SupportedNodes,
    string[] Countries,
    string ManifestSha256,
    string StagedAtUtc,
    bool Promoted,
    bool ContainsProviderSecret);

internal sealed record ControllerRequestResult(
    int Schema,
    string Kind,
    string RequestId,
    string Status,
    string DetailCode,
    string GenerationId,
    string AtUtc,
    bool ActiveChannelChanged,
    bool ContainsProviderSecret)
{
    public static ControllerRequestResult Completed(QueuedRequest request, string code, string generationId = "") =>
        new(1, "art-vpn-controller-result", request.RequestId, "Completed", code, generationId,
            DateTimeOffset.UtcNow.ToString("o"), false, false);

    public static ControllerRequestResult Deferred(QueuedRequest request, string code) =>
        new(1, "art-vpn-controller-result", request.RequestId, "Deferred", code, "",
            DateTimeOffset.UtcNow.ToString("o"), false, false);

    public static ControllerRequestResult Failed(string requestId, string code) =>
        new(1, "art-vpn-controller-result", requestId, "Failed", code, "",
            DateTimeOffset.UtcNow.ToString("o"), false, false);
}

internal sealed class StartupRestoreRetryPolicy
{
    private int _failures;


    public int Failures => _failures;
    public bool Pending { get; private set; }
    public DateTimeOffset NextAttemptUtc { get; private set; }

    public void Schedule(DateTimeOffset now)
    {
        var delay = _failures switch
        {
            0 => TimeSpan.FromSeconds(15),
            1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(1),
            3 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(5)
        };
        _failures++;
        Pending = true;
        NextAttemptUtc = now.Add(delay);
    }

    public bool ShouldAttempt(DateTimeOffset now) => Pending && now >= NextAttemptUtc;

    public void Succeeded()
    {
        _failures = 0;
        Pending = false;
        NextAttemptUtc = default;
    }

    public void Cancel() => Succeeded();
}

internal sealed class SubscriptionRefreshScheduler
{
    internal const string WaitingForDrain = "SubscriptionRefreshWaitingForDrain";
    private readonly string _installId;
    private int _failures;
    private int _checkTimeouts;
    private DateTimeOffset _nextRecoveryAllowed;

    public SubscriptionRefreshScheduler(string installId)
    {
        if (!Regex.IsMatch(installId, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("RefreshScheduleIdentityRejected");
        _installId = installId;
    }

    public DateTimeOffset NextRunUtc { get; private set; }
    public bool Initialized { get; private set; }

    public void Initialize(DateTimeOffset now, bool activeGenerationReady)
    {
        if (Initialized) return;
        Initialized = true;
        NextRunUtc = now.Add(activeGenerationReady ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(2))
            .Add(Jitter("initial", activeGenerationReady ? 30 : 1));
    }

    public bool ShouldRun(DateTimeOffset now) => Initialized && now >= NextRunUtc;

    public bool RequestRecovery(DateTimeOffset now)
    {
        if (now < _nextRecoveryAllowed) return false;
        _nextRecoveryAllowed = now.AddMinutes(2);
        Initialized = true;
        NextRunUtc = now;
        return true;
    }

    public void ObserveResult(string detailCode, DateTimeOffset now, bool activeGenerationReady)
    {
        if (detailCode == WaitingForDrain)
        {
            // Cheap read-only preflight every 30 seconds, not a provider fetch
            // or another full qualification. Do not consume failure backoff.
            Initialized = true;
            NextRunUtc = now.AddSeconds(30);
        }
        else if (detailCode == "CoreCheckTimedOut")
        {
            // A valid list was not evaluated. Do not postpone first refinement
            // for 30+ minutes, but bound retries under persistent contention.
            Initialized = true;
            NextRunUtc = now.AddSeconds(30 * (1 << Math.Min(_checkTimeouts, 3)));
            _checkTimeouts = Math.Min(_checkTimeouts + 1, 3);
        }
        else if (detailCode == "ProviderGenerationQualifiedAndActivated") Succeeded(now);
        else Failed(now, activeGenerationReady);
    }

    public void Succeeded(DateTimeOffset now)
    {
        Initialized = true;
        _failures = 0;
        _checkTimeouts = 0;
        NextRunUtc = now.AddHours(12).Add(Jitter("success", 45));
    }

    public void Failed(DateTimeOffset now, bool activeGenerationReady = true)
    {
        Initialized = true;
        var delay = !activeGenerationReady ? _failures switch
        {
            0 => TimeSpan.FromMinutes(2),
            1 => TimeSpan.FromMinutes(5),
            2 => TimeSpan.FromMinutes(10),
            3 => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromHours(1)
        } : _failures switch
        {
            0 => TimeSpan.FromMinutes(30),
            1 => TimeSpan.FromHours(1),
            2 => TimeSpan.FromHours(2),
            3 => TimeSpan.FromHours(4),
            _ => TimeSpan.FromHours(6)
        };
        _failures++;
        NextRunUtc = now.Add(delay).Add(Jitter("failure-" + _failures, activeGenerationReady ? 15 : 1));
    }

    public void Defer(DateTimeOffset now, TimeSpan delay)
    {
        if (delay < TimeSpan.FromMinutes(5) || delay > TimeSpan.FromHours(6))
            throw new ArgumentOutOfRangeException(nameof(delay));
        Initialized = true;
        NextRunUtc = now.Add(delay);
    }

    // One early refinement after an explicit first connection. This is not
    // generic retry/backoff: regular refreshes retain their existing bounds.
    // The controller still requires Auto and preempts for a manual command.
    public void AfterFirstConnection(DateTimeOffset now)
    {
        Initialized = true;
        _failures = 0;
        _checkTimeouts = 0;
        NextRunUtc = now.AddSeconds(30);
    }

    private TimeSpan Jitter(string phase, int maximumMinutes)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(_installId + ":" + phase));
        try { return TimeSpan.FromMinutes(BitConverter.ToUInt16(bytes, 0) % (maximumMinutes + 1)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal static class ProtectedControllerTests
{
    public static async Task<ProtectedControllerTestReceipt> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-controller-test-" + Guid.NewGuid().ToString("N"));
        var options = RuntimeOptions.Test(root, "ARTSPORT.ARTVpn.ControllerTest." + Guid.NewGuid().ToString("N"));
        const string provider = "https://example.invalid/provider-controller-test";
        const string key = "abcdefghijklmnopqrstuvwxyzABCDEFGH123456789";
        var source = string.Join('\n', new[]
        {
            $"vless://00000000-1111-2222-3333-444444444451@198.51.100.21:443?security=reality&type=tcp&sni=example.com&pbk={key}#Нидерланды",
            $"vless://00000000-1111-2222-3333-444444444452@198.51.100.22:8443?security=reality&type=grpc&sni=example.org&pbk={key}#Germany",
            $"vless://00000000-1111-2222-3333-444444444453@198.51.100.23:9443?security=reality&type=ws&sni=example.net&pbk={key}&path=%2Fws#Estonia"
        });
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var payload = Encoding.ASCII.GetBytes(Convert.ToBase64String(sourceBytes));
        var providerBytes = Encoding.UTF8.GetBytes(provider);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.InstallStatePath)!);
            var install = new InstallState(1, "art-vpn-consumer-install", "Configured", Environment.MachineName,
                "S-1-5-21-111-222-333", "S-1-5-21-111-222-333-1001", Guid.NewGuid().ToString("N"),
                "0.1.0-beta.1", new string('E', 64), DateTimeOffset.UtcNow.ToString("o"), false, false);
            ProviderSecretStore.Store(options, providerBytes, enforcePrivateAcl: false);
            var activeMarker = Path.Combine(root, "state", "active-generation.test");
            Directory.CreateDirectory(Path.GetDirectoryName(activeMarker)!);
            File.WriteAllText(activeMarker, "known-good-active", Encoding.UTF8);

            var request = NewRequest("RefreshSubscription", install.OwnerSid);
            AtomicFile.WriteJson(Path.Combine(options.RequestRoot, request.RequestId + ".json"), request);
            var controller = new ProtectedController(options, install,
                (_, _) => Task.FromResult(payload.ToArray()), enforcePrivateAcl: false);
            Assert(await controller.ProcessAvailableAsync(CancellationToken.None).ConfigureAwait(false));
            var resultPath = Path.Combine(options.ResultRoot, request.RequestId + ".json");
            var result = JsonSerializer.Deserialize<ControllerRequestResult>(File.ReadAllText(resultPath), JsonSettings.Strict)!;
            Assert(result.Status == "Completed" && result.DetailCode == "ProviderGenerationStagedForQualification" &&
                   !result.ActiveChannelChanged && result.GenerationId.Length > 0);
            Assert(File.ReadAllText(activeMarker, Encoding.UTF8) == "known-good-active");
            Assert(Directory.Exists(Path.Combine(options.PrivateGenerationRoot, result.GenerationId)) &&
                   File.Exists(options.StagedGenerationPath));

            var stateText = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "state"), "*", SearchOption.AllDirectories)
                .Select(path => File.ReadAllText(path, Encoding.UTF8)));
            Assert(!stateText.Contains(provider, StringComparison.Ordinal) &&
                   !stateText.Contains("198.51.100", StringComparison.Ordinal) &&
                   !stateText.Contains("444444444451", StringComparison.Ordinal));
            var privateText = File.ReadAllText(
                Path.Combine(options.PrivateGenerationRoot, result.GenerationId, "probe-config.json"), Encoding.UTF8);
            Assert(privateText.Contains("444444444451", StringComparison.Ordinal));

            var failed = NewRequest("RefreshSubscription", install.OwnerSid);
            AtomicFile.WriteJson(Path.Combine(options.RequestRoot, failed.RequestId + ".json"), failed);
            var failingController = new ProtectedController(options, install,
                (_, _) => throw new IOException("InjectedProviderFailure"), enforcePrivateAcl: false);
            Assert(await failingController.ProcessAvailableAsync(CancellationToken.None).ConfigureAwait(false));
            var failedResult = JsonSerializer.Deserialize<ControllerRequestResult>(
                File.ReadAllText(Path.Combine(options.ResultRoot, failed.RequestId + ".json")), JsonSettings.Strict)!;
            Assert(failedResult.Status == "Failed" && !failedResult.ActiveChannelChanged);
            Assert(File.ReadAllText(activeMarker, Encoding.UTF8) == "known-good-active");

            // A successful HTTP response can still contain no usable subscription.
            // Such payloads must not replace even the previous staged good list.
            var knownGoodStage = File.ReadAllText(options.StagedGenerationPath, Encoding.UTF8);
            foreach (var invalidPayload in new[] { "", "<html>not a VPN list</html>", "[]", "%%% broken subscription" })
            {
                var invalid = NewRequest("RefreshSubscription", install.OwnerSid);
                AtomicFile.WriteJson(Path.Combine(options.RequestRoot, invalid.RequestId + ".json"), invalid);
                var invalidController = new ProtectedController(options, install,
                    (_, _) => Task.FromResult(Encoding.UTF8.GetBytes(invalidPayload)), enforcePrivateAcl: false);
                Assert(await invalidController.ProcessAvailableAsync(CancellationToken.None).ConfigureAwait(false));
                var invalidResult = JsonSerializer.Deserialize<ControllerRequestResult>(File.ReadAllText(
                    Path.Combine(options.ResultRoot, invalid.RequestId + ".json")), JsonSettings.Strict)!;
                Assert(invalidResult.Status == "Failed" && !invalidResult.ActiveChannelChanged);
                Assert(File.ReadAllText(activeMarker, Encoding.UTF8) == "known-good-active" &&
                    File.ReadAllText(options.StagedGenerationPath, Encoding.UTF8) == knownGoodStage);
            }

            var expired = NewRequest("RefreshSubscription", install.OwnerSid);
            AtomicFile.WriteJson(Path.Combine(options.RequestRoot, expired.RequestId + ".json"), expired);
            var expiredController = new ProtectedController(options, install,
                (_, _) => throw new IOException("ProviderSubscriptionAccessRejected"), enforcePrivateAcl: false);
            Assert(await expiredController.ProcessAvailableAsync(CancellationToken.None).ConfigureAwait(false));
            var expiredResult = JsonSerializer.Deserialize<ControllerRequestResult>(File.ReadAllText(
                Path.Combine(options.ResultRoot, expired.RequestId + ".json")), JsonSettings.Strict)!;
            var expiredStatus = JsonSerializer.Deserialize<SanitizedServiceStatus>(
                File.ReadAllText(options.SanitizedStatusPath, Encoding.UTF8), JsonSettings.Strict)!;
            Assert(expiredResult.Status == "Failed" &&
                   expiredResult.DetailCode == "ProviderSubscriptionAccessRejected" &&
                   expiredStatus.Health == "Unavailable" &&
                   expiredStatus.Subscription.Contains("истечение срока не подтверждено", StringComparison.OrdinalIgnoreCase) &&
                   expiredStatus.SafeRepairAvailable && !expiredResult.ActiveChannelChanged);

            var retry = new StartupRestoreRetryPolicy();
            var now = DateTimeOffset.Parse("2026-08-31T00:00:00.0000000+00:00");
            Assert(!retry.Pending && !retry.ShouldAttempt(now));
            retry.Schedule(now);
            Assert(retry.Pending && retry.NextAttemptUtc == now.AddSeconds(15));
            Assert(!retry.ShouldAttempt(now.AddSeconds(14)) && retry.ShouldAttempt(now.AddSeconds(15)));
            retry.Schedule(now.AddSeconds(15));
            Assert(retry.NextAttemptUtc == now.AddSeconds(45));
            retry.Succeeded();
            Assert(!retry.Pending && !retry.ShouldAttempt(now.AddDays(1)));

            var refresh = new SubscriptionRefreshScheduler(install.InstallId);
            refresh.Initialize(now, activeGenerationReady: true);
            Assert(refresh.NextRunUtc >= now.AddHours(6) && refresh.NextRunUtc <= now.AddHours(6.5));
            Assert(!refresh.ShouldRun(refresh.NextRunUtc.AddTicks(-1)) && refresh.ShouldRun(refresh.NextRunUtc));
            refresh.Succeeded(now);
            Assert(refresh.NextRunUtc >= now.AddHours(12) && refresh.NextRunUtc <= now.AddHours(12.75));
            refresh.Failed(now);
            Assert(refresh.NextRunUtc >= now.AddMinutes(30) && refresh.NextRunUtc <= now.AddMinutes(45));
            refresh.Defer(now, TimeSpan.FromHours(1));
            Assert(refresh.NextRunUtc == now.AddHours(1));

            var notReadyProxy = NewRequest("EnableSystemProxy", install.OwnerSid);
            AtomicFile.WriteJson(Path.Combine(options.RequestRoot, notReadyProxy.RequestId + ".json"), notReadyProxy);
            Assert(await controller.ProcessAvailableAsync(CancellationToken.None).ConfigureAwait(false));
            var notReadyProxyResult = JsonSerializer.Deserialize<ControllerRequestResult>(File.ReadAllText(
                Path.Combine(options.ResultRoot, notReadyProxy.RequestId + ".json")), JsonSettings.Strict)!;
            Assert(notReadyProxyResult.Status == "Deferred" && notReadyProxyResult.DetailCode == "StableFrontNotReady");

            var proxyStore = new MemorySystemProxyStore(new SystemProxySnapshot(1, "127.0.0.1:2080", "<local>", null));
            var front = new StableFrontHost(options, frontPort: GetFreeTcpPort(), apiPort: GetFreeTcpPort(),
                allowedTargets: [22086, 22087, 22088, 2080], restorePersistedTarget: false);
            front.SwitchNewConnectionsTo(2080, "ControllerProxyTest");
            using var frontStop = new CancellationTokenSource();
            var frontTask = front.RunAsync(frontStop.Token);
            var frontReadyDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!front.IsServing && DateTimeOffset.UtcNow < frontReadyDeadline)
                await Task.Delay(10).ConfigureAwait(false);
            Assert(front.IsServing);
            var proxyController = new ProtectedController(options, install,
                (_, _) => Task.FromResult(payload.ToArray()), front: front, enforcePrivateAcl: false,
                systemProxyStore: proxyStore, modeProbe: (_, _) => Task.FromResult(true));
            var enableProxy = NewRequest("EnableSystemProxy", install.OwnerSid);
            AtomicFile.WriteJson(Path.Combine(options.RequestRoot, enableProxy.RequestId + ".json"), enableProxy);
            Assert(await proxyController.ProcessAvailableAsync(CancellationToken.None).ConfigureAwait(false));
            var enableProxyResult = JsonSerializer.Deserialize<ControllerRequestResult>(File.ReadAllText(
                Path.Combine(options.ResultRoot, enableProxy.RequestId + ".json")), JsonSettings.Strict)!;
            Assert(enableProxyResult.Status == "Completed" && enableProxyResult.DetailCode == "SelectedRouteVerified" &&
                   proxyStore.Current.ProxyServer == "127.0.0.1:22080" &&
                   JsonSerializer.Deserialize<SanitizedServiceStatus>(File.ReadAllText(options.SanitizedStatusPath), JsonSettings.Strict)!.Mode == "Авто");

            var fetchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetchRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observedController = new ProtectedController(options, install, async (_, token) =>
            {
                fetchEntered.TrySetResult();
                await fetchRelease.Task.WaitAsync(token).ConfigureAwait(false);
                return payload.ToArray();
            }, enforcePrivateAcl: false, systemProxyStore: proxyStore,
                modeProbe: (port, _) => Task.FromResult(port == 22080));
            Assert(await observedController.ProbeAndRecordRouteAsync(22080, CancellationToken.None));
            Assert(!await observedController.ProbeAndRecordRouteAsync(2080, CancellationToken.None));
            using (var activeHealth = JsonDocument.Parse(File.ReadAllText(Path.Combine(options.DataRoot,"state","route-health.v1.json"))))
                Assert(activeHealth.RootElement.GetProperty("ready").GetBoolean() &&
                       activeHealth.RootElement.GetProperty("proxy").GetString() == "127.0.0.1:22080");
            var blockedRefresh = NewRequest("RefreshSubscription", install.OwnerSid);
            AtomicFile.WriteJson(Path.Combine(options.RequestRoot, blockedRefresh.RequestId + ".json"), blockedRefresh);
            using var observationStop = new CancellationTokenSource();
            var blockedWork = observedController.ProcessAvailableAsync(observationStop.Token);
            Task observation = Task.CompletedTask;
            try
            {
                await fetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var beforeObservation = File.ReadAllText(Path.Combine(options.DataRoot,"state","route-health-22080.v1.json"));
                await Task.Delay(20);
                observation = observedController.RunObservationsAsync(observationStop.Token);
                var afterObservation = File.ReadAllText(Path.Combine(options.DataRoot,"state","route-health-22080.v1.json"));
                Assert(!blockedWork.IsCompleted && beforeObservation != afterObservation && proxyStore.Current.ProxyServer == "127.0.0.1:22080");
                fetchRelease.TrySetResult();
                await blockedWork;
            }
            finally
            {
                fetchRelease.TrySetResult();
                observationStop.Cancel();
                try { await observation; } catch (OperationCanceledException) { }
            }

            var restoreProxy = NewRequest("RestoreSystemProxy", install.OwnerSid);
            AtomicFile.WriteJson(Path.Combine(options.RequestRoot, restoreProxy.RequestId + ".json"), restoreProxy);
            Assert(await proxyController.ProcessAvailableAsync(CancellationToken.None).ConfigureAwait(false));
            var restoreProxyResult = JsonSerializer.Deserialize<ControllerRequestResult>(File.ReadAllText(
                Path.Combine(options.ResultRoot, restoreProxy.RequestId + ".json")), JsonSettings.Strict)!;
            Assert(restoreProxyResult.Status == "Completed" && restoreProxyResult.DetailCode == "SystemProxyRestored" &&
                   proxyStore.Current.ProxyServer == "127.0.0.1:2080");
            frontStop.Cancel();
            try { await frontTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

            return new ProtectedControllerTestReceipt("Passed", 38, true, true, true, false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourceBytes);
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(providerBytes);
            var resolved = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(Path.GetFileName(resolved), "^art-vpn-controller-test-[a-f0-9]{32}$", RegexOptions.CultureInvariant) &&
                Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static QueuedRequest NewRequest(string action, string ownerSid) => new(
        1,
        "art-vpn-controller-request",
        Guid.NewGuid().ToString("N"),
        action,
        null,
        null,
        ownerSid,
        1,
        DateTimeOffset.UtcNow.ToString("o"));

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("ProtectedControllerInvariantFailed");
    }
}

internal sealed record ProtectedControllerTestReceipt(
    string Status,
    int Scenarios,
    bool TransactionalStage,
    bool ActiveGenerationPreserved,
    bool SanitizedState,
    bool SecretDisplayed);
