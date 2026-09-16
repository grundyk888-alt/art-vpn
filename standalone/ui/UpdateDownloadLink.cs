using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;

namespace ArtSport.ArtVpn.Ui;

internal static class UpdateDownloadLink
{
    internal const string InstallRoot = @"C:\Program Files\ART VPN";
    internal const string StateRoot = @"C:\ProgramData\ART VPN\state";
    internal static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ART VPN", "updates");

    public static void Offer(IWin32Window owner)
    {
        try
        {
            // A copy outside InstallRoot survives replacement of the old UI and
            // retains the user's normal token to reopen the new UI unelevated.
            var directory = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            RejectReparse(directory);
            var worker = Path.Combine(directory, "ARTVpn.Update.exe");
            File.Copy(Path.Combine(InstallRoot, "runtime", "ARTVpn.UI.exe"), worker, false);
            using var started = Process.Start(new ProcessStartInfo(worker) { UseShellExecute = false, ArgumentList = { "--apply-update" } });
            if (started is null) throw new IOException();
        }
        catch
        {
            MessageBox.Show(owner, "Не удалось запустить обновление. Текущий VPN не изменён.",
                "ART VPN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static void RejectReparse(string directory)
    {
        for (var item = new DirectoryInfo(directory); item is not null; item = item.Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("UpdateReparseRejected");
    }

    internal static int RunWorker()
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath!)!;
        if (!string.Equals(Path.GetDirectoryName(directory), CacheRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) return 25;
        RejectReparse(directory);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(InstallRoot, "manifest.v1.json")));
        var expected = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Single(f => f.GetProperty("path").GetString() == "runtime/ARTVpn.UI.exe").GetProperty("sha256").GetString();
        using (var self = File.OpenRead(Environment.ProcessPath!))
            if (Convert.ToHexString(SHA256.HashData(self)) != expected) return 25;
        using var mutex = new Mutex(false, @"Local\ARTSPORT.ARTVpn.Update.v1");
        bool owns;
        try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
        if (!owns) return 24;
        try { using var form = new ProductUpdateForm(directory); Application.Run(form); return form.Succeeded ? 0 : 1; }
        finally { mutex.ReleaseMutex(); }
    }

    internal static void CleanupCompleted()
    {
        if (!Directory.Exists(CacheRoot)) return;
        try
        {
            RejectReparse(CacheRoot);
            foreach (var directory in Directory.EnumerateDirectories(CacheRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || !File.Exists(Path.Combine(directory, "completed"))) continue;
                RejectReparse(directory);
                // Remove only this updater's exact files; retain unknown data.
                foreach (var name in new[] { "package.zip", "ART-VPN-Setup.exe", "ARTVpn.Update.exe" })
                    UpdatePackageTransfer.TryDelete(Path.Combine(directory, name));
                if (Directory.GetFiles(directory).Select(Path.GetFileName).SequenceEqual(new[] { "completed" }))
                { File.Delete(Path.Combine(directory, "completed")); Directory.Delete(directory); }
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
