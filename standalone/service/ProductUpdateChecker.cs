using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Service;

internal delegate Task<byte[]> UpdateManifestFetch(Uri uri, CancellationToken cancellationToken);

internal sealed class UpdateChannelAccessRequiredException() : IOException("UpdateChannelAccessRequired");

internal sealed record UpdateChannelContract(
    int Schema,
    string Kind,
    bool Enabled,
    string ManifestUri,
    string ManifestHost,
    string PackageHost,
    string SignerId,
    string Channel,
    string PublicKeyPem);

internal sealed record ProductUpdateCheckReceipt(
    int Schema,
    string Kind,
    string Status,
    string CurrentVersion,
    string AvailableVersion,
    string DetailCode,
    string CheckedAtUtc,
    bool UserActionRequired,
    bool ContainsProviderSecret,
    string PackageUri = "");

internal static partial class ProductUpdateChecker
{
    private const int MaxManifestBytes = 64 * 1024;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentity();

    public static async Task<ProductUpdateCheckReceipt> CheckAsync(
        RuntimeOptions options,
        InstallState install,
        UpdateManifestFetch? fetch,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(options.UpdateChannelPath))
            return Write(options, install.Version, "NotConfigured", "", "UpdateChannelNotConfigured", now, false);
        var contract = ReadContract(options.UpdateChannelPath);
        if (!contract.Enabled)
            return Write(options, install.Version, "Disabled", "", "UpdateChannelDisabled", now, false);
        var manifestUri = ValidateHttps(contract.ManifestUri, contract.ManifestHost, "UpdateManifestUriRejected");
        byte[] payload;
        try { payload = await (fetch ?? UpdateManifestFetcher.DownloadAsync)(manifestUri, cancellationToken).ConfigureAwait(false); }
        catch (UpdateChannelAccessRequiredException)
        {
            // A private release feed is a publisher-side problem, not a VPN failure.
            // Never follow sign-in, request user credentials or treat HTML as an update.
            return Write(options, install.Version, "ChannelUnavailable", "", "UpdateChannelAccessRequired", now, false);
        }
        try
        {
            if (payload.Length is 0 or > MaxManifestBytes) throw new InvalidDataException("UpdateManifestSizeRejected");
            var json = new UTF8Encoding(false, true).GetString(payload);
            using var key = RSA.Create();
            try { key.ImportFromPem(contract.PublicKeyPem); }
            catch (ArgumentException) { throw new InvalidDataException("UpdatePublicKeyRejected"); }
            var verified = SignedUpdatePolicy.Verify(json, key, contract.SignerId, install.InstallId,
                Environment.OSVersion.Version.Build, now);
            using (var document = JsonDocument.Parse(json, JsonSettings.Document))
                if (document.RootElement.GetProperty("channel").GetString() != contract.Channel)
                    throw new InvalidDataException("UpdateChannelRejected");
            if (!string.Equals(verified.PackageUri.DnsSafeHost, contract.PackageHost, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UpdatePackageHostRejected");

            var comparison = ProductVersion.Compare(verified.Version, install.Version);
            if (comparison <= 0)
                return Write(options, install.Version, "Current", verified.Version, "LatestVersionInstalled", now, false);
            if (!verified.EligibleForInstall)
                return Write(options, install.Version, "Deferred", verified.Version, "DeferredByRollout", now, false);
            return Write(options, install.Version, "Available", verified.Version, "SignedUpdateAvailable", now, true, verified.PackageUri.AbsoluteUri);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static UpdateChannelContract ReadContract(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        using var document = JsonDocument.Parse(json, JsonSettings.Document);
        if (!document.RootElement.EnumerateObject().Select(item => item.Name).SequenceEqual(
                ["schema", "kind", "enabled", "manifestUri", "manifestHost", "packageHost", "signerId", "channel", "publicKeyPem"],
                StringComparer.Ordinal))
            throw new InvalidDataException("UpdateChannelContractRejected");
        var contract = JsonSerializer.Deserialize<UpdateChannelContract>(json, JsonSettings.Strict)
            ?? throw new InvalidDataException("UpdateChannelContractRejected");
        if (contract.Schema != 1 || contract.Kind != "art-vpn-update-channel" ||
            !SafeIdentity().IsMatch(contract.SignerId) || contract.Channel is not ("stable" or "preview" or "internal-pilot") ||
            !IsHost(contract.ManifestHost) || !IsHost(contract.PackageHost) ||
            contract.PublicKeyPem.Length is < 200 or > 4_096)
            throw new InvalidDataException("UpdateChannelContractRejected");
        _ = ValidateHttps(contract.ManifestUri, contract.ManifestHost, "UpdateManifestUriRejected");
        return contract;
    }

    private static Uri ValidateHttps(string value, string exactHost, string code)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.DnsSafeHost, exactHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(code);
        return uri;
    }

    private static bool IsHost(string value) =>
        value.Length is > 0 and <= 253 && Uri.CheckHostName(value) is UriHostNameType.Dns;

    private static ProductUpdateCheckReceipt Write(
        RuntimeOptions options, string current, string status, string available, string code,
        DateTimeOffset now, bool userActionRequired, string packageUri = "")
    {
        var receipt = new ProductUpdateCheckReceipt(1, "art-vpn-update-check", status, current, available,
            code, now.ToString("o"), userActionRequired, false, packageUri);
        AtomicFile.ReplaceJson(options.UpdateCheckReceiptPath, receipt);
        return receipt;
    }
}

internal static class UpdateManifestFetcher
{
    public static async Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
        if (File.Exists(RuntimeOptions.Production().SystemProxyLeasePath))
            handler.Proxy = new WebProxy("http://127.0.0.1:22080");
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return await DownloadAsync(client, uri, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]> DownloadAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        var initial = uri;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("ART-VPN-Update/1");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                if (initial.Host != "drive.google.com" || location is null) throw new IOException("UpdateManifestRedirectRejected");
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (next.Scheme == "https" && next.IsDefaultPort && string.IsNullOrEmpty(next.UserInfo) &&
                    string.IsNullOrEmpty(next.Fragment) && next.DnsSafeHost == "accounts.google.com")
                    throw new UpdateChannelAccessRequiredException();
                if (!AllowedDriveRedirect(next)) throw new IOException("UpdateManifestRedirectRejected");
                uri = next;
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) throw new IOException("UpdateManifestHttpRejected");
            if (response.Content.Headers.ContentLength is > 64 * 1024) throw new InvalidDataException("UpdateManifestSizeRejected");
            return await BoundedHttpContent.ReadAsync(response.Content, 64 * 1024, cancellationToken).ConfigureAwait(false);
        }
        throw new IOException("UpdateManifestRedirectLimit");
    }

    internal static bool AllowedDriveRedirect(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        (uri.DnsSafeHost == "drive.google.com" || uri.DnsSafeHost == "drive.usercontent.google.com" ||
         uri.DnsSafeHost.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));
}

