using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ArtSport.ArtVpn.Setup;

internal sealed record InstallationCheck(string Code, string Level, string Title, string Message, bool RequiresUserAction = false);
internal sealed record InstallationReport(DateTimeOffset AtUtc, InstallationCheck[] Checks,
    bool CanInstall, bool SystemChanged = false, bool ContainsProviderSecret = false);
internal sealed record InstallationFacts(bool WindowsSupported, long FreeBytes, long RequiredBytes,
    bool Existing, int[] OccupiedPorts, string ProxyKind, int? LocalProxyPort,
    bool? DirectHttps, bool? OpenAiHttps, string[] Clients, bool? ServiceAuto,
    bool? ServiceRunning, bool? UserStartup, bool Integrated, bool ProxyChanged);

internal static class InstallationContinuation
{
    internal static bool CodexNeedsAction(string code) => code is not
        ("Ready" or "NotConfigured" or "CodexNotFound" or "ExternalRoute" or "RouteUpdateSuggested" or "RestartSuggested");
    internal static bool Allowed(InstallationReport? report) => report is { CanInstall: true, SystemChanged: false, ContainsProviderSecret: false } &&
        report.Checks.Length > 0 && report.Checks.All(c => c.Level is "Info" or "Warning");

    // A missing OpenAI response before installing a VPN is not itself a reason
    // for an extra click. Explicit action items and failed checks still stop.
    internal static bool Automatic(InstallationReport? report) => Allowed(report) &&
        report!.Checks.All(c => !c.RequiresUserAction);
}

