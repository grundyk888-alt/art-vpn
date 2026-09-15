using Microsoft.Win32;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Ui;

// Health of our engine is not proof that Windows is using it. This view is
// strictly read-only and never changes the route to make the UI look healthy.
internal sealed record WindowsRouteStatus(bool? UsesArtVpn, string Message, string Endpoint = "Unknown")
{
    internal static WindowsRouteStatus ReadCurrentUser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return new(null, "Не удалось прочитать подключение Windows. Настройки не изменены.");
            return Describe(Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0),
                Convert.ToString(key.GetValue("ProxyServer")) ?? "",
                Convert.ToString(key.GetValue("AutoConfigURL")) ?? "");
        }
        catch { return new(null, "Не удалось проверить подключение Windows. Настройки не изменены."); }
    }

    internal static WindowsRouteStatus Describe(int enabled, string server, string pac)
    {
        if (!string.IsNullOrWhiteSpace(pac))
            return new(null, "Windows использует сценарий прокси. Маршрут определяется им; автоматическое подключение не меняем.");
        if (enabled != 1)
            return new(false, "Системный прокси Windows выключен. Для подключения нажмите «Авто».", "Off");
        if (server.Trim().Equals("127.0.0.1:22080", StringComparison.OrdinalIgnoreCase))
            return new(true, "Windows использует ART VPN · 127.0.0.1:22080. Приложения со своим прокси могут идти другим маршрутом.", "ArtVpn");
        if (ExternalProxyEndpoint.ForPort(ExternalProxyEndpoint.KnownProxyPort(server.Trim())) is { } external)
            return new(false, $"Windows использует {external.DisplayName} · 127.0.0.1:{external.Port}. Автовозврат ART VPN не включается без вашего выбора «Авто».", external.Mode);
        // Never render arbitrary proxy values: they may contain credentials.
        return new(false, "Windows использует другой прокси. ART VPN его не меняет; подключение переключается вашей кнопкой.", "External");
    }

    internal VpnViewState Apply(VpnViewState state) => state with
    {
        WindowsRouteStatus = Message,
        WindowsUsesArtVpn = UsesArtVpn,
        // Neither a button click nor a cached backend alone confirms a mode.
        // Match the controller's mode to the independently observed OS route.
        ConfirmedRouteMode = state.ServiceAvailable ? (state.Mode, Endpoint) switch
        {
            ("Auto" or "Авто", "ArtVpn") => "Auto",
            ("ArtVpn" or "ART VPN", "ArtVpn") => "ArtVpn",
            ("Throne", "Throne") => "Throne",
            ("Happ", "Happ") => "Happ",
            _ => ""
        } : "",
        Headline = state.Health == VpnHealth.Healthy && UsesArtVpn == false
            ? "ART VPN готов, но Windows пока не подключена"
            : state.Headline
    };

    internal static void VerifyContract()
    {
        if (Describe(1, "127.0.0.1:22080", "").UsesArtVpn != true ||
            Describe(0, "127.0.0.1:22080", "").UsesArtVpn != false ||
            Describe(1, "127.0.0.1:2080", "").UsesArtVpn != false ||
            Describe(1, "127.0.0.1:22080", "https://example.invalid/private-pac").UsesArtVpn is not null ||
            Describe(1, "https://username:password@other.invalid", "").Message.Contains("password"))
            throw new InvalidOperationException("WindowsRouteViewInvariantFailed");
    }
}