internal static class ProductVersion
{
    public static int Compare(string left, string right)
    {
        var a = Parse(left);
        var b = Parse(right);
        for (var index = 0; index < 3; index++)
        {
            var number = a.Numbers[index].CompareTo(b.Numbers[index]);
            if (number != 0) return number;
        }
        if (a.PreRelease.Length == 0 && b.PreRelease.Length > 0) return 1;
        if (a.PreRelease.Length > 0 && b.PreRelease.Length == 0) return -1;
        var leftParts = a.PreRelease.Split('.');
        var rightParts = b.PreRelease.Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            var x = leftParts[index];
            var y = rightParts[index];
            var xn = x.Length > 0 && x.All(char.IsAsciiDigit);
            var yn = y.Length > 0 && y.All(char.IsAsciiDigit);
            var order = xn && yn
                ? (x.Length == y.Length ? string.CompareOrdinal(x, y) : x.Length.CompareTo(y.Length))
                : xn != yn ? (xn ? -1 : 1) : string.CompareOrdinal(x, y);
            if (order != 0) return order;
        }
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static (int[] Numbers, string PreRelease) Parse(string value)
    {
        if (value.Length > 128 || !Regex.IsMatch(value,
                @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
                RegexOptions.CultureInvariant))
            throw new InvalidDataException("UpdateVersionRejected");
        var split = value.Split('-', 2);
        var numbers = split[0].Split('.');
        if (numbers.Length != 3 || numbers.Any(item => !int.TryParse(item, out _)))
            throw new InvalidDataException("UpdateVersionRejected");
        if (split.Length == 2 && split[1].Split('.').Any(item => item.Length > 1 && item[0] == '0' && item.All(char.IsAsciiDigit)))
            throw new InvalidDataException("UpdateVersionRejected");
        return (numbers.Select(int.Parse).ToArray(), split.Length == 2 ? split[1] : "");
    }
}

