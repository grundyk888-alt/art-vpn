using System.Text;
using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Ui;

internal static class RouteHealthPresentation
{
    private const string StateRoot = @"C:\ProgramData\ART VPN\state";

    internal static VpnViewState Apply(VpnViewState state, WindowsRouteStatus actual,
        Func<string, string>? read = null, DateTimeOffset? utcNow = null)
    {
        // Never reuse an old green channel label when its proof is unavailable.
        state = state with { ActiveChannel = "Не подтверждён", ActiveChannelVerified = false };
        var expectedMode = state.ConfirmedRouteMode;
        // Auto may legitimately fall back to a direct Throne entry. A cached
        // mode alone is insufficient: the committed receipt below must match it.
        if (expectedMode.Length == 0 && ExternalProxyEndpoint.ForMode(actual.Endpoint) is not null && state.Mode is "Auto" or "Авто")
            expectedMode = "Auto";
        if (!state.ServiceAvailable || state.Health is not (VpnHealth.Healthy or VpnHealth.Fallback or VpnHealth.Checking or VpnHealth.Unavailable) ||
            actual.Endpoint is not ("ArtVpn" or "Throne" or "Happ") || expectedMode.Length == 0) return state;
        read ??= ReadBounded;
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var port = actual.Endpoint == "ArtVpn" ? 22080 : ExternalProxyEndpoint.ForMode(actual.Endpoint)!.Port;
        var endpoint = $"127.0.0.1:{port}";
        try
        {
            var modeText = read("route-mode.v1.json");
            using var mode = Parse(modeText);
            Require(mode.RootElement.GetProperty("kind").GetString() == "art-vpn-route-mode");
            Require(mode.RootElement.GetProperty("phase").GetString() == "Committed" &&
                mode.RootElement.GetProperty("mode").GetString() == expectedMode);
            var expected = mode.RootElement.GetProperty("expected");
            Require(expected.GetProperty("proxyEnable").GetInt32() == 1 &&
                expected.GetProperty("proxyServer").GetString() == endpoint);
            var earliest = Date(mode.RootElement, "atUtc");
            string? frontText = null;
            long frontRevision = 0;
            var channel = ExternalProxyEndpoint.ForPort(port)?.DisplayName ?? "ART VPN";
            if (port == 22080)
            {
                frontText = read("front-target.v1.json");
                using var front = Parse(frontText);
                Require(front.RootElement.GetProperty("kind").GetString() == "art-vpn-front-target" &&
                    front.RootElement.GetProperty("revision").GetInt64() > 0 &&
                    front.RootElement.GetProperty("targetPort").GetInt32() is 2080 or 10809 or 22086 or 22087 or 22088);
                var switched = Date(front.RootElement, "switchedAtUtc");
                frontRevision = front.RootElement.GetProperty("revision").GetInt64();
                channel = ExternalProxyEndpoint.ForPort(front.RootElement.GetProperty("targetPort").GetInt32())?.DisplayName ?? "ART VPN";
                if (switched > earliest) earliest = switched;
            }
            using var health = Parse(read($"route-health-{port}.v1.json"));
            var root = health.RootElement;
            Require(root.GetProperty("kind").GetString() == "art-vpn-route-health" &&
                root.GetProperty("proxy").GetString() == endpoint);
            var observed = Date(root, "atUtc");
            Require(observed >= earliest && observed <= now.AddSeconds(5) && observed >= now.AddSeconds(-90));
            if (port == 22080 && root.TryGetProperty("frontRevision", out var probeRevision))
                Require(probeRevision.GetInt64() == frontRevision);
            // A slot/route may change while files are read. Never paint the old
            // probe green for a newer route; wait for its own observation.
            Require(read("route-mode.v1.json") == modeText &&
                (frontText is null || read("front-target.v1.json") == frontText));
            var usesExternal = channel != "ART VPN";
            if (root.GetProperty("ready").GetBoolean()) return state with
            {
                ConfirmedRouteMode = expectedMode,
                ActiveChannel = channel,
                ActiveChannelVerified = true,
                Health = usesExternal ? VpnHealth.Fallback : state.Health == VpnHealth.Unavailable ? VpnHealth.Healthy : state.Health,
                Headline = usesExternal ? $"Работает внешний VPN — {channel}" : state.Health == VpnHealth.Unavailable ? "Выбранный канал снова доступен" : state.Headline,
                Explanation = usesExternal ? (expectedMode == "Auto" ? "Режим «Авто»: сейчас используется проверенный внешний резерв." : "Внешний канал проверен. Ваш ручной выбор сохранён; автоматический возврат выключен.")
                    : state.Health == VpnHealth.Unavailable ? "Свежая проверка пройдена. Настройки подключения не менялись." : state.Explanation,
                Quality = usesExternal ? "Внешний канал проверен" : state.Quality,
                Country = usesExternal ? $"Страна в {channel}" : state.Country,
                SelectedAt = usesExternal ? null : state.SelectedAt,
                Latency = usesExternal ? "—" : state.Latency,
                Stability = usesExternal ? "—" : state.Stability,
                Nodes = usesExternal ? state.Nodes.Select((node, index) => node with
                { Role = "ПУЛ ART VPN · " + (index + 1), Quality = "сейчас не используется" }).ToArray() : state.Nodes
            };
            ConnectionProblemMessage? problem = null;
            try
            {
                using var diagnosis = Parse(read("connection-diagnosis.v1.json"));
                var detail = diagnosis.RootElement;
                var at = Date(detail, "atUtc");
                Require(detail.GetProperty("kind").GetString() == "art-vpn-connection-diagnosis" &&
                    detail.GetProperty("proxy").GetString() == endpoint && at >= earliest &&
                    at >= now.AddSeconds(-90) && at <= observed &&
                    (port != 22080 || detail.GetProperty("frontRevision").GetInt64() == frontRevision));
                problem = ConnectionProblemText.Describe(detail.GetProperty("code").GetString() ?? "");
            }
            catch { } // Missing/old detail must not conceal the current route failure.
            Require(read("route-mode.v1.json") == modeText &&
                (frontText is null || read("front-target.v1.json") == frontText));
            return state with
            {
                Health = VpnHealth.Unavailable,
                Headline = problem?.Headline ?? "Выбранный канал не прошёл проверку связи",
                Explanation = problem?.Explanation ?? "Проверка заметила проблему. Режим не изменён; причина ещё уточняется.",
                Quality = "Нужна проверка"
            };
        }
        catch
        {
            return state with
            {
                Health = VpnHealth.Checking,
                Headline = "Проверяем выбранный канал",
                Explanation = "Свежего подтверждения для этого подключения пока нет. Можно нажать «Проверить сейчас».",
                Quality = "Проверяем"
            };
        }
    }

    private static JsonDocument Parse(string text)
    {
        Require(text.Length <= 32_768);
        var document = JsonDocument.Parse(text);
        try { Require(document.RootElement.GetProperty("schema").GetInt32() == 1); return document; }
        catch { document.Dispose(); throw; }
    }
    private static DateTimeOffset Date(JsonElement value, string name) => DateTimeOffset.Parse(value.GetProperty(name).GetString()!,
        System.Globalization.CultureInfo.InvariantCulture);
    private static void Require(bool condition)
    { if (!condition) throw new InvalidDataException("RouteProofNotCurrent"); }

    private static string ReadBounded(string name)
    {
        using var stream = new FileStream(Path.Combine(StateRoot, name), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 32_768) throw new InvalidDataException("RouteProofTooLarge");
        var bytes = new byte[32_769];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        Require(count <= 32_768);
        return new UTF8Encoding(false, true).GetString(bytes, 0, count);
    }
}
