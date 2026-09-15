using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Service;

internal sealed record VerifiedRuntimeAssets(
    string CorePath,
    string CoreSha256,
    RuntimeRuleAsset[] Rules,
    string RulesManifestSha256);

internal static partial class RuntimeAssetVerifier
{
    // Same accepted core for the standalone candidate and the lab. The
    // package builder checks these values against provenance/core-release.v1.json.
    public const string ExpectedCoreSha256 = "BF0866CCD7508755E64A12E54EF653729947F3CEE95F96450B2E5411577AAD61";
    public const long ExpectedCoreBytes = 81_978_368;

    private static readonly HashSet<string> ExpectedRuleTags = new(StringComparer.Ordinal)
    {
        "art-geoip-ru", "art-geosite-category-ru", "art-geosite-ru", "art-geosite-ru-inside"
    };

    [GeneratedRegex("^[A-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShaPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]+\\.srs$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleNamePattern();

    public static VerifiedRuntimeAssets Open(
        RuntimeOptions options,
        string expectedCoreSha256 = ExpectedCoreSha256,
        long expectedCoreBytes = ExpectedCoreBytes,
        string? rulesDirectory = null)
    {
        var runtimeRoot = Path.GetFullPath(options.RuntimeRoot).TrimEnd(Path.DirectorySeparatorChar);
        var corePath = Path.Combine(runtimeRoot, "ARTVpnCore.exe");
        var core = new FileInfo(corePath);
        if (!core.Exists || core.LinkTarget is not null || core.Length != expectedCoreBytes ||
            !ShaPattern().IsMatch(expectedCoreSha256))
            throw new InvalidDataException("RuntimeCoreIdentityRejected");
        var coreHash = HashFile(corePath);
        if (coreHash != expectedCoreSha256) throw new InvalidDataException("RuntimeCoreHashRejected");

        var rulesRoot = rulesDirectory ?? Path.Combine(runtimeRoot, "rules");
        if (rulesDirectory is not null)
        {
            var allowed = Path.GetFullPath(Path.Combine(options.PrivateRoot, "bypass")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(rulesRoot).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) ||
                new DirectoryInfo(rulesRoot).LinkTarget is not null)
                throw new InvalidDataException("BypassDirectoryRejected");
        }
        var manifestPath = Path.Combine(rulesRoot, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("RuntimeRulesManifestMissing");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8), JsonSettings.Document);
        var root = document.RootElement;
        var rootNames = root.EnumerateObject().Select(item => item.Name).ToArray();
        if (!rootNames.SequenceEqual(["source", "policy", "files"], StringComparer.Ordinal) ||
            root.GetProperty("source").GetString() is not { Length: > 0 } ||
            root.GetProperty("policy").GetString() != "immutable and hash-pinned; replacement requires isolated candidate validation" ||
            root.GetProperty("files").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("RuntimeRulesManifestRejected");
        var rules = new List<RuntimeRuleAsset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("files").EnumerateArray())
        {
            var names = item.EnumerateObject().Select(property => property.Name).ToArray();
            if (!names.SequenceEqual(
                    ["tag", "repository", "branch", "path", "git_blob", "installed_name", "bytes", "sha256"],
                    StringComparer.Ordinal))
                throw new InvalidDataException("RuntimeRuleEntryRejected");
            var tag = item.GetProperty("tag").GetString() ?? "";
            var name = item.GetProperty("installed_name").GetString() ?? "";
            var hash = item.GetProperty("sha256").GetString() ?? "";
            var bytes = item.GetProperty("bytes").GetInt64();
            if (!ExpectedRuleTags.Contains(tag) || !seen.Add(tag) || !RuleNamePattern().IsMatch(name) ||
                !ShaPattern().IsMatch(hash) || bytes < 4)
                throw new InvalidDataException("RuntimeRuleIdentityRejected");
            var path = Path.GetFullPath(Path.Combine(rulesRoot, name));
            var prefix = Path.GetFullPath(rulesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var file = new FileInfo(path);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !file.Exists || file.LinkTarget is not null ||
                file.Length != bytes || HashFile(path) != hash)
                throw new InvalidDataException("RuntimeRuleFileRejected");
            var header = new byte[4];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (stream.Read(header, 0, header.Length) != header.Length || header[0] != 'S' || header[1] != 'R' ||
                    header[2] != 'S' || header[3] is not (1 or 2))
                    throw new InvalidDataException("RuntimeRuleFormatRejected");
            rules.Add(new RuntimeRuleAsset(tag, path));
        }
        if (!ExpectedRuleTags.SetEquals(seen)) throw new InvalidDataException("RuntimeRuleCoverageRejected");
        return new VerifiedRuntimeAssets(corePath, coreHash,
            rules.OrderBy(rule => rule.Tag, StringComparer.Ordinal).ToArray(), HashFile(manifestPath));
    }

