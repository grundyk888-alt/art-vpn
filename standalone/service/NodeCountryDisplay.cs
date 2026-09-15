namespace ArtSport.ArtVpn.Service;

// Presentation only. Never replace provider identity, qualification evidence or ranking.
// A measured exit country belongs to this exact qualified node, not the whole provider.
internal static class NodeCountryDisplay
{
    internal static string Resolve(NodeProbeEvidence node)
    {
        if (node.Clean && !node.TlsIntegrityFailure && node.EgressStable && node.FailureClass == "Passed" &&
            ProviderAdapter.CountryNameFromCode(node.ObservedCountry) is { } observed)
            return observed;
        return Label(node.Country);
    }

    internal static string Label(string? country) =>
        string.IsNullOrWhiteSpace(country) || country is "Другая" or "Неизвестная" or "Unknown" or "—"
            ? "Страна не определена"
            : country;
}
