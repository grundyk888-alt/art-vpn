using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ArtSport.ArtVpn.Setup;

internal static class OwnedListeners
{
    internal static bool Match(string executable, params int[] required)
    {
        var owners = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        owners.Add(process.Id);
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        }
        if (owners.Count != 1) return false;
        return MatchPid(owners.Single(), required);
    }

    private static bool MatchPid(int owner, params int[] required)
    {
        var size = 0;
        var code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (code != 122 || size is < 4 or > 16_777_216) return false;
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(memory, ref size, false, 2, 3, 0) != 0) return false;
            var count = Marshal.ReadInt32(memory);
            if (count < 0 || count > (size - 4) / 24) return false;
            var ports = new HashSet<int>();
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(memory, 4 + index * 24);
                var address = unchecked((uint)Marshal.ReadInt32(row, 4));
                var raw = unchecked((uint)Marshal.ReadInt32(row, 8));
                var port = (int)(((raw & 255) << 8) | ((raw >> 8) & 255));
                if (Marshal.ReadInt32(row) == 2 && address == 0x0100007f && Marshal.ReadInt32(row, 20) == owner)
                    ports.Add(port);
            }
            return required.All(ports.Contains);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal static bool Test()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        if (!MatchPid(Environment.ProcessId, port) || MatchPid(int.MaxValue, port))
            throw new InvalidOperationException("ListenerOwnershipContractFailed");
        return true;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
