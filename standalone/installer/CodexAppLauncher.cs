using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Setup;

internal sealed record CodexLaunchTarget(string Executable, string ApplicationId);

// Capture only from the already running GUI, never from registry search results,
// a supplied command line, subscription, downloaded script or a stored password.
internal static class CodexAppLauncher
{
    internal static CodexLaunchTarget? Capture(Process process, bool requireWindow = true)
    {
        try
        {
            if (requireWindow && process.MainWindowHandle == IntPtr.Zero) return null;
            var path = process.MainModule?.FileName ?? "";
            if (!ValidExecutable(path) || !IsGuiExecutable(path)) return null;
            uint length = 0;
            var app = "";
            if (GetApplicationUserModelId(process.Handle, ref length, null) == 122 && length is > 1 and <= 512)
            {
                var text = new StringBuilder((int)length);
                if (GetApplicationUserModelId(process.Handle, ref length, text) == 0) app = text.ToString();
            }
            return new(path, app);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { return null; }
    }

    internal static bool ValidApplicationId(string id) => Regex.IsMatch(id,
        @"^[A-Za-z0-9][A-Za-z0-9_.!+\-]{1,255}$", RegexOptions.CultureInvariant);

    internal static bool ValidExecutable(string path) => Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
        (Path.GetFileName(path).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Equals("Codex.exe", StringComparison.OrdinalIgnoreCase));

    private static bool IsGuiExecutable(string path)
    {
        using var file = File.OpenRead(path);
        using var pe = new PEReader(file);
        return pe.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsGui;
    }

    internal static bool CanLaunch(CodexLaunchTarget target) => ValidApplicationId(target.ApplicationId) ||
        !InstallTransaction.IsAdministrator() && ValidExecutable(target.Executable) && File.Exists(target.Executable) && IsGuiExecutable(target.Executable);

    internal static bool Launch(CodexLaunchTarget target)
    {
        if (!CanLaunch(target)) return false;
        uint pid;
        if (ValidApplicationId(target.ApplicationId))
        {
            // Windows activates a packaged app in the current session and also
            // resolves its current installed version after a Store update.
            var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true)!;
            var instance = Activator.CreateInstance(type)!;
            try
            {
                var result = ((IApplicationActivationManager)instance).ActivateApplication(target.ApplicationId, "", 2, out pid);
                if (result < 0) return false;
            }
            finally { Marshal.ReleaseComObject(instance); }
        }
        else
        {
            // Do not inherit elevated installer rights into a Win32 client.
            if (InstallTransaction.IsAdministrator()) return false;
            using var launched = Process.Start(new ProcessStartInfo(target.Executable)
                { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target.Executable)! });
            if (launched is null) return false;
            pid = (uint)launched.Id;
        }
        try
        {
            using var client = Process.GetProcessById((int)pid);
            using var self = Process.GetCurrentProcess();
            // Activation returns a PID before the GUI is ready. Do not report
            // "opened" while only startup helpers exist. Runs on Task.Run.
            for (var attempt = 0; attempt < 60; attempt++)
            {
                client.Refresh();
                if (!ArtSport.Vpn.Shared.CodexClientProcesses.Matches(client.ProcessName, client.SessionId, self.SessionId, client.HasExited)) return false;
                if (client.MainWindowHandle != IntPtr.Zero) return true;
                Thread.Sleep(250);
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? value);

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
}

internal static class CodexRestartSequence
{
    internal static async Task<string> RunAsync(Func<bool> requestClose, Func<bool> oldStillRunning,
        Func<bool> newAlreadyRunning, Func<bool> launch, Func<Task>? delay = null)
    {
        delay ??= () => Task.Delay(250);
        if (!requestClose()) return "VPN подключён. Переоткройте Codex вручную.";
        for (var i = 0; i < 60 && oldStillRunning(); i++) await delay();
        if (oldStillRunning()) return "VPN подключён. Codex не закрылся — переоткройте его вручную.";
        if (newAlreadyRunning()) return "Codex уже открыт. Проверьте связь.";
        return launch() ? CodexGracefulClose.RestartedMessage : "VPN подключён. Откройте Codex вручную.";
    }
}
