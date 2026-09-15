using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

internal sealed record DirectInternetEvidence(bool NetworkAvailable, bool HttpsReachable);

internal static class DirectInternetProbe
{
    // Neutral reachability witnesses, not functional website/browser acceptance.
    private static readonly Uri[] Witnesses =
        [new("https://www.microsoft.com/"), new("https://www.ozon.ru/")];

    internal static async Task<DirectInternetEvidence> RunAsync(CancellationToken token)
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) return new(false, false);
        using var handler = new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            CheckCertificateRevocationList = true, AutomaticDecompression = DecompressionMethods.None
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        return await CheckAsync(true, async (uri, ct) =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                // A certificate-validated HTTPS 403 still proves reachability,
                // not permission to shop or successful VPN access.
                return (int)response.StatusCode is >= 200 and < 500 && (int)response.StatusCode != 407;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return false; }
        }, timeout.Token).ConfigureAwait(false);
    }

    internal static async Task<DirectInternetEvidence> CheckAsync(bool network,
        Func<Uri, CancellationToken, Task<bool>> probe, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!network) return new(false, false);
        var answers = await Task.WhenAll(Witnesses.Select(uri => probe(uri, token))).ConfigureAwait(false);
        return new(true, answers.Any(answer => answer));
    }
}

internal static class ConnectionDiagnosis
{
    internal static string Decide(DirectInternetEvidence direct, string providerCode, bool allTestedFailed) =>
        providerCode switch
        {
            "ProviderSubscriptionExpired" => "SubscriptionExpired",
            "ProviderSubscriptionQuotaExceeded" => "SubscriptionQuotaExceeded",
            "ProviderSubscriptionAccessRejected" => "SubscriptionAccessRejected",
            "ProviderClockMismatch" => "ProviderClockMismatch",
            _ => !direct.NetworkAvailable ? "NetworkUnavailable"
                : !direct.HttpsReachable ? "DirectInternetUnconfirmed"
                : allTestedFailed ? "AllTestedVpnUnavailable" : "VpnUnavailableInternetWorks"
        };
}

internal static class ProviderResponseCondition
{
    // Optional Subscription-Userinfo format documented by the upstream provider:
    // https://github.com/MHSanaei/3x-ui/blob/main/docs/content/docs/en/config/subscription.mdx
    // Missing/invalid metadata and HTTP 403 alone never mean "please pay".
    internal static string Inspect(HttpResponseMessage response, DateTimeOffset now)
    {
        if (!response.Headers.TryGetValues("subscription-userinfo", out var values)) return "";
        var text = string.Join(";", values.Take(2));
        if (text.Length > 4096 || values.Skip(1).Any()) return "";
        var fields = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2) return "";
            var name = pair[0].ToLowerInvariant();
            if (name is not ("expire" or "upload" or "download" or "total")) continue;
            if (!long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < 0 || !fields.TryAdd(name, value)) return "";
        }
        if (fields.TryGetValue("expire", out var expires) && expires > 0)
        {
            DateTimeOffset expiration;
            try { expiration = DateTimeOffset.FromUnixTimeSeconds(expires); }
            catch (ArgumentOutOfRangeException) { return ""; }
            // A wrong local clock must not become a payment recommendation.
            if (response.Headers.Date is { } serverTime)
            {
                if ((serverTime - now).Duration() > TimeSpan.FromMinutes(15)) return "ProviderClockMismatch";
                if (expiration < serverTime.AddMinutes(-5)) return "ProviderSubscriptionExpired";
            }
        }
        if (fields.TryGetValue("total", out var total) && total > 0 &&
            fields.TryGetValue("upload", out var upload) && fields.TryGetValue("download", out var download) &&
            (upload >= total || download >= total - upload)) return "ProviderSubscriptionQuotaExceeded";
        return "";
    }
}

