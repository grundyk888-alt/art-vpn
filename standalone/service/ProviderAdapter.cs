using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Service;

internal sealed record ProviderNode(
    string Tag,
    string Country,
    string Transport,
    string ServerKey,
    int DestinationPort,
    int ProxyPort,
    string ProbeIdentity,
    JsonObject PrivateOutbound);

internal sealed record ProviderNodeView(
    string Tag,
    string Name,
    string Country,
    string Transport,
    string ServerKey,
    int DestinationPort,
    int ProxyPort,
    string ProbeIdentity);

internal sealed record ProviderParseResult(
    int SourceLines,
    string Adapter,
    ProviderNode[] Nodes,
    JsonObject PrivateProbeConfig)
{
    public ProviderNodeView[] SanitizedManifest => Nodes.Select(node => new ProviderNodeView(
        node.Tag,
        $"{node.Country} {node.Tag}",
        node.Country,
        node.Transport,
        node.ServerKey,
        node.DestinationPort,
        node.ProxyPort,
        node.ProbeIdentity)).ToArray();
}

/// <summary>
/// Provider-neutral dispatch with the first production candidate adapter:
/// strict VLESS Reality share links used by the proven Quattro pilot. Raw
/// endpoints and credentials exist only in the private probe configuration.
/// </summary>
internal static partial class ProviderAdapter
{
    public const int MaxPayloadBytes = 4 * 1024 * 1024;
    public const int MaxDecodedBytes = 8 * 1024 * 1024;
    public const int MaxNodes = 1000;

    private static readonly string[] AcceptedTransports = ["tcp", "raw", "grpc", "ws", "httpupgrade"];

    private static readonly (string Russian, string English, string Code)[] Countries =
    [
        ("Нидерланды", "Netherlands", "NL"), ("Эстония", "Estonia", "EE"),
        ("Германия", "Germany", "DE"), ("Швеция", "Sweden", "SE"),
        ("Латвия", "Latvia", "LV"), ("Норвегия", "Norway", "NO"),
        ("Финляндия", "Finland", "FI"), ("Франция", "France", "FR"),
        ("Польша", "Poland", "PL"), ("Швейцария", "Switzerland", "CH"),
        ("Япония", "Japan", "JP"), ("Канада", "Canada", "CA"),
        ("США", "USA", "US"), ("Россия", "Russia", "RU"),
        ("Великобритания", "United Kingdom", "GB"), ("Кения", "Kenya", "KE"),
        ("Нигерия", "Nigeria", "NG"), ("Грузия", "Georgia", "GE"),
        ("Турция", "Turkey", "TR"), ("Израиль", "Israel", "IL"),
        ("Сербия", "Serbia", "RS"), ("Австрия", "Austria", "AT"),
        ("Бельгия", "Belgium", "BE"), ("Дания", "Denmark", "DK"),
        ("Ирландия", "Ireland", "IE"), ("Исландия", "Iceland", "IS"),
        ("Испания", "Spain", "ES"), ("Италия", "Italy", "IT"),
        ("Португалия", "Portugal", "PT"), ("Чехия", "Czechia", "CZ"),
        ("Словакия", "Slovakia", "SK"), ("Венгрия", "Hungary", "HU"),
        ("Румыния", "Romania", "RO"), ("Болгария", "Bulgaria", "BG"),
        ("Хорватия", "Croatia", "HR"), ("Словения", "Slovenia", "SI"),
        ("Литва", "Lithuania", "LT"), ("Люксембург", "Luxembourg", "LU"),
        ("Греция", "Greece", "GR"), ("Кипр", "Cyprus", "CY"),
        ("ОАЭ", "UAE", "AE"), ("Сингапур", "Singapore", "SG"),
        ("Южная Корея", "South Korea", "KR"), ("Австралия", "Australia", "AU"),
        ("Бразилия", "Brazil", "BR"), ("Аргентина", "Argentina", "AR"),
        ("Мексика", "Mexico", "MX")
    ];

