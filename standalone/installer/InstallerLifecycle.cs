using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Setup;

internal static class ExistingVersionPolicy
{
    public static string Decide(string installed, string candidate, string installedHash, string candidateHash)
    {
        static (Version Main, long Beta) Parse(string value)
        {
            var m = Regex.Match(value, @"^(\d+\.\d+\.\d+)(?:-beta\.(\d+))?$");
            var beta = long.MaxValue; // stable outranks every prerelease of the same version
            if (!m.Success || !Version.TryParse(m.Groups[1].Value, out var main) ||
                (m.Groups[2].Success && !long.TryParse(m.Groups[2].Value, out beta))) throw new InvalidDataException("InstallVersionRejected");
            return (main,beta);
        }
        var old = Parse(installed); var next = Parse(candidate);
        if (installed == candidate)
        {
            if (installedHash != candidateHash) throw new InvalidDataException("SameVersionPayloadMismatch");
            return "AlreadyInstalledVerified";
        }
        var compare = old.Main.CompareTo(next.Main);
        if (compare > 0 || (compare == 0 && old.Beta > next.Beta)) return "NewerVersionAlreadyInstalled";
        return "UpgradeRequired";
    }

    public static bool Test()
    {
        if (Decide("0.1.0-beta.30","0.1.0-beta.30","same","same") != "AlreadyInstalledVerified" ||
            Decide("0.1.0-beta.9","0.1.0-beta.30","old","new") != "UpgradeRequired" ||
            Decide("0.1.0-beta.31","0.1.0-beta.30","old","new") != "NewerVersionAlreadyInstalled" ||
            Decide("0.2.0-beta.1","0.1.0-beta.30","old","new") != "NewerVersionAlreadyInstalled" ||
            Decide("0.1.0-beta.31","1.0.0","old","new") != "UpgradeRequired" ||
            Decide("1.0.0-beta.31","1.0.0","old","new") != "UpgradeRequired" ||
            Decide("1.0.0","1.0.0-beta.31","old","new") != "NewerVersionAlreadyInstalled" ||
            Decide("1.0.0","1.0.0","same","same") != "AlreadyInstalledVerified")
            throw new InvalidOperationException("VersionPolicyRejected");
        try { _ = Decide("0.1.0-beta.30","0.1.0-beta.30","old","new"); }
        catch (InvalidDataException e) when(e.Message == "SameVersionPayloadMismatch") { return true; }
        throw new InvalidOperationException("SameVersionTamperAccepted");
    }
}

// Read numeric SCM state. An error is never interpreted as stopped/deleted.
internal static class NativeServiceState
{
    public const uint Missing=0, Stopped=1, StartPending=2, StopPending=3, Running=4, Deleting=8;
    public static bool IsStopped(uint state) => state is Missing or Stopped;
    public static uint Read(string name)
    {
        var manager = OpenSCManager(null,null,1);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager,name,4);
            if (service == IntPtr.Zero)
            {
                var code=Marshal.GetLastWin32Error();
                if(code==1060) return Missing;
                if(code==1072) return Deleting;
                throw new Win32Exception(code);
            }
            try
            {
                if(!QueryServiceStatus(service,out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if(status.State is < 1 or > 7) throw new InvalidDataException("ServiceStateRejected");
                return status.State;
            }
            finally { _=CloseServiceHandle(service); }
        }
        finally { _=CloseServiceHandle(manager); }
    }
    public static bool Test() => IsStopped(Stopped) && IsStopped(Missing) && !IsStopped(StopPending) &&
        !IsStopped(StartPending) && !IsStopped(Running) && !IsStopped(Deleting) && !IsStopped(999);
    [StructLayout(LayoutKind.Sequential)] private struct Status
    { public uint Type,State,Accepted,ExitCode,SpecificExitCode,Checkpoint,WaitHint; }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenService(IntPtr manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service,out Status status);
    [DllImport("advapi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
