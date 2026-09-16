using System.Text.Json;

namespace ArtSport.ArtVpn.Setup;

// Diagnostic evidence only: never pins, changes or disables Auto selection.
// Capture at service replacement, not before a potentially long download.
internal static class UpdateSelectionReceipt
{
    internal static void Capture(string dataRoot, string from, string to)
    {
        using var active = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "state", "active-generation.v1.json")));
        using var selector = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "state", "selector-live.v1.json")));
        var generation = active.RootElement.GetProperty("generationId").GetString();
        var live = selector.RootElement;
        var matches = live.GetProperty("generationId").GetString() == generation;
        AtomicJson.Replace(Path.Combine(dataRoot, "state", "update-selection.v1.json"), new
        {
            schema = 1, kind = "art-vpn-update-selection", fromVersion = from, toVersion = to,
            capturedAtUtc = DateTimeOffset.UtcNow.ToString("o"), generationId = generation,
            nodeTag = matches ? live.GetProperty("currentTag").GetString() : null,
            country = matches ? live.GetProperty("currentCountry").GetString() : null,
            observationMatchesGeneration = matches, containsProviderSecret = false
        });
    }

    internal static void TryCapture(string dataRoot, string from, string to)
    {
        // A fresh install without a subscription or a manually selected external
        // client may have no ART selector. Missing telemetry must not mutate it.
        try { Capture(dataRoot, from, to); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { }
    }
}
