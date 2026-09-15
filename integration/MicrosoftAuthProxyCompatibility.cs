using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;

namespace ArtSport.Vpn.Shared;

internal sealed record MicrosoftAuthPackage(string Family, string Sid);
internal sealed record MicrosoftAuthProxyResult(string Status, int Added, int Present);

internal interface IMicrosoftAuthLoopbackStore
{
    bool Installed(MicrosoftAuthPackage package);
    HashSet<string> Read();
    void Backup(string[] before, MicrosoftAuthPackage[] planned);
    void Add(MicrosoftAuthPackage package);
    void Remove(MicrosoftAuthPackage package);
}

// Windows account dialogs are AppContainers, unlike ordinary browsers. A healthy
// proxy probe therefore does not prove that WAM/CloudExperienceHost can reach it.
// This shared repair changes neither routes nor accounts, tokens or app lifetime.
internal static class MicrosoftAuthProxyCompatibility
{
    // Optional compatibility maintenance must never delay SCM's startup reply.
    // Even a diagnostic-write failure is isolated from the running VPN engine.
    internal static Task StartBackground(Action repair, Action<Exception> report) => Task.Run(() =>
    {
        try { repair(); }
        catch (Exception ex) { try { report(ex); } catch { /* VPN availability wins over diagnostics. */ } }
    });

    internal static readonly MicrosoftAuthPackage[] Packages =
    [
        new("Microsoft.AAD.BrokerPlugin_cw5n1h2txyewy", "S-1-15-2-1910091885-1573563583-1104941280-2418270861-3411158377-2822700936-2990310272"),
        new("Microsoft.AccountsControl_cw5n1h2txyewy", "S-1-15-2-969871995-3242822759-583047763-1618006129-3578262429-3647035748-2471858633"),
        new("Microsoft.Windows.CloudExperienceHost_cw5n1h2txyewy", "S-1-15-2-2434737943-167758768-3180539153-984336765-1107280622-3591121930-2677285773")
    ];

