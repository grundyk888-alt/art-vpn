using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ArtSport.ArtVpn.Service;

// Read-only guard, not a tunnel controller. Never disable a network adapter or
// kill a foreign VPN to make a proxy handover appear successful.
internal static class ExternalTunnelDiagnosis
{
    internal static string? Check()
    {
        try
        {
            var tunnels = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && IsKnownTunnel(n.Name, n.Description))
                .Select(n => n.GetIPProperties().GetIPv4Properties()?.Index ?? -1).Where(i => i > 0).ToHashSet();
            if (tunnels.Count == 0) return null;
            // Routing lookups only; no packets sent, no public IP collected.
            foreach (var address in new[] { "1.1.1.1", "203.0.113.1" })
            {
                var rc = GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse(address).GetAddressBytes()), out var index);
                if (rc == 0 && tunnels.Contains((int)index)) return "ExternalTunnelOwnsRoute";
            }
            return null;
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or PlatformNotSupportedException)
        { return "ExternalTunnelCheckUnavailable"; }
    }

    internal static bool IsKnownTunnel(string name, string description) =>
        (name + " " + description).Contains("happ", StringComparison.OrdinalIgnoreCase) ||
        (name + " " + description).Contains("throne", StringComparison.OrdinalIgnoreCase) ||
        (name + " " + description).Contains("sing-box", StringComparison.OrdinalIgnoreCase);

    internal static bool HappOwnsRoute()
    {
        try
        {
            var indices = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                    (n.Name + " " + n.Description).Contains("happ", StringComparison.OrdinalIgnoreCase))
                .Select(n => n.GetIPProperties().GetIPv4Properties()?.Index ?? -1).ToHashSet();
            return GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes()), out var index) == 0 &&
                indices.Contains((int)index);
        }
        catch { return false; } // Unknown ownership never authorizes disconnect.
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterface(uint destination, out uint interfaceIndex);
}
