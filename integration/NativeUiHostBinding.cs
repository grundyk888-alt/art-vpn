using System.Text.Json;
using System.IO;

namespace ArtSport.Vpn.Shared;

// A native service being present is not permission for Monitor to adopt it.
// The installer emits this explicit, non-secret binding for an integrated install.
// A corrupt binding fails closed instead of silently starting the legacy engine.
internal static class NativeUiHostBinding
{
    internal const string FileName = "ui-host.v1.json";
    internal static string Create(string installationJson)
    {
        using var install = JsonDocument.Parse(installationJson);
        var value = install.RootElement;
        return JsonSerializer.Serialize(new { schema = 1, kind = "art-vpn-ui-host", host = "ArtMonitor",
            computerName = String(value, "computerName"), ownerSid = String(value, "ownerSid"),
            installId = String(value, "installId"), containsProviderSecret = false });
    }

    internal static bool IsIntegrated(string? bindingJson, string installationJson, string computer, string owner)
    {
        if (bindingJson is null) return false;
        if (bindingJson.Length > 4096 || installationJson.Length > 16384)
            throw new InvalidDataException("NativeUiHostBindingTooLarge");
        try
        {
            using var binding = JsonDocument.Parse(bindingJson);
            using var installation = JsonDocument.Parse(installationJson);
            var b = binding.RootElement; var i = installation.RootElement;
            var fields = new HashSet<string>(StringComparer.Ordinal)
                { "schema", "kind", "host", "computerName", "ownerSid", "installId", "containsProviderSecret" };
            if (b.ValueKind != JsonValueKind.Object || b.EnumerateObject().Any(p => !fields.Remove(p.Name)) || fields.Count != 0 ||
                b.GetProperty("schema").GetInt32() != 1 || String(b,"kind") != "art-vpn-ui-host" ||
                String(b,"host") != "ArtMonitor" || b.GetProperty("containsProviderSecret").ValueKind != JsonValueKind.False ||
                String(b,"computerName") != computer || String(b,"ownerSid") != owner ||
                !Guid.TryParseExact(String(b,"installId"), "N", out _) ||
                String(i,"computerName") != computer || String(i,"ownerSid") != owner ||
                String(i,"kind") != "art-vpn-consumer-install" || String(i,"status") != "Configured" ||
                String(i,"installId") != String(b,"installId"))
                throw new InvalidDataException("NativeUiHostBindingRejected");
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("NativeUiHostBindingRejected", error); }
    }

    private static string String(JsonElement e, string key) => e.GetProperty(key).GetString() ?? "";

    internal static object Test()
    {
        const string install = "{\"kind\":\"art-vpn-consumer-install\",\"status\":\"Configured\",\"computerName\":\"LAB\",\"ownerSid\":\"owner\",\"installId\":\"00000000000000000000000000000001\"}";
        var binding = Create(install); int checks = 0;
        void Assert(bool condition) { if (!condition) throw new InvalidOperationException("NativeUiHostBindingRegression"); checks++; }
        Assert(!IsIntegrated(null, install, "LAB", "owner"));
        Assert(IsIntegrated(binding, install, "LAB", "owner"));
        foreach (var rejected in new[] { "{}", "bad json", binding.Replace("ArtMonitor","Other"),
                     binding.Replace("\"LAB\"", "\"OTHER\""), binding.Replace("\"owner\"","\"other-owner\""),
                     binding.Replace("00000000000000000000000000000001","00000000000000000000000000000002"),
                     binding.Replace("false","true"), binding.Replace("\"schema\":1", "\"schema\":2"),
                     binding.Replace("\"schema\":1", "\"schema\":1,\"schema\":1") })
        {
            bool denied = false;
            try { IsIntegrated(rejected, install, "LAB", "owner"); }
            catch (InvalidDataException) { denied = true; }
            Assert(denied);
        }
        return new { Status = "Passed", Checks = checks, SystemChanged = false };
    }
}
