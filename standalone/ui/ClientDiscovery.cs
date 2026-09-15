using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;
using System.ComponentModel;

namespace ArtSport.ArtVpn.Ui;

internal sealed record ClientDiscoveryResult(
    bool Found,
    string Product,
    string ExecutablePath,
    string Source,
    string Version = "",
    bool IsRunning = false);

internal sealed record ClientExecutableCandidate(string Path, string Source, bool IsRunning = false);

internal interface IClientDiscovery
{
    Task<ClientDiscoveryResult> FindAsync(string product, IEnumerable<string> knownPaths);
}

internal sealed class DesktopClientDiscovery : IClientDiscovery
{
    public Task<ClientDiscoveryResult> FindAsync(string product, IEnumerable<string> knownPaths)
    {
        var paths = knownPaths.ToArray();
        return Task.Run(() => ClientDiscovery.Find(product, paths));
    }
}

internal static class ClientDiscovery
{
    public static ClientDiscoveryResult Find(string product, IEnumerable<string> knownPaths)
    {
        var names = ExecutableNames(product);
        return FindFromSources(product, knownPaths, ReadRunningApplications(names),
            ReadInstalledApplications(product, names));
    }

    internal static ClientDiscoveryResult FindFromSources(string product, IEnumerable<string> knownPaths,
        IEnumerable<ClientExecutableCandidate> running, IEnumerable<ClientExecutableCandidate> installed)
    {
        // Уже используемый клиент важнее только что скачанной копии без профиля.
        // Обнаружение не запускает EXE и не читает подписки/параметры процесса.
        return FindCandidates(product, ExecutableNames(product), running.Concat(installed)
            .Concat(knownPaths.Select(path => new ClientExecutableCandidate(path, "стандартная папка"))));
    }

    internal static ClientDiscoveryResult FindCandidates(
        string product,
        IReadOnlySet<string> executableNames,
        IEnumerable<ClientExecutableCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                var value = candidate.Path.Trim().Trim('"');
                if (!IsLocalAbsolutePath(value)) continue;
                var path = Path.GetFullPath(value);
                if (!LocalParentsAreSafe(path)) continue;
                var info = new FileInfo(path);
                if (!info.Exists || info.LinkTarget is not null ||
                    !executableNames.Contains(info.Name) || info.Length == 0)
                    continue;
                var version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
                return new ClientDiscoveryResult(true, product, path, candidate.Source, version, candidate.IsRunning);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or
                UnauthorizedAccessException or Win32Exception or System.Security.SecurityException)
            {
                // An unreadable candidate is ignored; discovery never changes the installation.
            }
        }
        return new ClientDiscoveryResult(false, product, "", "не найден");
    }

    public static ClientDiscoveryResult[] Snapshot() =>
    [
        Find("Throne",
        [
            @"C:\Program Files\Throne\Throne.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Throne", "Throne.exe"),
            @"C:\ProgramData\ART VPN\clients\Throne\1.2.4\Throne.exe"
        ]),
        Find("HAPP",
        [
            @"C:\Program Files\Happ\Happ.exe",
            @"C:\Program Files\Happ Desktop\Happ.exe",
            @"C:\Program Files\FlyFrogLLC\Happ\Happ.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Happ", "Happ.exe")
        ])
    ];

    public static void WriteSanitizedReport(string destination)
    {
        var path = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("DiscoveryReportPathRejected");
        Directory.CreateDirectory(directory);
        var report = Snapshot().Select(item => new
        {
            product = item.Product,
            found = item.Found,
            source = item.Source,
            version = item.Version,
            running = item.IsRunning,
            executableName = item.Found ? Path.GetFileName(item.ExecutablePath) : ""
        });
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static HashSet<string> ExecutableNames(string product) => product switch
    {
        "Throne" => new HashSet<string>(["Throne.exe"], StringComparer.OrdinalIgnoreCase),
        "HAPP" => new HashSet<string>(["Happ.exe", "Happ Desktop.exe"], StringComparer.OrdinalIgnoreCase),
        _ => throw new ArgumentOutOfRangeException(nameof(product))
    };

    private static IEnumerable<ClientExecutableCandidate> ReadRunningApplications(IReadOnlySet<string> executableNames)
    {
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        foreach (var name in executableNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { continue; }
            try
            {
                foreach (var process in processes)
                {
                    string? path = null;
                    try
                    {
                        if (process.SessionId == session && !process.HasExited)
                            path = process.MainModule?.FileName;
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    { /* Процесс мог завершиться или быть недоступен. Никаких повышений прав. */ }
                    if (!string.IsNullOrWhiteSpace(path))
                        yield return new ClientExecutableCandidate(path, "запущен в вашем сеансе", true);
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    internal static bool IsLocalAbsolutePath(string path) => path.Length >= 3 &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] is '\\' or '/');

    private static bool LocalParentsAreSafe(string path)
    {
        // Не обращаться к сетевым дискам/ссылкам из старой записи установщика:
        // недоступная шара не должна подвешивать мастер или вызывать вход в сеть.
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) return false;
        var parents = new Stack<DirectoryInfo>();
        for (var parent = new FileInfo(path).Directory; parent is not null; parent = parent.Parent)
            parents.Push(parent);
        while (parents.TryPop(out var parent))
            if (!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static IEnumerable<ClientExecutableCandidate> ReadInstalledApplications(
        string product,
        IReadOnlySet<string> executableNames)
    {
        var result = new List<ClientExecutableCandidate>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var item = uninstall.OpenSubKey(name);
                    var displayName = item?.GetValue("DisplayName") as string ?? "";
                    if (!displayName.Contains(product, StringComparison.OrdinalIgnoreCase)) continue;
                    var location = item?.GetValue("InstallLocation") as string ?? "";
                    if (!string.IsNullOrWhiteSpace(location))
                        foreach (var executable in executableNames)
                            result.Add(new(Path.Combine(location, executable), "список установленных программ"));
                    var icon = NormalizeDisplayIcon(item?.GetValue("DisplayIcon") as string);
                    if (!string.IsNullOrWhiteSpace(icon)) result.Add(new(icon, "список установленных программ"));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // Read-only discovery is best effort and fail-closed.
            }
        }
        return result;
    }

    private static string NormalizeDisplayIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : "";
        }
        var comma = text.LastIndexOf(',');
        return comma > 2 ? text[..comma].Trim() : text;
    }
}