    public static VerifiedRuntimeAssets ForConfig(RuntimeOptions options, string configPath, VerifiedRuntimeAssets bundled)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(configPath), JsonSettings.Document);
        var sets = config.RootElement.GetProperty("route").GetProperty("rule_set").EnumerateArray().ToArray();
        if (sets.Length != 4) throw new InvalidDataException("RuntimeRuleCoverageRejected");
        var paths = sets.Select(item => item.GetProperty("path").GetString() ?? "").ToArray();
        var roots = paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length != 1) throw new InvalidDataException("RuntimeRuleDirectoryMismatch");
        var selected = string.Equals(roots[0], Path.Combine(options.RuntimeRoot, "rules"), StringComparison.OrdinalIgnoreCase)
            ? bundled : Open(options, rulesDirectory: roots[0]);
        if (!selected.Rules.Select(rule => rule.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(paths))
            throw new InvalidDataException("RuntimeRulePathMismatch");
        return selected;
    }

    public static bool IsInstalled(RuntimeOptions options)
    {
        try { _ = Open(options); return true; }
        catch { return false; }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal static class RuntimeAssetTests
{
    public static RuntimeAssetTestReceipt Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-assets-test-" + Guid.NewGuid().ToString("N"));
        var options = RuntimeOptions.Test(root, "ARTSPORT.ARTVpn.AssetsTest." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(options.RuntimeRoot, "rules"));
            var core = Path.Combine(options.RuntimeRoot, "ARTVpnCore.exe");
            File.WriteAllBytes(core, Encoding.ASCII.GetBytes("fixture-core"));
            var entries = new List<object>();
            foreach (var pair in new[]
                     {
                         ("art-geoip-ru", "geoip.srs"), ("art-geosite-category-ru", "category.srs"),
                         ("art-geosite-ru", "ru.srs"), ("art-geosite-ru-inside", "inside.srs")
                     })
            {
                var path = Path.Combine(options.RuntimeRoot, "rules", pair.Item2);
                File.WriteAllBytes(path, [(byte)'S', (byte)'R', (byte)'S', 1, 7, 8, 9]);
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                entries.Add(new
                {
                    tag = pair.Item1, repository = "fixture/repo", branch = "main", path = "fixture/" + pair.Item2,
                    git_blob = new string('a', 40), installed_name = pair.Item2, bytes = 7, sha256 = hash
                });
            }
            File.WriteAllText(Path.Combine(options.RuntimeRoot, "rules", "manifest.json"), JsonSerializer.Serialize(new
            {
                source = "fixture",
                policy = "immutable and hash-pinned; replacement requires isolated candidate validation",
                files = entries
            }, JsonSettings.Output), Encoding.UTF8);
            var coreBytes = new FileInfo(core).Length;
            var coreHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(core)));
            var accepted = RuntimeAssetVerifier.Open(options, coreHash, coreBytes);
            Assert(accepted.Rules.Length == 4 && accepted.CoreSha256 == coreHash);
            File.AppendAllText(accepted.Rules[0].Path, "tamper", Encoding.ASCII);
            try
            {
                _ = RuntimeAssetVerifier.Open(options, coreHash, coreBytes);
                throw new InvalidOperationException("TamperedRuleAccepted");
            }
            catch (InvalidDataException) { }
            return new RuntimeAssetTestReceipt("Passed", 9, true, true, true, false);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("art-vpn-assets-test-", StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("RuntimeAssetInvariantFailed");
    }
}

internal sealed record RuntimeAssetTestReceipt(
    string Status,
    int Scenarios,
    bool CorePinned,
    bool RulesPinned,
    bool TamperRejected,
    bool SecretDisplayed);
