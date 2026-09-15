namespace ArtSport.ArtVpn.Common;

// Shared by the service and its UI. A client name is not a health result:
// every selection still requires a fresh end-to-end proxy probe. Deliberately
// accept only documented loopback HTTP ports, never arbitrary remote proxies.
internal sealed record ExternalProxyEndpoint(string Mode, string DisplayName, int Port)
{
    internal static ExternalProxyEndpoint Throne { get; } = new("Throne", "Throne", 2080);
    internal static ExternalProxyEndpoint Happ { get; } = new("Happ", "HAPP", 10809);

    internal static ExternalProxyEndpoint? ForMode(string? mode) => mode switch
    {
        "Throne" => Throne,
        "Happ" => Happ,
        _ => null
    };

    internal static ExternalProxyEndpoint? ForPort(int port) => port switch
    {
        2080 => Throne,
        10809 => Happ,
        _ => null
    };

    internal static int KnownProxyPort(string? server) => server switch
    {
        "127.0.0.1:2080" => 2080,
        "127.0.0.1:10809" => 10809,
        "127.0.0.1:22080" => 22080,
        _ => 0
    };
}
