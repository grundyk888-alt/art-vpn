using System.IO;
using System.Text.Json;

namespace ArtSport.Vpn.Shared;

/// <summary>
/// One reply contract for both consoles. An accepted flag alone does not prove
/// that this service acknowledged this command. Validation never changes state.
/// </summary>
internal static class ControlReplyPolicy
{
    internal sealed record Reply(bool Accepted, bool Queued, string DetailCode);

    internal static Reply Parse(JsonElement root, string requestId, string action)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
            !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) || version != 1 ||
            Text(root, "kind") != "art-vpn-control-response" ||
            Text(root, "requestId") != requestId || Text(root, "action") != action ||
            !root.TryGetProperty("accepted", out var accepted) || accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("queued", out var queued) || queued.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            Text(root, "detailCode") is not { } code)
            throw new InvalidDataException("ControlReplyRejected");

        var ok = accepted.GetBoolean();
        if (Text(root, "status") != (ok ? "Accepted" : "Rejected") || (!ok && queued.GetBoolean()))
            throw new InvalidDataException("ControlReplyRejected");
        return new Reply(ok, queued.GetBoolean(), code);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Pure contract regression: no pipe, network, registry or external process.
    internal static int VerifyContract()
    {
        const string valid = """{"schema":1,"kind":"art-vpn-control-response","status":"Accepted","requestId":"qa-id","action":"Ping","accepted":true,"queued":false,"detailCode":"ServiceReady"}""";
        var cases = new (string Json, bool Valid)[]
        {
            (valid, true),
            (valid.Replace("\"queued\":false", "\"queued\":true"), true),
            (valid.Replace("\"Accepted\"", "\"Rejected\"").Replace("\"accepted\":true", "\"accepted\":false"), true),
            ("[]", false), ("null", false),
            (valid.Replace("\"schema\":1", "\"schema\":2"), false),
            (valid.Replace("art-vpn-control-response", "other"), false),
            (valid.Replace("qa-id", "old-id"), false),
            (valid.Replace("Ping", "SetMode"), false),
            (valid.Replace("\"accepted\":true", "\"accepted\":\"true\""), false),
            (valid.Replace("\"queued\":false", "\"queued\":1"), false),
            (valid.Replace("\"Accepted\"", "\"Completed\""), false),
            (valid.Replace("\"detailCode\":\"ServiceReady\"", "\"detailCode\":null"), false),
            (valid.Replace("\"schema\":1", "\"schema\":1,\"schema\":1"), false)
        };
        foreach (var item in cases)
        {
            using var document = JsonDocument.Parse(item.Json);
            var passed = true;
            try { Parse(document.RootElement, "qa-id", "Ping"); }
            catch (InvalidDataException) { passed = false; }
            if (passed != item.Valid) throw new InvalidOperationException("ControlReplyContractRegression");
        }
        return cases.Length;
    }
}
