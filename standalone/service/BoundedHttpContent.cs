namespace ArtSport.ArtVpn.Service;

internal static class BoundedHttpContent
{
    internal static async Task<byte[]> ReadAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength is { } size && size > maximum)
            throw new InvalidDataException("HttpBodyLimitExceeded");
        await using var source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[Math.Min(maximum + 1, 8192)];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) return result.ToArray();
                if (result.Length + read > maximum) throw new InvalidDataException("HttpBodyLimitExceeded");
                result.Write(buffer, 0, read);
            }
        }
        finally { Array.Clear(buffer); }
    }
}