internal static class ProductUpdateCheckerTests
{
    public static async Task<ProductUpdateCheckerTestReceipt> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-update-check-" + Guid.NewGuid().ToString("N"));
        var options = RuntimeOptions.Test(root, "ARTSPORT.ARTVpn.UpdateCheck." + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        using var key = RSA.Create(2048);
        var install = new InstallState(1, "art-vpn-consumer-install", "Configured", "TEST-PC",
            "S-1-5-21-111-222-333", "S-1-5-21-111-222-333-1001", Guid.NewGuid().ToString("N"),
            "0.1.0-beta.1", new string('A', 64), now.ToString("o"), false, false);
        try
        {
            var notConfigured = await ProductUpdateChecker.CheckAsync(options, install, null, now, CancellationToken.None);
            if (notConfigured.Status != "NotConfigured") throw new InvalidOperationException("MissingChannelAccepted");
            AtomicFile.ReplaceJson(options.UpdateChannelPath, new UpdateChannelContract(1, "art-vpn-update-channel", true,
                "https://updates.example.invalid/art-vpn/preview.json", "updates.example.invalid", "downloads.example.invalid",
                "ARTSPORT-RELEASE-1", "preview", key.ExportSubjectPublicKeyInfoPem()));
            var manifest = Sign(new SignedUpdateManifest(1, "art-vpn-signed-update", "ART VPN", "preview",
                "0.1.1-beta.1", "0.1.1-beta.1", 17763,
                "https://downloads.example.invalid/art-vpn/ART-VPN-0.1.1.zip", new string('B', 64), new string('C', 64),
                "ARTSPORT-RELEASE-1", now.ToString("o"), 100, "0.1.0-beta.1", ""), key);
            UpdateManifestFetch fetch = (_, _) => Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonSettings.Output));
            var available = await ProductUpdateChecker.CheckAsync(options, install, fetch, now, CancellationToken.None);
            if (available.Status != "Available" || !available.UserActionRequired || available.AvailableVersion != "0.1.1-beta.1" || available.PackageUri != manifest.PackageUri)
                throw new InvalidOperationException("SignedUpdateNotOffered");
            var tampered = manifest with { PackageSha256 = new string('D', 64) };
            UpdateManifestFetch bad = (_, _) => Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(tampered, JsonSettings.Output));
            var rejected = false;
            try { _ = await ProductUpdateChecker.CheckAsync(options, install, bad, now, CancellationToken.None); }
            catch (InvalidDataException ex) when (ex.Message == "UpdateSignatureRejected") { rejected = true; }
            if (!rejected) throw new InvalidOperationException("TamperedUpdateAccepted");
            UpdateManifestFetch privateFeed = (_, _) => throw new UpdateChannelAccessRequiredException();
            var unavailable = await ProductUpdateChecker.CheckAsync(options, install, privateFeed, now, CancellationToken.None);
            if (unavailable.Status != "ChannelUnavailable" || unavailable.UserActionRequired ||
                unavailable.AvailableVersion.Length != 0 || unavailable.PackageUri.Length != 0)
                throw new InvalidOperationException("PrivateFeedReportedAsUpdateOrVpnFailure");
            var unavailableReadback = JsonSerializer.Deserialize<ProductUpdateCheckReceipt>(
                File.ReadAllText(options.UpdateCheckReceiptPath), JsonSettings.Strict);
            if (unavailableReadback != unavailable) throw new InvalidOperationException("PrivateFeedReceiptNotPersisted");
            await VerifyFetchBoundariesAsync().ConfigureAwait(false);
            if (!UpdateManifestFetcher.AllowedDriveRedirect(new Uri("https://drive.usercontent.google.com/download?id=fixture")) ||
                UpdateManifestFetcher.AllowedDriveRedirect(new Uri("http://drive.usercontent.google.com/download")) ||
                UpdateManifestFetcher.AllowedDriveRedirect(new Uri("https://drive.usercontent.google.com.evil.invalid/download")) ||
                UpdateManifestFetcher.AllowedDriveRedirect(new Uri("https://user:secret@drive.google.com/download")))
                throw new InvalidOperationException("UpdateRedirectPolicyFailed");
            if (ProductVersion.Compare("1.0.0", "1.0.0-beta.99") <= 0 || ProductVersion.Compare("0.1.0-beta.10", "0.1.0-beta.9") <= 0)
                throw new InvalidOperationException("SemanticVersionComparisonFailed");
            return new ProductUpdateCheckerTestReceipt("Passed", 24, true, true, true, true, false, false);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task VerifyFetchBoundariesAsync()
    {
        // Exercise the actual fetch loop. The login endpoint must never receive a request.
        using var handler = new FixtureHandler((index, _) => index switch
        {
            1 => Redirect("https://drive.usercontent.google.com/download?id=fixture"),
            2 => Redirect("https://accounts.google.com/ServiceLogin?continue=fixture"),
            _ => throw new InvalidOperationException("UpdateFetcherFollowedAuthentication")
        });
        using var client = new HttpClient(handler);
        try
        {
            _ = await UpdateManifestFetcher.DownloadAsync(client, new Uri("https://drive.google.com/uc?id=fixture"), CancellationToken.None);
            throw new InvalidOperationException("PrivateFeedAccepted");
        }
        catch (UpdateChannelAccessRequiredException) { }
        if (handler.Requests != 2) throw new InvalidOperationException("AuthenticationRequestCountWrong");

        foreach (var target in new[] { "http://accounts.google.com/ServiceLogin", "https://accounts.google.com.evil.invalid/",
                     "https://user:fixture@accounts.google.com/", "https://127.0.0.1/" })
        {
            using var rejectedHandler = new FixtureHandler((_, _) => Redirect(target));
            using var rejectedClient = new HttpClient(rejectedHandler);
            try
            {
                _ = await UpdateManifestFetcher.DownloadAsync(rejectedClient, new Uri("https://drive.google.com/uc?id=fixture"), CancellationToken.None);
                throw new InvalidOperationException("UnsafeUpdateRedirectAccepted");
            }
            catch (IOException ex) when (ex.Message == "UpdateManifestRedirectRejected") { }
            if (rejectedHandler.Requests != 1) throw new InvalidOperationException("UnsafeRedirectFollowed");
        }
        using var successHandler = new FixtureHandler((index, _) => index == 1
            ? Redirect("https://drive.usercontent.google.com/download?id=fixture")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("fixture-manifest") });
        using var successClient = new HttpClient(successHandler);
        var bytes = await UpdateManifestFetcher.DownloadAsync(successClient, new Uri("https://drive.google.com/uc?id=fixture"), CancellationToken.None);
        if (Encoding.UTF8.GetString(bytes) != "fixture-manifest" || successHandler.Requests != 2)
            throw new InvalidOperationException("AllowedUpdateDeliveryBroken");
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location) }
    };

    private sealed class FixtureHandler(Func<int, HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(++Requests, request));
    }

    private static SignedUpdateManifest Sign(SignedUpdateManifest manifest, RSA key)
    {
        var canonical = SignedUpdatePolicy.CanonicalBytes(manifest with { Signature = "" });
        try
        {
            var signature = key.SignData(canonical, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            try { return manifest with { Signature = Convert.ToBase64String(signature) }; }
            finally { CryptographicOperations.ZeroMemory(signature); }
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }
}

internal sealed record ProductUpdateCheckerTestReceipt(
    string Status,
    int Scenarios,
    bool SignedManifestRequired,
    bool ExactHostsRequired,
    bool NewerVersionOffered,
    bool TamperRejected,
    bool BackgroundInstall,
    bool SecretDisplayed);
