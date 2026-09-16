using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ArtSport.Vpn.Shared;

internal sealed record CodexProxyResult(string Code, string Message, bool Changed = false);
internal sealed record CodexProxyPlan(string Text, int Removed, bool CustomOverride);

// Shared by ART Monitor and the standalone UI. This does not own the VPN,
// change the system proxy, restart applications, or modify installed Codex files.
internal static class CodexProxyCompatibility
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<int> ManagedPorts = [2080, 10809, 22080, 22086, 22087, 22088];
    private static readonly string[] ProxyNames = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"];
    private static readonly Regex Assignment = new(
        @"^[ \t]*(?:export[ \t]+)?(?<key>HTTP_PROXY|HTTPS_PROXY|ALL_PROXY)[ \t]*=[ \t]*(?<value>.*?)[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // WebSocket transports do not reliably follow WinINET. Keep an explicit
    // HTTP proxy to the approved front, never a country/backend-specific port.
    internal static CodexProxyPlan Analyze(string text, string proxy = "http://127.0.0.1:22080")
    {
        if (proxy is not ("http://127.0.0.1:22080" or "http://127.0.0.1:2080" or "http://127.0.0.1:10809"))
            throw new ArgumentException("Only a verified ART/external loopback entry is allowed", nameof(proxy));
        var output = new StringBuilder();
        var removed = 0;
        var custom = false;
        foreach (var line in Regex.Split(text, @"(?<=\n)"))
        {
            var match = Assignment.Match(line.TrimEnd('\r', '\n'));
            if (!match.Success) { output.Append(line); continue; }
            var value = match.Groups["value"].Value.Trim();
            if (value.StartsWith('"') || value.StartsWith('\''))
            {
                var close = value.IndexOf(value[0], 1);
                if (close < 0 || (value[(close + 1)..].Trim() is var tail && tail.Length > 0 && !tail.StartsWith('#')))
                { custom = true; output.Append(line); continue; }
                value = value[1..close];
            }
            else value = Regex.Replace(value, @"[ \t]+#.*$", "").Trim();
            if (IsManagedProxy(value)) { removed++; continue; }
            if (value.Length > 0) custom = true;
            output.Append(line);
        }
        // An explicit non-ART setting may be a deliberate corporate policy.
        // Do not partially rewrite a mixed configuration.
        if (custom) return new(text, 0, true);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        if (output.Length > 0 && output[^1] != '\n') output.Append(newline);
        if (!Regex.IsMatch(text, @"(?im)^[ \t]*(?:export[ \t]+)?NO_PROXY[ \t]*="))
            output.Append("NO_PROXY=localhost,127.0.0.1,::1").Append(newline);
        foreach (var name in ProxyNames) output.Append(name).Append('=').Append(proxy).Append(newline);
        return new(output.ToString(), removed, false);
    }

    private static bool IsManagedProxy(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" or "socks5" or "socks5h" &&
        uri.Host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "[::1]" or "::1" &&
        ManagedPorts.Contains(uri.Port) && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath is "" or "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    internal static void PrepareHelperEnvironment(ProcessStartInfo start)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var server = Convert.ToString(key?.GetValue("ProxyServer")) ?? "";
        if (Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) != 1 ||
            !string.IsNullOrWhiteSpace(Convert.ToString(key?.GetValue("AutoConfigURL"))) ||
            server is not ("127.0.0.1:22080" or "127.0.0.1:2080" or "127.0.0.1:10809")) return;
        PrepareHelperEnvironment(start, "http://" + server);
    }

    internal static void PrepareHelperEnvironment(ProcessStartInfo start, string proxy)
    {
        if (proxy is not ("http://127.0.0.1:22080" or "http://127.0.0.1:2080" or "http://127.0.0.1:10809")) return;
        var keys = start.Environment.Keys.Where(name => ProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (keys.Any(name => !string.IsNullOrWhiteSpace(start.Environment[name]) && !IsManagedProxy(start.Environment[name]!))) return;
        foreach (var name in keys) start.Environment.Remove(name);
        foreach (var name in ProxyNames) start.Environment[name] = proxy;
        if (!start.Environment.Keys.Any(name => name.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase)))
            start.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
    }

    // Status polling is observational. Migration belongs to a successful,
    // explicit route command, never to opening or refreshing a window.
    public static CodexProxyResult InspectCurrentUser(bool routeAuthorized) => CheckCurrentUser(routeAuthorized, false);

    public static CodexProxyResult RefreshCurrentUser(bool autoAuthorized) => CheckCurrentUser(autoAuthorized, true);

    private static CodexProxyResult CheckCurrentUser(bool routeAuthorized, bool migrate)
    {
        if (!routeAuthorized) return new("Inactive", "Настройки Codex сохранены; маршрут ART VPN не выбран.");
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enabled = Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) == 1;
            var proxy = Convert.ToString(key?.GetValue("ProxyServer")) ?? "";
            if (!enabled || proxy is not ("127.0.0.1:22080" or "127.0.0.1:2080" or "127.0.0.1:10809"))
                return new("ExternalRoute", "Подхват Codex: Windows сейчас использует другой маршрут; настройки сохранены.");
            if (!string.IsNullOrWhiteSpace(Convert.ToString(key?.GetValue("AutoConfigURL"))))
                return new("PacConflict", "Подхват Codex: обнаружен сценарий прокси Windows; нужна отдельная проверка.");
            foreach (var name in ProxyNames)
                foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name, target)))
                        return new("EnvironmentOverride", "Для Codex задан отдельный прокси Windows. Автоматически чужие настройки не изменяем.");

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            var root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(profile, ".codex") : configured);
            if (!root.StartsWith(Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return new("ExternalCodexHome", "Папка Codex находится вне профиля пользователя; настройки сохранены.");
            lock (Gate)
            {
                if (!Directory.Exists(root)) return new("CodexNotFound", "Codex ещё не настроен; папка не создавалась.");
                if (migrate)
                    using (var client = new System.Net.Sockets.TcpClient())
                        if (!client.ConnectAsync("127.0.0.1", int.Parse(proxy.Split(':')[1])).Wait(1500))
                            return new("ProxyUnavailable", "Вход VPN недоступен; настройки Codex сохранены.");
                var result = migrate
                    ? PrepareFile(Path.Combine(root, ".env"), "http://" + proxy)
                    : InspectFile(Path.Combine(root, ".env"), "http://" + proxy);
                if (result.Code is not ("Ready" or "Migrated")) return result;
                var receipt = Path.Combine(root, ".art-vpn-proxy-migration.json");
                if (File.Exists(receipt) && !IsReparse(receipt) && new FileInfo(receipt).Length < 4096)
                {
                    var at = JsonSerializer.Deserialize<DateTimeOffset>(File.ReadAllText(receipt, StrictUtf8));
                    if (CodexClientProcesses.IsRunningInCurrentSession(at))
                        return new("RestartSuggested", "Маршрут Codex исправлен. Один раз закройте и откройте Codex / ChatGPT, чтобы пересоздать соединения; VPN не выключайте.", result.Changed);
                }
                return result;
            }
        }
        catch
        {
            // Do not include file content, account names, paths or proxy secrets.
            return new("CheckUnavailable", "Подхват Codex пока не проверен; текущий VPN и приложение не изменены.");
        }
    }

    internal static CodexProxyResult InspectFile(string path, string proxy)
    {
        path = Path.GetFullPath(path);
        for (var directory = Path.GetDirectoryName(path); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            if (Directory.Exists(directory) && IsReparse(directory))
                return new("LinkedPath", "Настройки Codex перенаправлены; автоматическая проверка пропущена.");
        if (!File.Exists(path))
            return new("NotConfigured", "Отдельный прокси Codex не задан. Наличие удалённого доступа проверяется в самом Codex.");
        if (IsReparse(path) || new FileInfo(path).Length > 131072)
            return new("UnsafeFile", "Настройки Codex требуют отдельной проверки; файл не изменён.");
        var original = File.ReadAllBytes(path);
        var bom = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        string text;
        try { text = StrictUtf8.GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0)); }
        catch (DecoderFallbackException) { return new("Encoding", "Неизвестная кодировка настроек Codex; файл сохранён без изменений."); }
        var plan = Analyze(text, proxy);
        if (plan.CustomOverride)
            return new("CustomOverride", "Codex использует отдельный прокси. Его настройки не изменены.");
        return plan.Text == text ? Ready() : new("RouteUpdateSuggested",
            "Настройка Codex отличается от выбранного входа. Она обновляется после успешного переключения кнопкой; просмотр статуса её не меняет.");
    }

    internal static CodexProxyResult PrepareFile(string path, string proxy = "http://127.0.0.1:22080")
    {
        path = Path.GetFullPath(path);
        for (var directory = Path.GetDirectoryName(path); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            if (Directory.Exists(directory) && IsReparse(directory))
                return new("LinkedPath", "Подхват Codex: папка перенаправлена; автоматическое изменение пропущено.");
        var existed = File.Exists(path);
        if (!Directory.Exists(Path.GetDirectoryName(path))) return new("MissingDirectory", "Папка Codex не найдена; настройки не изменены.");
        if (existed && (IsReparse(path) || new FileInfo(path).Length > 131072))
            return new("UnsafeFile", "Настройки Codex требуют отдельной проверки; файл не изменён.");
        var original = existed ? File.ReadAllBytes(path) : Array.Empty<byte>();
        var bom = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        string text;
        try { text = StrictUtf8.GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0)); }
        catch (DecoderFallbackException) { return new("Encoding", "Неизвестная кодировка настроек Codex; файл сохранён без изменений."); }
        var plan = Analyze(text, proxy);
        if (plan.CustomOverride) return new("CustomOverride", "Codex использует отдельный прокси. Он сохранён; для подхвата ART VPN нужна проверка этой настройки.");
        if (plan.Text == text) return Ready();
        var suffix = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        var temporary = path + ".art-vpn-" + suffix + ".tmp";
        var backup = path + ".art-vpn-backup-" + suffix;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (bom) stream.Write(new byte[] { 0xef, 0xbb, 0xbf });
                stream.Write(StrictUtf8.GetBytes(plan.Text));
                stream.Flush(true);
            }
            if (File.Exists(path) != existed || (existed && !File.ReadAllBytes(path).AsSpan().SequenceEqual(original)))
                return new("ConcurrentChange", "Настройки Codex меняются другой программой; повторим проверку позже.");
            // File.Replace preserves Windows metadata and keeps the exact old
            // file next to the original, under the same user's protected folder.
            if (existed) File.Replace(temporary, path, backup, ignoreMetadataErrors: false);
            else File.Move(temporary, path, overwrite: false);
            var verified = StrictUtf8.GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
            if (Analyze(verified, proxy).Text != verified)
                return new("VerificationFailed", "Исправление подхвата Codex не подтверждено; резервная копия сохранена.", true);
            var receipt = Path.Combine(Path.GetDirectoryName(path)!, ".art-vpn-proxy-migration.json");
            if (new FileInfo(receipt).LinkTarget is null && (!File.Exists(receipt) || !IsReparse(receipt)))
                File.WriteAllText(receipt, JsonSerializer.Serialize(DateTimeOffset.UtcNow), StrictUtf8);
            return new("Migrated", "Для следующего запуска Codex сохранён выбранный вход VPN. Уже открытый Codex может потребовать перезапуска; готовность удалённого доступа проверяется отдельно.", true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static CodexProxyResult Ready() => new("Ready", "Прокси в настройках Codex совпадает с выбранным входом. Связь и удалённый доступ проверяются отдельно.");
}

// Новый пакет OpenAI.Codex запускает ChatGPT.exe, старый — Codex.exe.
// Это только подсказка пользователю: процессы и их соединения не завершаем.
internal static class CodexClientProcesses
{
    internal static bool Matches(string name, int session, int currentSession, bool exited) =>
        !exited && session == currentSession &&
        (name.Equals("codex", StringComparison.OrdinalIgnoreCase) || name.Equals("chatgpt", StringComparison.OrdinalIgnoreCase));

    internal static bool IsRunningInCurrentSession(DateTimeOffset? startedBefore = null)
    {
        using var current = Process.GetCurrentProcess();
        foreach (var name in new[] { "codex", "ChatGPT" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                    try
                    {
                        if (Matches(process.ProcessName, process.SessionId, current.SessionId, process.HasExited) &&
                            (startedBefore is null || process.StartTime.ToUniversalTime() < startedBefore.Value.UtcDateTime)) return true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }
}