    [GeneratedRegex("^[A-Za-z0-9_-]{20,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex RealityKeyPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{0,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintPattern();

    public static ProviderParseResult Parse(
        ReadOnlySpan<byte> payload,
        int firstPort = 24000,
        int lastPort = 24999,
        int minimumNodes = 1)
    {
        if (payload.Length is < 1 or > MaxPayloadBytes)
            throw new InvalidDataException("ProviderPayloadSizeRejected");
        if (firstPort is < 1024 or > 65535 || lastPort < firstPort || lastPort > 65535 ||
            minimumNodes is < 1 or > MaxNodes)
            throw new InvalidDataException("ProviderBoundsRejected");

        var text = Decode(payload);
        try
        {
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length is < 1 or > MaxNodes * 4)
                throw new InvalidDataException("ProviderLineCountRejected");

            var unique = new HashSet<string>(StringComparer.Ordinal);
            var nodes = new List<ProviderNode>(Math.Min(lines.Length, MaxNodes));
            foreach (var line in lines)
            {
                if (!line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) continue;
                var parsed = ParseVlessReality(line, firstPort + nodes.Count);
                if (parsed is null || !unique.Add(parsed.Value.CredentialDigest)) continue;
                nodes.Add(parsed.Value.Node);
                if (nodes.Count > MaxNodes || firstPort + nodes.Count - 1 > lastPort)
                    throw new InvalidDataException("ProviderNodeRangeRejected");
            }

            if (nodes.Count < minimumNodes)
                throw new InvalidDataException("ProviderSupportedNodesInsufficient");

            return new ProviderParseResult(
                lines.Length,
                "vless-reality-v1",
                nodes.ToArray(),
                BuildProbeConfig(nodes));
        }
        finally
        {
            text = string.Empty;
        }
    }

    private static string Decode(ReadOnlySpan<byte> payload)
    {
        var strictUtf8 = new UTF8Encoding(false, true);
        var raw = payload.ToArray();
        try
        {
            string direct;
            try { direct = strictUtf8.GetString(raw).TrimStart('\uFEFF').Trim(); }
            catch (DecoderFallbackException) { direct = string.Empty; }
            if (direct.Contains("://", StringComparison.Ordinal)) return direct;

            var compact = direct.Length == 0
                ? string.Empty
                : string.Concat(direct.Where(character => !char.IsWhiteSpace(character)));
            if (compact.Length == 0 || compact.Length > MaxPayloadBytes * 2 ||
                compact.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '=' or '-' or '_')))
                throw new InvalidDataException("ProviderEncodingRejected");

            compact = compact.Replace('-', '+').Replace('_', '/');
            var padding = compact.Length % 4;
            if (padding == 1) throw new InvalidDataException("ProviderEncodingRejected");
            if (padding > 0) compact += new string('=', 4 - padding);
            byte[] decoded;
            try { decoded = Convert.FromBase64String(compact); }
            catch (FormatException) { throw new InvalidDataException("ProviderEncodingRejected"); }
            try
            {
                if (decoded.Length is < 1 or > MaxDecodedBytes)
                    throw new InvalidDataException("ProviderDecodedSizeRejected");
                return strictUtf8.GetString(decoded).TrimStart('\uFEFF').Trim();
            }
            catch (DecoderFallbackException) { throw new InvalidDataException("ProviderEncodingRejected"); }
            finally { CryptographicOperations.ZeroMemory(decoded); }
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    private static (ProviderNode Node, string CredentialDigest)? ParseVlessReality(string value, int proxyPort)
    {
        if (value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "vless", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.DnsSafeHost) || uri.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(uri.UserInfo))
            return null;

        var user = Uri.UnescapeDataString(uri.UserInfo);
        if (!Guid.TryParse(user, out var userId)) return null;
        user = userId.ToString("D");
        Dictionary<string, string> query;
        try { query = ParseQuery(uri.Query); }
        catch (InvalidDataException) { return null; }
        if (!query.TryGetValue("security", out var security) ||
            !string.Equals(security, "reality", StringComparison.OrdinalIgnoreCase)) return null;
        var transport = query.TryGetValue("type", out var type) ? type.ToLowerInvariant() : "tcp";
        if (!AcceptedTransports.Contains(transport, StringComparer.Ordinal)) return null;
        if (query.TryGetValue("allowInsecure", out var allowInsecure) && IsTrue(allowInsecure)) return null;
        if (!query.TryGetValue("sni", out var sni) || !IsSafeHost(sni) ||
            !query.TryGetValue("pbk", out var publicKey) || !RealityKeyPattern().IsMatch(publicKey)) return null;
        var shortId = query.GetValueOrDefault("sid", string.Empty);
        if (!ShortIdPattern().IsMatch(shortId)) return null;
        var fingerprint = query.GetValueOrDefault("fp", "chrome");
        if (!FingerprintPattern().IsMatch(fingerprint)) return null;
        var flow = query.GetValueOrDefault("flow", string.Empty);
        if (flow.Length > 64 || (flow.Length > 0 && !string.Equals(flow, "xtls-rprx-vision", StringComparison.Ordinal)))
            return null;

        // Provider labels and query ordering are presentation details.  They
        // must not erase accumulated node history or make a healthy endpoint
        // look new after every subscription refresh.
        var normalizedQuery = string.Join('&', query
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .Select(item => Uri.EscapeDataString(item.Key.ToLowerInvariant()) + "=" + Uri.EscapeDataString(item.Value)));
        var endpointIdentity = $"vless://{user}@{uri.DnsSafeHost.ToLowerInvariant()}:{uri.Port}?{normalizedQuery}";
        var credentialDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpointIdentity))).ToLowerInvariant();
        var tag = "n-" + credentialDigest[..16];
        var label = uri.Fragment.Length > 1 ? Uri.UnescapeDataString(uri.Fragment[1..]) : tag;
        var country = CountryFromLabel(label);
        var serverKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.DnsSafeHost.ToLowerInvariant())))
            .ToLowerInvariant()[..16];

        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = tag,
            ["server"] = uri.DnsSafeHost,
            ["server_port"] = uri.Port,
            ["uuid"] = user,
            ["packet_encoding"] = "xudp",
            ["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = sni,
                ["insecure"] = false,
                ["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fingerprint },
                ["reality"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = publicKey,
                    ["short_id"] = shortId
                }
            }
        };
        if (flow.Length > 0) outbound["flow"] = flow;
        if (transport == "grpc")
        {
            var serviceName = query.GetValueOrDefault("serviceName", query.GetValueOrDefault("service_name", string.Empty));
            if (serviceName.Length > 256) return null;
            outbound["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = serviceName };
        }
        else if (transport is "ws" or "httpupgrade")
        {
            var path = query.GetValueOrDefault("path", "/");
            if (path.Length is < 1 or > 2048 || !path.StartsWith('/')) return null;
            var wire = new JsonObject { ["type"] = transport, ["path"] = path };
            if (query.TryGetValue("host", out var host) && host.Length > 0)
            {
                if (!IsSafeHost(host)) return null;
                wire["headers"] = new JsonObject { ["Host"] = host };
            }
            outbound["transport"] = wire;
        }

        var probeIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{tag}|{serverKey}|{uri.Port}|{credentialDigest}|{proxyPort}"))).ToLowerInvariant()[..32];
        return (new ProviderNode(tag, country, transport, serverKey, uri.Port, proxyPort, probeIdentity, outbound),
            credentialDigest);
    }

    private static Dictionary<string, string> ParseQuery(string queryText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in queryText.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace("+", "%20", StringComparison.Ordinal));
            var value = pair.Length == 2
                ? Uri.UnescapeDataString(pair[1].Replace("+", "%20", StringComparison.Ordinal))
                : string.Empty;
            if (key.Length is < 1 or > 64 || value.Length > 4096 || !result.TryAdd(key, value))
                throw new InvalidDataException("ProviderQueryRejected");
        }
        return result;
    }

    private static bool IsTrue(string value) =>
        value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeHost(string value) =>
        value.Length is > 0 and <= 253 &&
        Uri.CheckHostName(value) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    internal static string? CountryNameFromCode(string? code)
    {
        if (code is null || code.Length != 2 || !code.All(char.IsAsciiLetter)) return null;
        foreach (var entry in Countries)
            if (string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase)) return entry.Russian;
        // Keep uncommon ISO countries readable even before a Russian translation is added.
        try
        {
            var region = new System.Globalization.RegionInfo(code.ToUpperInvariant());
            return string.Equals(region.TwoLetterISORegionName, code, StringComparison.OrdinalIgnoreCase)
                ? region.EnglishName : null;
        }
        catch (ArgumentException) { return null; }
    }

    internal static string CountryFromLabel(string label)
    {
        foreach (var entry in Countries)
            if (label.Contains(entry.Russian, StringComparison.OrdinalIgnoreCase) ||
                label.Contains(entry.English, StringComparison.OrdinalIgnoreCase) ||
                label.Contains(char.ConvertFromUtf32(0x1F1E6 + entry.Code[0] - 'A') +
                    char.ConvertFromUtf32(0x1F1E6 + entry.Code[1] - 'A'), StringComparison.Ordinal) ||
                Regex.IsMatch(label, $"(?:^|[^A-Za-z]){Regex.Escape(entry.Code)}(?:[^A-Za-z]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return entry.Russian;
        return "Другая";
    }

    private static JsonObject BuildProbeConfig(IReadOnlyList<ProviderNode> nodes)
    {
        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var rules = new JsonArray();
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var inboundTag = "probe-" + index;
            inbounds.Add(new JsonObject
            {
                ["type"] = "mixed", ["tag"] = inboundTag,
                ["listen"] = "127.0.0.1", ["listen_port"] = node.ProxyPort
            });
            outbounds.Add(node.PrivateOutbound.DeepClone());
            rules.Add(new JsonObject
            {
                ["inbound"] = new JsonArray(inboundTag), ["outbound"] = node.Tag
            });
        }
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        return new JsonObject
        {
            ["log"] = new JsonObject { ["disabled"] = true },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["rules"] = rules,
                ["final"] = "direct"
            }
        };
    }
}