// Отчёт — только разрешённые поля. Не экспортируем proxy/PAC URL, имена
// компьютеров/пользователей, командные строки, переменные окружения или подписки.
internal static class InstallationDiagnostics
{
    internal static readonly int[] ManagedPorts = [22080, 22090, 22086, 22096, 22087, 22097, 22088];
    internal const string InstallRoot = @"C:\Program Files\ART VPN";
    internal const string DataRoot = @"C:\ProgramData\ART VPN";
    private const string InternetKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    internal static async Task<InstallationReport> CollectAsync(string? payload, CancellationToken token)
    {
        var checks = new List<InstallationCheck>();
        long required = 1_000_000_000;
        var minimumBuild = 17763;
        if (payload is not null)
        {
            try
            {
                var verified = await Task.Run(() => PackageVerifier.Verify(payload), token);
                required = Math.Max(required, checked(verified.Manifest.Files.Sum(file => file.Bytes) * 3 + 200_000_000));
                minimumBuild = verified.Manifest.MinimumWindowsBuild;
                checks.Add(new("PackageVerified", "Info", "Установочный пакет", "Файлы и контрольные суммы проверены."));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or OverflowException)
            {
                checks.Add(new("PackageUnverified", "Blocked", "Установочный пакет", "Целостность не подтверждена. Скачайте пакет заново; установка не начнётся."));
            }
        }
        token.ThrowIfCancellationRequested();
        var before = ReadProxy();
        var localPort = LocalProxyPort(before.Server);
        var proxyKind = before.Pac ? "Pac" : !before.Enabled ? "Direct" : localPort.HasValue ? "Loopback" : "External";
        bool? direct = null, openAi = null;
        var directTask = ProbeAsync(new Uri("https://www.microsoft.com/"), null, false, token);
        // Сложные PAC/корпоративные прокси не угадываем и не отправляем им
        // учётные данные. Для локального proxy проверяем именно выбранный порт.
        if (proxyKind is "Direct" or "Loopback")
        {
            var route = proxyKind == "Loopback" ? new Uri($"http://{(before.Server.Contains("[::1]") ? "[::1]" : "127.0.0.1")}:{localPort}") : null;
            var protectedTask = ProbeAsync(new Uri("https://api.openai.com/v1/models"), route, true, token);
            await Task.WhenAll(directTask, protectedTask);
            openAi = await protectedTask;
        }
        direct = await directTask;
        long free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(InstallRoot)!).AvailableFreeSpace; }
        catch (IOException) { }
        int[] ports;
        try { ports = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(endpoint => ManagedPorts.Contains(endpoint.Port)).Select(endpoint => endpoint.Port).Distinct().Order().ToArray(); }
        catch (NetworkInformationException)
        {
            ports = [];
            checks.Add(new("PortsUnverified", "Warning", "Локальные порты", "Windows не дала прочитать список. Установщик повторит обязательную проверку перед записью файлов."));
        }
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ARTVpnService");
        var existing = Directory.Exists(InstallRoot) || Directory.Exists(DataRoot) || service is not null;
        bool? running = null;
        try { running = NativeServiceState.Read("ARTVpnService") == NativeServiceState.Running; }
        catch (System.ComponentModel.Win32Exception) { }
        var integrated = File.Exists(Path.Combine(DataRoot, "state", ArtSport.Vpn.Shared.NativeUiHostBinding.FileName));
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var expectedRun = $"\"{InstallRoot}\\runtime\\ARTVpn.UI.exe\" --start-in-tray";
        var startup = string.Equals(run?.GetValue("ART VPN") as string, expectedRun, StringComparison.OrdinalIgnoreCase);
        var facts = new InstallationFacts(Environment.Is64BitOperatingSystem && Environment.OSVersion.Version.Build >= minimumBuild,
            free, required, existing, ports, proxyKind, localPort, direct, openAi, DetectClients(),
            service is null ? null : service.GetValue("Start") is int start && start == 2,
            running, startup, integrated, before != ReadProxy());
        checks.AddRange(Evaluate(facts));
        var codex = ArtSport.Vpn.Shared.CodexProxyCompatibility.InspectCurrentUser(true);
        checks.Add(new("CodexProxy", InstallationContinuation.CodexNeedsAction(codex.Code) ? "Warning" : "Info",
            "Настройки Codex", codex.Message, InstallationContinuation.CodexNeedsAction(codex.Code)));
        return new(DateTimeOffset.UtcNow, checks.ToArray(), checks.All(check => check.Level != "Blocked"));
    }

    private sealed record ProxyObservation(bool Enabled, string Server, bool Pac);
    private static ProxyObservation ReadProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetKey);
        return new(key?.GetValue("ProxyEnable") is int value && value != 0,
            key?.GetValue("ProxyServer") as string ?? "", !string.IsNullOrWhiteSpace(key?.GetValue("AutoConfigURL") as string));
    }

    internal static int? LocalProxyPort(string value)
    {
        var match = Regex.Match(value, @"^(?:http://)?(?:127\.0\.0\.1|localhost|\[::1\]):([0-9]{1,5})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) && port is >= 1 and <= 65535 ? port : null;
    }

    private static async Task<bool> ProbeAsync(Uri uri, Uri? proxy, bool openAi, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = proxy is not null,
                Proxy = proxy is null ? null : new WebProxy(proxy), UseCookies = false, UseDefaultCredentials = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // Обычная страница 403 и captive portal не означают доступ к OpenAI.
            if (openAi) return response.StatusCode == HttpStatusCode.Unauthorized &&
                response.Content.Headers.ContentType?.MediaType == "application/json";
            return response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { return false; }
    }

    private static string[] DetectClients()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = new[] { "Throne", "Happ", "WireGuard", "OpenVPN", "Amnezia", "Outline", "v2rayN", "Clash" };
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in key?.GetSubKeyNames() ?? [])
                {
                    using var item = key!.OpenSubKey(name);
                    var display = item?.GetValue("DisplayName") as string ?? "";
                    foreach (var product in known)
                        if (display.Contains(product, StringComparison.OrdinalIgnoreCase)) found.Add(product);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        foreach (var product in known)
        {
            // Переносная программа может не иметь записи в установленных.
            // Читаем только имя процесса, не его параметры или закрытый профиль.
            var processes = System.Diagnostics.Process.GetProcessesByName(product);
            if (processes.Length > 0) found.Add(product);
            foreach (var process in processes) process.Dispose();
        }
        return found.Order().ToArray();
    }

    internal static InstallationCheck[] Evaluate(InstallationFacts f)
    {
        var checks = new List<InstallationCheck>
        {
            new("Windows", f.WindowsSupported ? "Info" : "Blocked", "Windows", f.WindowsSupported ? "Версия и разрядность подходят." : "Нужна поддерживаемая 64-разрядная Windows."),
            new("Space", f.FreeBytes >= f.RequiredBytes ? "Info" : "Blocked", "Место на диске", $"Свободно {f.FreeBytes / 1_000_000_000.0:F1} ГБ; для установки и отката нужно {f.RequiredBytes / 1_000_000_000.0:F1} ГБ."),
            new("Ports", f.OccupiedPorts.Length == 0 ? "Info" : f.Existing ? "Warning" : "Blocked", "Локальные порты",
                f.OccupiedPorts.Length == 0 ? "Порты ART VPN свободны. Открывать входящие порты в роутере не нужно." :
                "Заняты: " + string.Join(", ", f.OccupiedPorts) + (f.Existing ? ". Возможна действующая ART VPN; перед обновлением владелец будет проверен." : ". Установка остановлена; чужие процессы не завершаем.")),
            new("Proxy", f.ProxyKind is "Pac" or "External" ? "Warning" : "Info", "Текущее подключение Windows",
                f.ProxyKind switch { "Pac" => "Есть сценарий PAC. Сохраняем его: подключение ART VPN потребует решения владельца сети.",
                    "Loopback" => $"Локальный прокси, порт {f.LocalProxyPort}. Установка не переключает этот канал.",
                    "External" => "Используется внешний или составной прокси. Адрес и авторизацию не сохраняем и не меняем.",
                    _ => "Системный прокси выключен. Это не исключает VPN через сетевой адаптер." }, f.ProxyKind is "Pac" or "External"),
            new("DirectHttps", f.DirectHttps == true ? "Info" : "Warning", "Прямой Интернет",
                f.DirectHttps == true ? "Получен ответ HTTPS Microsoft напрямую." : "Прямой HTTPS не подтверждён. Это не мешает установке; сеть могла ограничить именно проверочный сайт."),
            new("OpenAiHttps", f.OpenAiHttps == true && !f.ProxyChanged ? "Info" : "Warning", "Канал OpenAI / Codex",
                f.ProxyChanged ? "Маршрут изменился во время проверки. Повторите диагностику; старый результат не используем." :
                f.OpenAiHttps == true ? "OpenAI отвечает через выбранный канал. Вход, ответ модели и удалённое подключение Codex проверяются отдельно." :
                f.OpenAiHttps == false ? "OpenAI через выбранный канал не подтверждён. Причина не обязательно в подписке; установка сохраняет текущую сеть." :
                "Сложный прокси/PAC автоматически не проверяется. Не подменяем настройки и не запрашиваем пароль.", f.ProxyChanged),
            new("Clients", "Info", "Другие VPN", f.Clients.Length == 0 ? "Зарегистрированные известные клиенты не найдены. Переносные клиенты могут не отображаться; ART VPN работает самостоятельно." :
                "Найдены: " + string.Join(", ", f.Clients) + ". Наличие программы не означает рабочий резерв. Подписки не читаем, чужой VPN не отключаем."),
            new("Security", "Info", "Доступ и защита", "Для установки службы потребуется разрешение администратора. Проверка не меняет firewall, DNS, маршруты, защиту и входы в аккаунты.")
        };
        if (f.Existing)
        {
            checks.Add(new("ServiceStartup", f.ServiceAuto == true && f.ServiceRunning == true ? "Info" : "Warning", "Автозапуск службы",
                f.ServiceAuto == true && f.ServiceRunning == true ? "Служба запущена, автозапуск включён. Фактический старт после перезагрузки — отдельная проверка." :
                "Служба остановлена или автозапуск не подтверждён. «Восстановить ART VPN» проверит владельца и исправит только собственные записи."));
            checks.Add(new("UserStartup", f.Integrated || f.UserStartup == true ? "Info" : "Warning", "Окно программы",
                f.Integrated ? "Обнаружена привязка к Art Monitor. Второе окно ART VPN не требуется; привязку проверяет восстановление." :
                f.UserStartup == true ? "Запуск в трее при входе записан. Если его отключили в диспетчере задач, включите там вручную." : "Запись запуска в трее отсутствует или отличается. Проверенное восстановление может вернуть собственную запись."));
        }
        return checks.ToArray();
    }

    internal static object Test()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("InstallationDiagnosticsRegression"); count++; }
        var good = new InstallationFacts(true, 3_000_000_000, 1_000_000_000, false, [], "Direct", null, true, true, [], null, null, null, false, false);
        InstallationReport Report(InstallationFacts facts) => new(DateTimeOffset.UtcNow, Evaluate(facts), true);
        Check(InstallationContinuation.Automatic(Report(good)));
        Check(InstallationContinuation.Automatic(Report(good with { OpenAiHttps = false })));
        Check(!InstallationContinuation.Automatic(Report(good with { ProxyKind = "Pac" })));
        Check(!InstallationContinuation.Automatic(Report(good with { ProxyChanged = true })));
        Check(!InstallationContinuation.Allowed(Report(good with { FreeBytes = 0 })));
        Check(!InstallationContinuation.Automatic(new(DateTimeOffset.UtcNow, [], true)));
        Check(!InstallationContinuation.Automatic(Report(good) with { SystemChanged = true }));
        Check(Evaluate(good).All(c => c.Level != "Blocked"));
        Check(Evaluate(good with { WindowsSupported = false }).Single(c => c.Code == "Windows").Level == "Blocked");
        Check(Evaluate(good with { FreeBytes = 1 }).Single(c => c.Code == "Space").Level == "Blocked");
        Check(Evaluate(good with { OccupiedPorts = [22080] }).Single(c => c.Code == "Ports").Level == "Blocked");
        Check(Evaluate(good with { Existing = true, OccupiedPorts = [22080] }).Single(c => c.Code == "Ports").Level == "Warning");
        foreach (var kind in new[] { "Pac", "External" }) Check(Evaluate(good with { ProxyKind = kind, OpenAiHttps = null }).All(c => c.Level != "Blocked"));
        Check(Evaluate(good with { DirectHttps = false, OpenAiHttps = false }).All(c => c.Level != "Blocked"));
        Check(Evaluate(good with { ProxyChanged = true }).Single(c => c.Code == "OpenAiHttps").Level == "Warning");
        Check(Evaluate(good with { Existing = true, ServiceAuto = false }).Single(c => c.Code == "ServiceStartup").Level == "Warning");
        Check(Evaluate(good with { Existing = true, Integrated = true }).Single(c => c.Code == "UserStartup").Level == "Info");
        foreach (var input in new[] { "127.0.0.1:22080", "localhost:2080", "http://127.0.0.1:2080", "[::1]:2080" }) Check(LocalProxyPort(input).HasValue);
        foreach (var input in new[] { "user:secret@127.0.0.1:2080", "https://private.invalid/token", "127.0.0.1:0", "127.0.0.1:65536", "http=127.0.0.1:2080;https=127.0.0.1:2081", "localhost:2080/path" }) Check(LocalProxyPort(input) is null);
        return new { status = "Passed", scenarios = count, externalNetworkUsed = false, systemChanged = false, secretDisplayed = false };
    }
}
