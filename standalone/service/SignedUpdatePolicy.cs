using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Service;

internal sealed record SignedUpdateManifest(
    int Schema,
    string Kind,
    string Product,
    string Channel,
    string Version,
    string CoreVersion,
    int MinimumWindowsBuild,
    string PackageUri,
    string PackageSha256,
    string ManifestSha256,
    string SignerId,
    string PublishedAtUtc,
    int RolloutPercent,
    string RollbackVersion,
    string Signature);

internal sealed record VerifiedUpdate(
    string Version,
    string CoreVersion,
    Uri PackageUri,
    string PackageSha256,
    string ManifestSha256,
    string SignerId,
    int RolloutPercent,
    string RollbackVersion,
    bool EligibleForInstall);

internal static class SignedUpdatePolicy
{
    private static readonly string[] PropertyOrder =
    [
        "schema", "kind", "product", "channel", "version", "coreVersion", "minimumWindowsBuild",
        "packageUri", "packageSha256", "manifestSha256", "signerId", "publishedAtUtc", "rolloutPercent",
        "rollbackVersion", "signature"
    ];

    public static VerifiedUpdate Verify(
        string json,
        RSA trustedManifestKey,
        string expectedSignerId,
        string installId,
        int currentWindowsBuild,
        DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json, JsonSettings.Document);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.EnumerateObject().Select(item => item.Name).SequenceEqual(PropertyOrder, StringComparer.Ordinal))
            throw new InvalidDataException("UpdatePropertySetRejected");
        var manifest = JsonSerializer.Deserialize<SignedUpdateManifest>(json, JsonSettings.Strict)
            ?? throw new InvalidDataException("UpdateManifestEmpty");
        ValidateFields(manifest, expectedSignerId, currentWindowsBuild, now);

        byte[] signature;
        try { signature = Convert.FromBase64String(manifest.Signature); }
        catch (FormatException) { throw new InvalidDataException("UpdateSignatureRejected"); }
        var canonical = CanonicalBytes(manifest with { Signature = "" });
        try
        {
            if (!trustedManifestKey.VerifyData(canonical, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("UpdateSignatureRejected");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(signature);
        }

        var bucket = RolloutBucket(installId);
        return new VerifiedUpdate(
            manifest.Version,
            manifest.CoreVersion,
            new Uri(manifest.PackageUri),
            manifest.PackageSha256,
            manifest.ManifestSha256,
            manifest.SignerId,
            manifest.RolloutPercent,
            manifest.RollbackVersion,
            bucket < manifest.RolloutPercent);
    }

    public static string ActivationDecision(
        VerifiedUpdate update,
        bool packageHashVerified,
        bool authenticodeVerified,
        bool smokeTestPassed,
        bool stableFrontCanRemain,
        bool verifiedExternalReserveReady)
    {
        if (!update.EligibleForInstall) return "DeferredByRollout";
        if (!packageHashVerified || !authenticodeVerified || !smokeTestPassed) return "RejectAndKeepCurrent";
        if (!stableFrontCanRemain && !verifiedExternalReserveReady) return "StagedAwaitingContinuity";
        return "ActivateWithAutomaticRollback";
    }

    public static byte[] CanonicalBytes(SignedUpdateManifest manifest)
    {
        var canonical = new
        {
            schema = manifest.Schema,
            kind = manifest.Kind,
            product = manifest.Product,
            channel = manifest.Channel,
            version = manifest.Version,
            coreVersion = manifest.CoreVersion,
            minimumWindowsBuild = manifest.MinimumWindowsBuild,
            packageUri = manifest.PackageUri,
            packageSha256 = manifest.PackageSha256,
            manifestSha256 = manifest.ManifestSha256,
            signerId = manifest.SignerId,
            publishedAtUtc = manifest.PublishedAtUtc,
            rolloutPercent = manifest.RolloutPercent,
            rollbackVersion = manifest.RollbackVersion,
            signature = ""
        };
        return JsonSerializer.SerializeToUtf8Bytes(canonical, JsonSettings.Output);
    }

    public static int RolloutBucket(string installId)
    {
        if (!Regex.IsMatch(installId, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("UpdateInstallIdentityRejected");
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(installId));
        try { return BitConverter.ToUInt16(hash, 0) % 100; }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    private static void ValidateFields(
        SignedUpdateManifest manifest,
        string expectedSignerId,
        int currentWindowsBuild,
        DateTimeOffset now)
    {
        if (manifest.Schema != 1 || manifest.Kind != "art-vpn-signed-update" || manifest.Product != "ART VPN")
            throw new InvalidDataException("UpdateContractRejected");
        if (manifest.Channel is not ("stable" or "preview" or "internal-pilot"))
            throw new InvalidDataException("UpdateChannelRejected");
        if (!Regex.IsMatch(manifest.Version, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(manifest.CoreVersion, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(manifest.RollbackVersion, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("UpdateVersionRejected");
        if (manifest.MinimumWindowsBuild < 17763 || currentWindowsBuild < manifest.MinimumWindowsBuild)
            throw new InvalidDataException("UpdateWindowsBuildRejected");
        if (!Uri.TryCreate(manifest.PackageUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.DnsSafeHost) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            (!uri.IsDefaultPort && uri.Port != 443))
            throw new InvalidDataException("UpdatePackageUriRejected");
        if (!Regex.IsMatch(manifest.PackageSha256, "^[A-F0-9]{64}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(manifest.ManifestSha256, "^[A-F0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("UpdateHashRejected");
        if (!string.Equals(manifest.SignerId, expectedSignerId, StringComparison.Ordinal) ||
            !Regex.IsMatch(manifest.SignerId, "^[A-Za-z0-9][A-Za-z0-9_.-]{2,63}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("UpdateSignerRejected");
        if (!DateTimeOffset.TryParseExact(manifest.PublishedAtUtc, "o", null,
                System.Globalization.DateTimeStyles.None, out var published) ||
            published > now.AddMinutes(5) || now - published > TimeSpan.FromDays(45))
            throw new InvalidDataException("UpdateFreshnessRejected");
        if (manifest.RolloutPercent is < 0 or > 100)
            throw new InvalidDataException("UpdateRolloutRejected");
    }
}

internal static class SignedUpdatePolicyTests
{
    public static SignedUpdateTestReceipt Run()
    {
        using var trusted = RSA.Create(2048);
        using var foreign = RSA.Create(2048);
        var now = DateTimeOffset.Parse("2026-08-31T09:00:00Z");
        const string signer = "art-vpn-release-test";
        const string installId = "0123456789abcdef0123456789abcdef";
        var manifest = new SignedUpdateManifest(
            1, "art-vpn-signed-update", "ART VPN", "stable", "0.1.1", "1.0.1", 17763,
            "https://downloads.example.invalid/art-vpn/0.1.1/ART-VPN-Setup.exe",
            new string('A', 64), new string('B', 64), signer, now.ToString("o"), 100, "0.1.0", "");
        manifest = Sign(manifest, trusted);
        var json = JsonSerializer.Serialize(manifest, JsonSettings.Output);
        var verified = SignedUpdatePolicy.Verify(json, trusted, signer, installId, 19045, now);
        var scenarios = 1;

        ExpectRejected(() => SignedUpdatePolicy.Verify(
            json.Replace("0.1.1", "0.1.2", StringComparison.Ordinal), trusted, signer, installId, 19045, now));
        scenarios++;
        ExpectRejected(() => SignedUpdatePolicy.Verify(json, foreign, signer, installId, 19045, now));
        scenarios++;
        var http = Sign(manifest with { PackageUri = "http://downloads.example.invalid/setup.exe", Signature = "" }, trusted);
        ExpectRejected(() => SignedUpdatePolicy.Verify(JsonSerializer.Serialize(http, JsonSettings.Output), trusted, signer, installId, 19045, now));
        scenarios++;
        var build = Sign(manifest with { MinimumWindowsBuild = 99999, Signature = "" }, trusted);
        ExpectRejected(() => SignedUpdatePolicy.Verify(JsonSerializer.Serialize(build, JsonSettings.Output), trusted, signer, installId, 19045, now));
        scenarios++;

        if (SignedUpdatePolicy.ActivationDecision(verified, false, true, true, true, false) != "RejectAndKeepCurrent")
            throw new InvalidOperationException("UnsignedPackageAccepted");
        scenarios++;
        if (SignedUpdatePolicy.ActivationDecision(verified, true, true, true, false, false) != "StagedAwaitingContinuity")
            throw new InvalidOperationException("ContinuityGateBypassed");
        scenarios++;
        if (SignedUpdatePolicy.ActivationDecision(verified, true, true, true, true, false) != "ActivateWithAutomaticRollback")
            throw new InvalidOperationException("VerifiedUpdateRejected");
        scenarios++;
        var zeroRollout = Sign(manifest with { RolloutPercent = 0, Signature = "" }, trusted);
        var deferred = SignedUpdatePolicy.Verify(JsonSerializer.Serialize(zeroRollout, JsonSettings.Output), trusted, signer, installId, 19045, now);
        if (deferred.EligibleForInstall || SignedUpdatePolicy.ActivationDecision(deferred, true, true, true, true, true) != "DeferredByRollout")
            throw new InvalidOperationException("RolloutGateBypassed");
        scenarios++;

        return new SignedUpdateTestReceipt("Passed", scenarios, true, true, false, false);
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

    private static void ExpectRejected(Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException("UnsafeUpdateAccepted");
        }
        catch (InvalidDataException) { }
    }
}

internal sealed record SignedUpdateTestReceipt(
    string Status,
    int Scenarios,
    bool TamperRejected,
    bool ContinuityRequired,
    bool ActiveVersionLost,
    bool SecretDisplayed);
