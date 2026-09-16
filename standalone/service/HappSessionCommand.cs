using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ArtSport.ArtVpn.Service;

// Deliberately narrow adapter: tested HAPP binary, existing owner session and
// two built-in commands only. Never execute a registry-provided command, create
// a scheduled task, kill a client, disable an adapter or persist a user token.
internal static class HappSessionCommand
{
    internal const string Executable = @"C:\Program Files\FlyFrogLLC\Happ\Happ.exe";
    internal const string AcceptedSha256 = "53A7F231CC85D18E17F5EEE98FFE6C56A97EF6DC399F6C58E646228B99ECA9FB";

    internal static bool Available(string owner)
    {
        try { using var token = FindOwnerToken(owner); return true; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException) { return false; }
    }

    internal static async Task ExecuteAsync(string owner, bool connect, CancellationToken cancellationToken)
    {
        using var token = FindOwnerToken(owner);
        var environment = IntPtr.Zero;
        if (!CreateEnvironmentBlock(out environment, token.Handle, false)) throw new Win32Exception();
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default", Flags = 1, ShowWindow = 0 };
        try
        {
            var command = new StringBuilder('"' + Executable + "\" happ://" + (connect ? "connect" : "disconnect"));
            if (!CreateProcessAsUser(token.Handle, Executable, command, IntPtr.Zero, IntPtr.Zero, false,
                0x400, environment, Path.GetDirectoryName(Executable), ref startup, out var process)) throw new Win32Exception();
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                while (WaitForSingleObject(process.Process, 0) == 258)
                    await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                if (!GetExitCodeProcess(process.Process, out var exitCode) || exitCode != 0)
                    throw new InvalidOperationException("HappCommandRejected");
            }
            finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
        }
        finally { if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment); }
    }

    private static OwnedToken FindOwnerToken(string owner)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Executable)) throw new InvalidOperationException("HappControlUnavailable");
        for (var path = Path.GetDirectoryName(Executable); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("HappPathRejected");
        if ((File.GetAttributes(Executable) & FileAttributes.ReparsePoint) != 0 ||
            !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Executable))).Equals(AcceptedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("HappVersionNotAccepted");
        var matches = new List<OwnedToken>();
        try
        {
            foreach (var process in Process.GetProcessesByName("Happ"))
                using (process)
                {
                    try
                    {
                        if (process.SessionId == 0 || process.MainModule?.FileName is not { } path ||
                            !path.Equals(Executable, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!OpenProcessToken(process.Handle, 0xB, out var original)) continue;
                        try
                        {
                            using var identity = new WindowsIdentity(original);
                            if (identity.User?.Value != owner) continue;
                            if (!DuplicateTokenEx(original, 0x02000000, IntPtr.Zero, 2, 1, out var copy)) throw new Win32Exception();
                            matches.Add(new(copy));
                        }
                        finally { CloseHandle(original); }
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
            if (matches.Count != 1) throw new InvalidOperationException("HappOwnerSessionUnavailable");
            var result = matches[0]; matches.Clear(); return result;
        }
        finally { foreach (var match in matches) match.Dispose(); }
    }

    private sealed class OwnedToken(IntPtr handle) : IDisposable
    { internal IntPtr Handle { get; } = handle; public void Dispose() => CloseHandle(Handle); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, Reserved2; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr copy);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
}