internal static class ClientDiscoveryTests
{
    public static ClientDiscoveryTestReceipt Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-client-discovery-" + Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }
        try
        {
            Directory.CreateDirectory(root);
            var throne = Path.Combine(root, "Throne.exe");
            File.WriteAllBytes(throne, [1, 2, 3]);
            var found = ClientDiscovery.FindCandidates("Throne",
                new HashSet<string>(["Throne.exe"], StringComparer.OrdinalIgnoreCase),
                [new(throne, "fixture")]);
            Check(found.Found && found.ExecutablePath == throne, "ThroneDiscoveryFailed");
            Check(!found.IsRunning && found.Source == "fixture", "FileMistakenForRunningClient");

            var happ = Path.Combine(root, "Happ.exe");
            File.WriteAllBytes(happ, [4, 5, 6]);
            var happFound = ClientDiscovery.FindCandidates("HAPP",
                new HashSet<string>(["Happ.exe"], StringComparer.OrdinalIgnoreCase),
                [new(happ, "fixture")]);
            Check(happFound.Found, "HappDiscoveryFailed");

            var wrong = Path.Combine(root, "Unknown.exe");
            File.WriteAllBytes(wrong, [7]);
            var rejected = ClientDiscovery.FindCandidates("Throne",
                new HashSet<string>(["Throne.exe"], StringComparer.OrdinalIgnoreCase),
                [new(wrong, "fixture"), new(Path.Combine(root, "missing.exe"), "fixture")]);
            Check(!rejected.Found, "UnknownClientAccepted");
            var stagedRoot = Path.Combine(root, "downloaded");
            Directory.CreateDirectory(stagedRoot);
            var staged = Path.Combine(stagedRoot, "Throne.exe");
            File.WriteAllBytes(staged, [1, 2, 3]);
            var missing = Path.Combine(root, "missing", "Throne.exe");
            var running = ClientDiscovery.FindFromSources("Throne", [staged],
                [new(throne, "running fixture", true)], [new(staged, "installed fixture")]);
            Check(running.ExecutablePath == throne, "DownloadedCopyPreferredOverRunningClient");
            Check(running.IsRunning && running.Source == "running fixture", "RunningSourceLost");
            var registered = ClientDiscovery.FindFromSources("Throne", [staged], [], [new(throne, "registered fixture")]);
            Check(registered.ExecutablePath == throne && !registered.IsRunning, "RegisteredClientNotPreferred");
            var fallback = ClientDiscovery.FindFromSources("Throne", [staged], [new(missing, "stale process", true)], []);
            Check(fallback.ExecutablePath == staged && !fallback.IsRunning, "ExitedProcessBlocksFallback");
            var wrongProcess = ClientDiscovery.FindFromSources("Throne", [staged], [new(wrong, "wrong process", true)], []);
            Check(wrongProcess.ExecutablePath == staged && !wrongProcess.IsRunning, "WrongProcessFileAccepted");
            Check(!ClientDiscovery.FindFromSources("Throne", [missing], [], []).Found, "MissingClientAccepted");
            Check(ClientDiscovery.FindFromSources("Throne", ["\"" + staged + "\""], [], []).Found,
                "QuotedLocalPathRejected");
            var runningHapp = ClientDiscovery.FindFromSources("HAPP", [], [new(happ, "running fixture", true)], []);
            Check(runningHapp.Found && runningHapp.IsRunning, "RunningHappNotFound");
            foreach (var remoteOrRelative in new[]
                { @"\\not-a-real-server.invalid\share\Throne.exe", @"\\?\UNC\server\share\Throne.exe", @"C:Throne.exe", "Throne.exe" })
            {
                Check(!ClientDiscovery.IsLocalAbsolutePath(remoteOrRelative), "UnsafeDiscoveryPathShape");
                Check(!ClientDiscovery.FindFromSources("Throne", [remoteOrRelative], [], []).Found,
                    "UnsafePathWasProbed");
            }
            File.WriteAllBytes(staged, []);
            Check(!ClientDiscovery.FindFromSources("Throne", [staged], [], []).Found, "EmptyDownloadAccepted");
            return new ClientDiscoveryTestReceipt("Passed", checks, true, true, true, false, false);
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetFullPath(root).StartsWith(
                    Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("art-vpn-client-discovery-", StringComparison.Ordinal))
                Directory.Delete(root, true);
        }
    }
}

internal sealed record ClientDiscoveryTestReceipt(
    string Status,
    int Scenarios,
    bool ThroneFound,
    bool HappFound,
    bool UnknownRejected,
    bool SystemChanged,
    bool SecretDisplayed);
