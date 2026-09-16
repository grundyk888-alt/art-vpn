using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ArtSport.Vpn.Shared;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Ui;

internal sealed class ServiceClient : IArtVpnUiController
{
    private const string ControlPipe = "ARTSPORT.ARTVpn.Control.v1";
    private const string StatusPath = @"C:\ProgramData\ART VPN\state\status.v1.json";
    private const string SupportReportPath = @"C:\ProgramData\ART VPN\reports\latest-support.v1.json";
    private const string ResultRoot = @"C:\ProgramData\ART VPN\state\results";

    public async Task<VpnViewState> GetStatusAsync(CancellationToken cancellationToken)
    {
        var ping = await SendControlAsync("Ping", null, null, cancellationToken).ConfigureAwait(false);
        if (!ping.Success) return Unavailable(ping.Message);
        if (!File.Exists(StatusPath)) return Unavailable("Служба запущена, первичная настройка ещё не завершена.");
        try
        {
            var json = await File.ReadAllTextAsync(StatusPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var status = JsonSerializer.Deserialize<SanitizedStatus>(json, JsonOptions.Strict)
                ?? throw new InvalidDataException();
            var compatibility = CodexProxyCompatibility.InspectCurrentUser(
                status.ServiceAvailable && status.Mode is "Auto" or "Авто" or "ART VPN" or "Throne" or "Happ");
            var route = WindowsRouteStatus.ReadCurrentUser();
            return RouteHealthPresentation.Apply(route.Apply(status.ToViewState()), route) with
                { CodexRouteStatus = compatibility.Message };
        }
        catch
        {
            return Unavailable("Состояние службы пока недоступно. Рабочий канал не изменён.");
        }
    }

    public Task<UiOperationResult> CheckNowAsync(CancellationToken cancellationToken) =>
        SendControlAsync("CheckNow", null, null, cancellationToken);

    public Task<UiOperationResult> CheckUpdatesAsync(CancellationToken cancellationToken) =>
        SendControlAsync("CheckUpdates", null, null, cancellationToken);

    public Task<UiOperationResult> RefreshSubscriptionAsync(CancellationToken cancellationToken) =>
        SendControlAsync("RefreshSubscription", null, null, cancellationToken,
            waitForCompletion: UiRequestPolicy.WaitForCompletion("RefreshSubscription"));

    public Task<UiOperationResult> CollectDiagnosticsAsync(CancellationToken cancellationToken) =>
        SendControlAsync("CollectReport", null, null, cancellationToken);

    public async Task<UiOperationResult> SubmitDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SupportReportPath))
            return new UiOperationResult(false, "Очищенный отчёт ещё не подготовлен.", "SupportReportMissing");
        return await SupportEmailComposer.ComposeAsync(SupportReportPath, cancellationToken).ConfigureAwait(false);
    }

    public Task<UiOperationResult> SetAutoAsync(CancellationToken cancellationToken) =>
        SetModeAsync("Auto", cancellationToken);

    public async Task<UiOperationResult> SetModeAsync(string mode, CancellationToken cancellationToken)
    {
        if (mode is not ("Auto" or "ArtVpn" or "Throne" or "Happ"))
            return new(false, "Этот режим пока не поддерживается.", "ControllerModeRejected");
        var beganAt = DateTimeOffset.UtcNow;
        var result = await SendControlAsync("SetMode", "mode", mode, cancellationToken).ConfigureAwait(false);
        return await CompleteRouteChangeAsync(result, beganAt).ConfigureAwait(false);
    }

    public async Task<UiOperationResult> EnableSystemProxyAsync(CancellationToken cancellationToken)
    {
        var beganAt = DateTimeOffset.UtcNow;
        var result = await SendControlAsync("EnableSystemProxy", null, null, cancellationToken).ConfigureAwait(false);
        return await CompleteRouteChangeAsync(result, beganAt).ConfigureAwait(false);
    }

    public async Task<UiOperationResult> RestoreSystemProxyAsync(CancellationToken cancellationToken)
    {
        var beganAt = DateTimeOffset.UtcNow;
        var result = await SendControlAsync("RestoreSystemProxy", null, null, cancellationToken).ConfigureAwait(false);
        return await CompleteRouteChangeAsync(result, beganAt).ConfigureAwait(false);
    }

    private static Task<UiOperationResult> CompleteRouteChangeAsync(UiOperationResult result, DateTimeOffset beganAt) =>
        RouteChangeAdvisory.CompleteAsync(result,
            () => CodexClientProcesses.IsRunningInCurrentSession(beganAt),
            WinInetSettings.NotifyChanged,
            () => Task.Run(() => CodexProxyCompatibility.RefreshCurrentUser(true)));

    public Task<UiOperationResult> AcquireClientAsync(string client, CancellationToken cancellationToken) =>
        SendControlAsync("AcquireClient", "client", client, cancellationToken);

    public async Task<UiOperationResult> ConfigureProviderAsync(
        SecureString first,
        SecureString confirmation,
        CancellationToken cancellationToken)
    {
        var beganAt = DateTimeOffset.UtcNow;
        var result = await ProviderConnectionClient.ConnectAsync(first, confirmation, cancellationToken).ConfigureAwait(false);
        return await CompleteRouteChangeAsync(new(result.Success, Humanize(result.DetailCode), result.DetailCode), beganAt).ConfigureAwait(false);
    }

    private static async Task<UiOperationResult> SendControlAsync(string action, string? argumentName,
        string? argumentValue,
        CancellationToken cancellationToken,
        bool waitForCompletion = true)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", ControlPipe, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await client.ConnectAsync(5_000, cancellationToken).ConfigureAwait(false);
            var request = new ControlRequest(
                1,
                "art-vpn-control-request",
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"),
                action,
                argumentName is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { [argumentName] = argumentValue ?? "" });
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions.Output) + "\n");
            try
            {
                await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
            var response = await ReadLineAsync(client, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(response);
            var reply = ControlReplyPolicy.Parse(document.RootElement, request.RequestId, action);
            var code = reply.DetailCode;
            if (!reply.Accepted) return new UiOperationResult(false, Humanize(code), code);
            if (!reply.Queued) return new UiOperationResult(true, action == "Ping" ? "Служба отвечает." : Humanize(code), code);
            if (!waitForCompletion)
                return new UiOperationResult(true, Humanize("SubscriptionRefreshQueued"), "SubscriptionRefreshQueued");
            return await WaitForControllerResultAsync(request.RequestId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new UiOperationResult(false, Humanize("ServiceUnavailable"), "ServiceUnavailable");
        }
    }

    private static async Task<UiOperationResult> WaitForControllerResultAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        var resultPath = Path.Combine(ResultRoot, requestId + ".json");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (File.Exists(resultPath))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(resultPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                        var result = JsonSerializer.Deserialize<ControllerResultView>(json, JsonOptions.Strict)
                                     ?? throw new InvalidDataException();
                        if (result.Schema != 1 || result.Kind != "art-vpn-controller-result" ||
                            !string.Equals(result.RequestId, requestId, StringComparison.Ordinal) ||
                            result.ContainsProviderSecret)
                            throw new InvalidDataException();
                        var success = result.Status == "Completed";
                        return new UiOperationResult(success, Humanize(result.DetailCode), result.DetailCode);
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
                    {
                        return new UiOperationResult(false, Humanize("ControllerResultRejected"), "ControllerResultRejected");
                    }
                }
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        return new UiOperationResult(false, Humanize("ControllerResultTimedOut"), "ControllerResultTimedOut");
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count <= 16_384)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0) throw new EndOfStreamException();
            if (one[0] == (byte)'\n') return new UTF8Encoding(false, true).GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        throw new InvalidDataException();
    }

    private static string Humanize(string code) => code switch
    {
        "ExternalTunnelOwnsRoute" => "Этот TUN пока нельзя переключить автоматически. Отключите его в другом VPN и повторите. Настройки и подписка сохранены.",
        "HappControlUnavailable" or "HappRecoveryPending" or "HappRollbackNotVerified" => "Не удалось подтвердить управление HAPP. Проверьте его подключение; настройки HAPP не сбрасывались.",
        "ExternalTunnelCheckUnavailable" => "Не удалось проверить маршруты другого VPN. Подписка сохранена; сеть не изменена. Повторите подключение после проверки режима другого VPN.",
        "ProviderConnectedAuto" => "ART VPN подключён. Режим «Авто» включён. Если открытый Codex ещё не отвечает, закройте и снова откройте его: прежние соединения могут оставаться на другом VPN.",
        "ProvisionProtocolRejected" => "Служба ART VPN устарела. Обновите установку, чтобы сохранять подписку и подключаться одним действием.",
        "FirstConnectionTimedOut" => "Не удалось подключиться за время проверки. Подписка сохранена. Проверьте Интернет или другой VPN и повторите настройку; «Авто» отдельно нажимать не требуется.",
        "OperationCanceled" or "OperationCanceledException" => "Подключение не завершено: время проверки истекло или выбран другой режим. Проверьте текущий канал и повторите подключение при необходимости.",
        "ProviderAlreadyConfigured" => "Подписка уже сохранена. Для замены откройте настройки подписки.",
        "ProviderConfirmationDiffers" => "Ссылки не совпадают. Проверьте ввод ещё раз.",
        "ProviderValueRejected" => "Нужна корректная защищённая ссылка HTTPS.",
        "CallerRejected" => "Этот пользователь не имеет права менять настройку ART VPN.",
        "SupportEndpointNotConfigured" => "Адрес разработчиков ещё не подключён. Отчёт сохранён только на компьютере.",
        "SupportUploadRejected" => "Сервер разработчиков не принял отчёт. Он сохранён локально — попробуйте позже.",
        "SupportDeliveryTimedOut" => "Сервер не подтвердил отправку. Отчёт сохранён локально.",
        "SupportReceiptRejected" => "Ответ сервера не прошёл безопасную проверку. Отчёт сохранён локально.",
        "ClientDownloadedAndReady" => "Файлы клиента скачаны и проверены. Подписка и подключение резерва ещё не настроены.",
        "ClientInstallerDownloaded" => "Установщик HAPP скачан и проверен. Далее Windows может запросить разрешение на установку.",
        "ClientArchiveShapeRejected" => "Официальный пакет изменил структуру и был безопасно отклонён. Текущая сеть не изменена.",
        "ClientAssetHashRejected" => "Контрольная сумма загрузки не совпала. Файл удалён, текущая сеть не изменена.",
        "ClientHttpRejected" => "Официальный сервер загрузки не ответил. Попробуйте позже.",
        "ClientDownloadPacUnsupported" => "В Windows настроен сценарий прокси. Скачайте клиент через официальный сайт в браузере; сетевые настройки не изменены.",
        "ClientDownloadRouteUnsupported" => "Не удалось безопасно использовать текущий прокси для загрузки. Откройте официальный сайт в браузере.",
        "HappReleasePendingAcceptance" => "Для HAPP открывается проверенная официальная страница загрузки.",
        "SelectedRouteVerified" => "Выбранный маршрут проверен и подключён. Приложения не перезапускались.",
        "SelectedRouteAlreadyVerified" => "Выбранный маршрут уже работает и прошёл проверку. Повторного переключения не было.",
        "SelectedRoutePreflightFailed" => "Новый канал не прошёл проверку связи. Текущее подключение сохранено; причина записана в диагностике.",
        "SelectedRoutePostflightFailed" => "Новый маршрут не подтвердил работу. Выполнен безопасный возврат, если настройку не изменила другая программа.",
        "ControlRequestPreemptedByManualChoice" => "Проверка отложена: сначала выполняется ваш ручной выбор подключения.",
        "SubscriptionRefreshPausedByManualRoute" => "Выбран внешний VPN. Фоновая проверка ART VPN приостановлена до включения Auto или ART VPN.",
        "ExternalRoutePreservedAfterFailedSwitch" => "Во время проверки подключение изменилось вне ART VPN. Ваш новый маршрут сохранён.",
        "RouteSwitchNeedsReconciliation" => "Переключение требует проверки состояния. ART VPN не будет принудительно возвращать свой маршрут.",
        "HappRollbackRouteUnconfirmed" => "HAPP включён обратно, но связь пока не подтверждена. Откройте диагностику или проверьте подключение в HAPP.",
        "SystemProxyEnabled" => "Windows подключена к стабильному входу ART VPN. Российские сайты, Avito и Ozon идут напрямую.",
        "SystemProxyAlreadyEnabled" => "Windows уже использует стабильный вход ART VPN.",
        "SystemProxyRestored" => "Прежняя настройка подключения Windows восстановлена точно.",
        "SystemProxyAlreadyRestored" => "Прежняя настройка уже восстановлена.",
        "SystemProxyNotManaged" => "ART VPN не менял системное подключение — возвращать нечего.",
        "SystemProxyExternalOwnerPreserved" => "Подключение уже изменено другой программой. ART VPN его не перезаписал.",
        "SystemProxyPacConflict" => "Обнаружен сценарий автоматической настройки прокси. ART VPN ничего не изменил — сначала нужна отдельная проверка.",
        "SystemProxyOwnershipLost" => "Настройка прокси изменилась вне ART VPN. Текущее подключение сохранено без изменений.",
        "StableFrontNotReady" => "Стабильный вход ART VPN ещё не готов. Текущее подключение не изменено.",
        "SanitizedReportReady" => "Очищенный отчёт готов локально.",
        "HealthCheckScheduledWithoutCutover" => "Проверка выполнена без прерывания рабочего канала.",
        "ProviderGenerationQualifiedAndActivated" => "Новый список проверен и безопасно принят.",
        "ProviderGenerationStagedForQualification" => "Новый список получен и проверяется отдельно от рабочего канала.",
        "SubscriptionRefreshQueued" => "Новый список проверяется в фоне. Рабочий канал продолжает работать.",
        "QualifiedPoolEmpty" or "PrefilterEmpty" => "Ни один проверенный узел не прошёл все проверки доступа и стабильности. Причина уточняется; это не доказательство истечения подписки.",
        "QualifiedPoolInsufficient" or "PrefilterInsufficient" => "В подписке не хватило совместимых безопасных узлов. Рабочий канал сохранён.",
        "ProviderSubscriptionExpired" => "По данным провайдера срок VPN-подписки истёк. Проверьте её продление в личном кабинете.",
        "ProviderSubscriptionQuotaExceeded" => "По данным провайдера исчерпан лимит трафика. Проверьте пакет или дату восстановления лимита.",
        "ProviderClockMismatch" => "Время Windows отличается от времени сервера. Пока нельзя надёжно подтвердить срок подписки.",
        "ProviderSubscriptionAccessRejected" => "Сервер отклонил доступ к подписке. Истечение срока не подтверждено; проверьте ссылку и личный кабинет.",
        "ProviderSubscriptionLinkRejected" => "Ссылка подписки больше не существует. Запросите новую у провайдера.",
        "ProviderSupportedNodesInsufficient" => "Подписка получена, но совместимых рабочих узлов недостаточно.",
        "ProviderPayloadEmpty" => "Провайдер вернул пустой список. Проверьте срок подписки.",
        "ProviderEncodingRejected" => "Провайдер вернул неподдерживаемый ответ вместо списка VPN-узлов.",
        "ProviderHttpRejected" => "Сервер подписки временно не ответил. Текущий рабочий пул сохранён.",
        "QueuedForProtectedController" => "Операция принята защищённой службой.",
        "ControllerResultRejected" => "Ответ службы не прошёл проверку. Проверьте текущий канал перед повтором.",
        "ControllerResultTimedOut" or "ServiceUnavailable" => "Нет подтверждения службы. Проверьте текущий канал перед повтором.",
        _ => "Результат операции не подтверждён. Проверьте текущий канал перед повтором."
    };

    private static VpnViewState Unavailable(string explanation) => new(
        VpnHealth.Unavailable,
        "Требуется настройка",
        explanation,
        "Авто",
        "—",
        null,
        "Не проверено",
        "—",
        "—",
        "—",
        "Рабочий канал не изменён",
        [],
        "Не настроена",
        "—",
        "Не проверен",
        "Нет данных",
        "0.1.0",
        false,
        false);

    private sealed record ControlRequest(
        int Schema,
        string Kind,
        string RequestId,
        string Nonce,
        string Action,
        Dictionary<string, string> Arguments);

    private sealed record ControllerResultView(
        int Schema,
        string Kind,
        string RequestId,
        string Status,
        string DetailCode,
        string GenerationId,
        string AtUtc,
        bool ActiveChannelChanged,
        bool ContainsProviderSecret);
}

