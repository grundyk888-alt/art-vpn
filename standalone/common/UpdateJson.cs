using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArtSport.ArtVpn.Common;

// Shared by the service and unelevated downloader: identical signed bytes.
internal static class UpdateJson
{
    internal static readonly JsonSerializerOptions Output = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    internal static readonly JsonDocumentOptions Document = new() { MaxDepth = 16 };
}