    internal static MicrosoftAuthProxyResult Inspect() => Inspect(new WindowsMicrosoftAuthLoopbackStore());
    internal static MicrosoftAuthProxyResult Ensure()
    {
        var store = new WindowsMicrosoftAuthLoopbackStore();
        var current = Inspect(store);
        if (current.Status == "Ready") return current;
        using var gate = new Mutex(false, @"Global\ARTSPORT.MicrosoftAuthProxyCompatibility.v1");
        bool held;
        try { held = gate.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new TimeoutException("MicrosoftAuthRepairBusy");
        try { return Ensure(store); }
        finally { gate.ReleaseMutex(); }
    }

    internal static MicrosoftAuthProxyResult Inspect(IMicrosoftAuthLoopbackStore store)
    {
        var installed = Packages.Where(store.Installed).ToArray();
        var before = store.Read();
        return new(installed.All(p => before.Contains(p.Sid)) ? "Ready" : "RepairRequired", 0, installed.Length);
    }

    internal static MicrosoftAuthProxyResult Ensure(IMicrosoftAuthLoopbackStore store)
    {
        var installed = Packages.Where(store.Installed).ToArray();
        var before = store.Read();
        var planned = installed.Where(p => !before.Contains(p.Sid)).ToArray();
        if (planned.Length == 0) return new("Ready", 0, installed.Length);
        // Save a new exact receipt before the first privileged change. Never
        // overwrite an earlier backup, enumerate arbitrary apps, or clear the list.
        store.Backup(before.Order(StringComparer.Ordinal).ToArray(), planned);
        var added = new List<MicrosoftAuthPackage>();
        try
        {
            foreach (var package in planned)
            {
                // Another installer may have repaired the same shared package.
                if (store.Read().Contains(package.Sid)) continue;
                added.Add(package); // Also undo a native call that changed state then failed.
                store.Add(package);
                if (!store.Read().Contains(package.Sid)) throw new InvalidOperationException("MicrosoftAuthProxyReadbackFailed");
            }
            var after = store.Read();
            if (!before.All(after.Contains) || !installed.All(p => after.Contains(p.Sid)))
                throw new InvalidOperationException("MicrosoftAuthProxyReadbackFailed");
            return new("Ready", added.Count, installed.Length);
        }
        catch (Exception original)
        {
            // Only our new entries; preserve foreign or pre-existing exceptions.
            // One failed native rollback must not prevent attempts to undo the
            // remaining owned additions. Preserve both causes for diagnostics.
            var errors = new List<Exception> { original };
            foreach (var package in added.AsEnumerable().Reverse())
            {
                try { store.Remove(package); }
                catch (Exception rollback) { errors.Add(rollback); }
            }
            try
            {
                var after = store.Read();
                if (!before.All(after.Contains) || added.Any(p => after.Contains(p.Sid)))
                    errors.Add(new InvalidOperationException("MicrosoftAuthProxyRollbackReadbackFailed"));
            }
            catch (Exception readback) { errors.Add(readback); }
            if (errors.Count > 1)
                throw new InvalidOperationException("MicrosoftAuthProxyRollbackFailed", new AggregateException(errors));
            throw;
        }
    }
}

internal sealed class WindowsMicrosoftAuthLoopbackStore : IMicrosoftAuthLoopbackStore
{
    private const string Publisher = "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    public bool Installed(MicrosoftAuthPackage package)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "SystemApps", package.Family, "AppxManifest.xml");
        if (!File.Exists(path)) return false;
        // Built-in Windows identity package only; do not trust a display name or
        // a third-party package that merely resembles a Microsoft component.
        var identity = XDocument.Load(path).Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "Identity");
        if ((string?)identity?.Attribute("Name") != package.Family.Split('_')[0] ||
            (string?)identity?.Attribute("Publisher") != Publisher)
            throw new InvalidDataException("MicrosoftAuthPackageIdentityRejected");
        return true;
    }

    public HashSet<string> Read()
    {
        uint error = NetworkIsolationGetAppContainerConfig(out uint count, out IntPtr entries);
        if (error != 0) throw new Win32Exception((int)error);
        var result = new HashSet<string>(StringComparer.Ordinal);
        int size = Marshal.SizeOf<SidEntry>();
        try
        {
            for (uint i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<SidEntry>(IntPtr.Add(entries, checked((int)i * size)));
                result.Add(new SecurityIdentifier(entry.Sid).Value);
            }
        }
        finally
        {
            // NetworkIsolationGetAppContainerConfig allocates both SID buffers
            // and the array on the process heap (per the Windows API contract).
            for (uint i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<SidEntry>(IntPtr.Add(entries, checked((int)i * size)));
                _ = HeapFree(GetProcessHeap(), 0, entry.Sid);
            }
            _ = HeapFree(GetProcessHeap(), 0, entries);
        }
        return result;
    }

    public void Backup(string[] before, MicrosoftAuthPackage[] planned)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("MicrosoftAuthProxyAdministratorRequired");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ARTSPORT", "ProxyCompatibility");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("MicrosoftAuthBackupPathRejected");
        var path = Path.Combine(root, "microsoft-auth-" + Guid.NewGuid().ToString("N") + ".json");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, new { schema = 1, atUtc = DateTimeOffset.UtcNow, before, planned,
            scope = "WindowsMicrosoftIdentityLoopbackOnly", proxyChanged = false, accountDataRead = false });
        output.Flush(flushToDisk: true);
    }

    public void Add(MicrosoftAuthPackage package) => Change(package, true);
    public void Remove(MicrosoftAuthPackage package) => Change(package, false);
    private static void Change(MicrosoftAuthPackage package, bool add)
    {
        if (!MicrosoftAuthProxyCompatibility.Packages.Contains(package))
            throw new InvalidOperationException("MicrosoftAuthPackageOutsideAllowlist");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "CheckNetIsolation.exe")) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("LoopbackExempt");
        start.ArgumentList.Add(add ? "-a" : "-d");
        start.ArgumentList.Add("-n=" + package.Family);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("MicrosoftAuthRepairStartFailed");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("MicrosoftAuthRepairTimedOut");
        }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException("MicrosoftAuthRepairRejected");
    }

    [StructLayout(LayoutKind.Sequential)] private struct SidEntry { public IntPtr Sid; public uint Attributes; }
    [DllImport("FirewallAPI.dll")] private static extern uint NetworkIsolationGetAppContainerConfig(out uint count, out IntPtr entries);
    [DllImport("kernel32.dll")] private static extern IntPtr GetProcessHeap();
    [DllImport("kernel32.dll")] private static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);
}