internal static class UiRequestPolicy
{
    public static bool WaitForCompletion(string action) =>
        !string.Equals(action, "RefreshSubscription", StringComparison.Ordinal);
}

internal static class WinInetSettings
{
    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;

    public static void NotifyChanged()
    {
        _ = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);
}

internal static class SecureStringUtf8
{
    public static byte[] Copy(SecureString value)
    {
        var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.SecureStringToGlobalAllocUnicode(value);
            var chars = new char[value.Length];
            for (var index = 0; index < chars.Length; index++) chars[index] = (char)Marshal.ReadInt16(pointer, index * 2);
            try { return new UTF8Encoding(false, true).GetBytes(chars); }
            finally { Array.Clear(chars); }
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
    }
}

internal sealed record SanitizedStatus(
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
    NodeView[] Nodes,
    string Subscription,
    string SubscriptionAtUtc,
    string Bypass,
    string Update,
    string Version,
    bool ServiceAvailable,
    bool SafeRepairAvailable,
    bool BrowserDegraded = false)
{
    public VpnViewState ToViewState()
    {
        if (!Enum.TryParse<VpnHealth>(Health, ignoreCase: false, out var parsedHealth) || !Enum.IsDefined(parsedHealth))
            parsedHealth = VpnHealth.Unavailable;
        var headline = BrowserDegraded && parsedHealth is VpnHealth.Healthy or VpnHealth.Fallback
            ? "VPN работает, но Google требует проверки" : parsedHealth switch
        {
            VpnHealth.Healthy => Nodes.Length < 2 ? "VPN работает, но резерва пока нет" : "VPN работает стабильно",
            VpnHealth.Checking => "Проверяем новый канал",
            VpnHealth.Fallback => "Работает резервный канал",
            VpnHealth.Stopped => "VPN не подключён",
            _ => "Состояние требует внимания"
        };
        return new VpnViewState(parsedHealth, headline,
            parsedHealth == VpnHealth.Checking ? "Текущий канал продолжает работать." : LastSwitchReason,
            Mode, Country,
            DateTimeOffset.TryParse(SelectedAtUtc, out var selected) ? selected : null,
            Quality, Latency, StatusText.Stability(Stability), StatusText.LastSwitch(LastSwitchAtUtc), LastSwitchReason, Nodes,
            Subscription, SubscriptionAtUtc, Bypass, Update, Version, ServiceAvailable, SafeRepairAvailable);
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Output = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
}