internal static class ProviderAdapterTests
{
    public static ProviderAdapterTestReceipt Run()
    {
        const string uuidA = "00000000-1111-2222-3333-444444444441";
        const string uuidB = "00000000-1111-2222-3333-444444444442";
        const string uuidC = "00000000-1111-2222-3333-444444444443";
        const string key = "abcdefghijklmnopqrstuvwxyzABCDEFGH123456789";
        var lines = new[]
        {
            $"vless://{uuidA}@198.51.100.10:443?security=reality&type=tcp&sni=example.com&pbk={key}&sid=aa#Нидерланды",
            $"vless://{uuidB}@198.51.100.11:8443?security=reality&type=grpc&sni=example.org&pbk={key}&serviceName=grpc#Germany",
            $"vless://{uuidC}@198.51.100.12:9443?security=reality&type=ws&sni=example.net&pbk={key}&path=%2Fws&host=example.net#EE",
        };
        var raw = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(raw).TrimEnd('='));
        try
        {
            var result = ProviderAdapter.Parse(encoded, minimumNodes: 3);
            Assert(result.Nodes.Length == 3 && result.Nodes[0].ProxyPort == 24000 && result.Nodes[2].ProxyPort == 24002);
            Assert(result.Nodes[0].Country == "Нидерланды" && result.Nodes[1].Country == "Германия" && result.Nodes[2].Country == "Эстония");
            Assert(result.Nodes.Select(node => node.ServerKey).Distinct(StringComparer.Ordinal).Count() == 3);
            var publicJson = JsonSerializer.Serialize(result.SanitizedManifest, JsonSettings.Output);
            Assert(!lines.Any(publicJson.Contains) && !publicJson.Contains(uuidA, StringComparison.Ordinal) &&
                   !publicJson.Contains("198.51.100", StringComparison.Ordinal));
            var privateJson = result.PrivateProbeConfig.ToJsonString(JsonSettings.Output);
            Assert(privateJson.Contains(uuidA, StringComparison.Ordinal) && privateJson.Contains("198.51.100.10", StringComparison.Ordinal));
            Assert(result.Nodes.All(node => node.PrivateOutbound["packet_encoding"]?.GetValue<string>() == "xudp"));

            var insecure = Encoding.UTF8.GetBytes(lines[0].Replace("&sid=aa", "&sid=aa&allowInsecure=true", StringComparison.Ordinal));
            ExpectRejected(insecure, 1);
            var duplicate = Encoding.UTF8.GetBytes(string.Join('\n', [lines[0], lines[0]]));
            ExpectRejected(duplicate, 2);
            var unsupported = Encoding.UTF8.GetBytes(lines[0].Replace("type=tcp", "type=kcp", StringComparison.Ordinal));
            ExpectRejected(unsupported, 1);
            var ambiguous = Encoding.UTF8.GetBytes(lines[0].Replace("sni=example.com", "sni=example.com&sni=other.invalid", StringComparison.Ordinal));
            ExpectRejected(ambiguous, 1);

            var relabelled = Encoding.UTF8.GetBytes(
                $"vless://{uuidA}@198.51.100.10:443?pbk={key}&sid=aa&sni=example.com&type=tcp&security=reality#Новое имя");
            try
            {
                var sameEndpoint = ProviderAdapter.Parse(relabelled, minimumNodes: 1);
                Assert(sameEndpoint.Nodes.Single().Tag == result.Nodes[0].Tag);
            }
            finally { CryptographicOperations.ZeroMemory(relabelled); }

            Assert(ProviderAdapter.CountryFromLabel("🇮🇹 01") == "Италия");
            Assert(ProviderAdapter.CountryFromLabel("🇳🇱") == "Нидерланды");
            Assert(ProviderAdapter.CountryFromLabel("unnamed node") == "Другая");
            return new ProviderAdapterTestReceipt("Passed", 14, result.Adapter, result.Nodes.Length, true, false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static void ExpectRejected(byte[] payload, int minimum)
    {
        try
        {
            _ = ProviderAdapter.Parse(payload, minimumNodes: minimum);
            throw new InvalidOperationException("UnsafeProviderPayloadAccepted");
        }
        catch (InvalidDataException) { }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("ProviderAdapterInvariantFailed");
    }
}

internal sealed record ProviderAdapterTestReceipt(
    string Status,
    int Scenarios,
    string Adapter,
    int SupportedNodes,
    bool SanitizedManifest,
    bool SecretDisplayed);
