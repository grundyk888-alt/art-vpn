using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ArtSport.ArtVpn.Setup;

// WM_CLOSE can only hide an Electron GUI. Restart Manager requests a real,
// NON-FORCED application shutdown, with PID + creation time binding.
// Never register file paths, services, Explorer or a whole process name.
internal static class CodexRestartManager
{
    internal static bool Request(CodexCloseTarget[] targets)
    {
        var gui = targets.Where(t => t.Launch is not null && CodexGracefulClose.IsStillTarget(t)).ToArray();
        if (gui.Length != 1 || !CodexAppLauncher.CanLaunch(gui[0].Launch!)) return false;
        var unique = new[] { Unique(gui[0]) };
        var key = new StringBuilder(33);
        if (RmStartSession(out var session, 0, key) != 0) return false;
        try
        {
            if (RmRegisterResources(session, 0, null, 1, unique, 0, null) != 0) return false;
            uint count = 0;
            var first = RmGetList(session, out var required, ref count, null, out var reboot);
            if (first != 234 || required != 1 || reboot != 0) return false;
            var affected = new ProcessInfo[1]; count = 1;
            if (RmGetList(session, out required, ref count, affected, out reboot) != 0 || count != 1 || reboot != 0) return false;
            using var self = Process.GetCurrentProcess();
            var item = affected[0];
            if (!SafeAffected(gui[0], item.Process.Id, FileTime(item.Process.Start), item.Session,
                    self.SessionId, item.Type, item.Service) || !CodexGracefulClose.IsStillTarget(gui[0])) return false;
            // 0 is deliberate: RmForceShutdown is NEVER requested. If the app
            // refuses shutdown, leave a short honest manual-restart message.
            return RmShutdown(session, 0, IntPtr.Zero) == 0;
        }
        finally { RmEndSession(session); }
    }

    internal static bool SafeAffected(CodexCloseTarget target, uint id, long fileTime, uint session,
        int currentSession, int type, string? service) => target.Id == id && target.Session == session &&
        target.Session == currentSession && target.Launch is not null && type is 1 or 2 &&
        string.IsNullOrEmpty(service) && new DateTime(target.StartUtcTicks, DateTimeKind.Utc).ToFileTimeUtc() == fileTime;

    private static UniqueProcess Unique(CodexCloseTarget target)
    {
        var time = new DateTime(target.StartUtcTicks, DateTimeKind.Utc).ToFileTimeUtc();
        return new() { Id = (uint)target.Id, Start = new FILETIME { dwLowDateTime = (int)time, dwHighDateTime = (int)(time >> 32) } };
    }
    private static long FileTime(FILETIME time) => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess { public uint Id; public FILETIME Start; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string App;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public int Type; public uint Status; public uint Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern uint RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern uint RmRegisterResources(uint session, uint files, string[]? paths,
        uint apps, UniqueProcess[] processes, uint services, string[]? names);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern uint RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] ProcessInfo[]? info, out uint reboot);
    [DllImport("rstrtmgr.dll")]
    private static extern uint RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")]
    private static extern uint RmEndSession(uint session);
}