internal static class ConnectionDiagnosisTests
{
    internal static async Task<object> RunAsync()
    {
        var checks = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("DiagnosisInvariant" + checks); checks++; }
        var now = new DateTimeOffset(2026, 9, 8, 16, 0, 0, TimeSpan.Zero);
        string Condition(string? metadata, DateTimeOffset? serverTime)
        {
            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (metadata is not null) response.Headers.TryAddWithoutValidation("subscription-userinfo", metadata);
            response.Headers.Date = serverTime;
            return ProviderResponseCondition.Inspect(response, now);
        }
        var expired = (now.AddDays(-1)).ToUnixTimeSeconds();
        var valid = (now.AddDays(1)).ToUnixTimeSeconds();
        Check(Condition(null, now) == "");
        Check(Condition("expire=" + expired, now) == "ProviderSubscriptionExpired");
        Check(Condition("EXPIRE=" + expired, now) == "ProviderSubscriptionExpired");
        Check(Condition("expire=" + expired, null) == "");
        Check(Condition("expire=" + expired, now.AddHours(-2)) == "ProviderClockMismatch");
        foreach (var value in new[] { "expire=0", "expire=-1", "expire=x", "expire=9223372036854775807", "expire=" + valid,
            "expire=" + expired + ";expire=" + valid, new string('x', 4097) }) Check(Condition(value, now) == "");
        Check(Condition("upload=0;download=100;total=100;expire=0", now) == "ProviderSubscriptionQuotaExceeded");
        Check(Condition("upload=0;download=100;total=0;expire=0", now) == "");
        Check(Condition("upload=9223372036854775807;download=1;total=9223372036854775807", now) == "ProviderSubscriptionQuotaExceeded");
        Check(Condition("upload=0;download=99;total=100", now) == "");
        foreach (var status in new[] { 401, 402, 403, 410, 404, 503 })
        {
            using var response = new HttpResponseMessage((HttpStatusCode)status);
            var expected = status == 404 ? "ProviderSubscriptionLinkRejected" : status == 503 ? "ProviderHttpRejected" : "ProviderSubscriptionAccessRejected";
            try { SubscriptionFetcher.RequireUsableResponse(response, now); throw new InvalidOperationException("HttpFailureAccepted"); }
            catch (IOException ex) { Check(ex.Message == expected); }
        }
        using (var response = new HttpResponseMessage(HttpStatusCode.Forbidden))
        {
            response.Headers.Date = now;
            response.Headers.TryAddWithoutValidation("subscription-userinfo", "expire=" + expired);
            try { SubscriptionFetcher.RequireUsableResponse(response, now); throw new InvalidOperationException("ExpiredMetadataIgnored"); }
            catch (IOException ex) { Check(ex.Message == "ProviderSubscriptionExpired"); }
        }
        using (var response = new HttpResponseMessage(HttpStatusCode.OK))
        {
            SubscriptionFetcher.RequireUsableResponse(response, now); Check(true);
            response.Headers.TryAddWithoutValidation("subscription-userinfo", new[] { "expire=" + expired, "expire=" + valid });
            Check(ProviderResponseCondition.Inspect(response, now) == "");
        }
        foreach (var item in new[]
        {
            (new DirectInternetEvidence(false, false), "", false, "NetworkUnavailable"),
            (new DirectInternetEvidence(true, false), "", true, "DirectInternetUnconfirmed"),
            (new DirectInternetEvidence(true, true), "", true, "AllTestedVpnUnavailable"),
            (new DirectInternetEvidence(true, true), "", false, "VpnUnavailableInternetWorks"),
            (new DirectInternetEvidence(true, true), "ProviderSubscriptionAccessRejected", true, "SubscriptionAccessRejected"),
            (new DirectInternetEvidence(true, true), "ProviderSubscriptionExpired", true, "SubscriptionExpired"),
            (new DirectInternetEvidence(true, true), "ProviderSubscriptionQuotaExceeded", true, "SubscriptionQuotaExceeded")
        })
        {
            var code = ConnectionDiagnosis.Decide(item.Item1, item.Item2, item.Item3);
            Check(code == item.Item4 && ConnectionProblemText.Describe(code) is not null);
        }
        Check(ConnectionProblemText.Describe("untrusted https://private.invalid/token") is null);
        Check(ConnectionProblemText.Describe("AllTestedVpnUnavailable")!.Explanation.Contains("Возможны"));
        var calls = 0;
        var offline = await DirectInternetProbe.CheckAsync(false, (_, _) => { calls++; return Task.FromResult(true); }, CancellationToken.None);
        Check(!offline.NetworkAvailable && calls == 0);
        var reachable = await DirectInternetProbe.CheckAsync(true, (uri, _) =>
        { calls++; Check(uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0); return Task.FromResult(uri.Host == "www.ozon.ru"); }, CancellationToken.None);
        Check(reachable.HttpsReachable && calls == 2);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await DirectInternetProbe.CheckAsync(true, (_, _) => throw new Exception(), canceled.Token); throw new InvalidOperationException("CancellationIgnored"); }
        catch (OperationCanceledException) { checks++; }
        var controllerChecks = await ConnectionDiagnosisControllerTests.RunAsync();
        return new { status = "Passed", scenarios = checks + controllerChecks, controllerChecks,
            externalNetworkUsed = false, productionNetworkChanged = false, secretDisplayed = false };
    }
}
