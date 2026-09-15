using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArtSport.Vpn.Shared;

// Оба интерфейса открывают одну диагностику, а не собственный второй VPN.
internal static class VpnMaintenanceLauncher
{
    internal static void OpenDiagnostics()
    {
        const string root = @"C:\Program Files\ART VPN";
        const string relative = "runtime/ARTVpn.Maintenance.exe";
        var path = Path.Combine(root, "runtime", "ARTVpn.Maintenance.exe");
        var manifestPath = Path.Combine(root, "manifest.v1.json");
        foreach (var directory in new[] { root, Path.GetDirectoryName(path)! })
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("MaintenancePathRejected");
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists || manifestInfo.Length is < 1 or > 131072 || manifestInfo.LinkTarget is not null)
            throw new InvalidDataException("MaintenanceManifestRejected");
        using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var entry = json.RootElement.GetProperty("files").EnumerateArray()
            .Single(item => item.GetProperty("path").GetString() == relative);
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || file.Length != entry.GetProperty("bytes").GetInt64())
            throw new InvalidDataException("MaintenanceFileRejected");
        using var source = File.OpenRead(path);
        if (Convert.ToHexString(SHA256.HashData(source)) != entry.GetProperty("sha256").GetString())
            throw new InvalidDataException("MaintenanceFileRejected");
        _ = Process.Start(new ProcessStartInfo(path) { Arguments = "--diagnostics", UseShellExecute = false,
            WorkingDirectory = root }) ?? throw new InvalidOperationException("MaintenanceLaunchFailed");
    }
}
