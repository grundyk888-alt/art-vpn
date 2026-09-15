using System.Net;

namespace ArtSport.ArtVpn.Service;

// Separate browser evidence: never a core outage predicate, never a direct bypass.
internal static class GoogleReachability
{
    internal static async Task<string> ProbeAsync(IWebProxy proxy, CancellationToken token)
    {
        using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var response = await client.GetAsync("https://www.google.com/",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            // Do not retain search bodies, cookies or query strings in diagnostics.
            return Classify((int)response.StatusCode);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return "Timeout"; }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        { return "TlsFailure"; }
        catch (HttpRequestException) { return "Unavailable"; }
    }

    internal static string Classify(int httpStatus) => httpStatus switch
    {
        200 => "Available",
        301 or 302 or 303 or 307 or 308 or 429 => "VerificationRequired",
        _ => "Unavailable"
    };

    internal static int Rank(double availability) => availability >= 83 ? 0 : availability < 0 ? 1 : 2;
    internal static string Describe(string status) => status switch
    {
        "Available" => "Google доступен через VPN",
        "VerificationRequired" => "Google отвечает, но требует проверки в браузере",
        "NotChecked" => "Google ещё не проверен",
        _ => "Google недоступен на этом канале; работающий канал ChatGPT сохранён"
    };
}
