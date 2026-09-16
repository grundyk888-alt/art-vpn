using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Service;

internal sealed record BypassRefreshReceipt(string Status, string CheckedAtUtc, string NextCheckAtUtc, string RulesDirectory);

// Updated rules are immutable candidate assets. Only the normal qualified A/B
// promotion can put them into service; downloading never rewrites active rules.
internal static class BypassRuleRefresh
{
    private const int MaxRuleBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> Sources = new(StringComparer.Ordinal)
    {
        "MetaCubeX/meta-rules-dat|sing|geo/geoip/ru.srs",
        "MetaCubeX/meta-rules-dat|sing|geo/geosite/category-ru.srs",
        "hiddify/hiddify-geo|rule-set|country/geosite-ru.srs",
        "runetfreedom/russia-v2ray-rules-dat|release|sing-box/rule-set-geosite/geosite-ru-available-only-inside.srs"
    };
    private static string ReceiptPath(RuntimeOptions options) => Path.Combine(options.DataRoot, "state", "bypass-refresh.v1.json");

    public static string DescribeActive(RuntimeOptions options)
    {
        try
        {
            var active = ActiveGenerationStore.TryRead(options);
            if (active is not null)
            {
                var assets = RuntimeAssetVerifier.ForConfig(options, active.ConfigPath, RuntimeAssetVerifier.Open(options));
                var updated = assets.Rules.All(rule => rule.Path.StartsWith(Path.Combine(options.PrivateRoot, "bypass") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                BypassRefreshReceipt? receipt = null;
                try { receipt = JsonSerializer.Deserialize<BypassRefreshReceipt>(File.ReadAllText(ReceiptPath(options)), JsonSettings.Strict); }
                catch { }
                var activeDirectory = Path.GetDirectoryName(assets.Rules[0].Path)!;
                return "РФ, Avito и Ozon напрямую • " + DescribeRefresh(receipt, activeDirectory, updated);
            }
        }
        catch { }
        return "РФ, Avito и Ozon: правила подготовлены; действующий канал ещё не подтверждён";
    }

    internal static string DescribeRefresh(BypassRefreshReceipt? receipt, string activeDirectory, bool updated)
    {
        if (receipt?.Status == "PreviousRulesRetained")
            return "обновление не прошло проверку; прежние списки сохранены";
        if (receipt?.Status == "CandidateVerified" &&
            !string.Equals(Path.TrimEndingDirectorySeparator(receipt.RulesDirectory),
                Path.TrimEndingDirectorySeparator(activeDirectory), StringComparison.OrdinalIgnoreCase))
            return "новые списки скачаны; ожидают проверки нового канала";
        return updated ? "обновлённые списки работают • автопроверка ежедневно"
            : "списки из комплекта • автопроверка ежедневно";
    }

    public static async Task<VerifiedRuntimeAssets> ForCandidateAsync(RuntimeOptions options, CancellationToken cancellationToken,
        Func<string?, VerifiedRuntimeAssets>? openAssets = null,
        Func<Uri, int, CancellationToken, Task<byte[]>>? fetch = null,
        DateTimeOffset? nowOverride = null, bool deferRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        openAssets ??= directory => RuntimeAssetVerifier.Open(options, rulesDirectory: directory);
        var bundled = openAssets(null);
        var previous = CurrentAssets(options, bundled);
        // Initial connection needs verified local rules, not seven upstream
        // update requests (up to 90 seconds without an existing VPN). Keep the
        // active verified rules, or bundled rules on a clean install. The normal
        // background refinement still downloads and validates updates in full.
        if (deferRefresh) return previous;
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        BypassRefreshReceipt? receipt = null;
        try { receipt = JsonSerializer.Deserialize<BypassRefreshReceipt>(File.ReadAllText(ReceiptPath(options)), JsonSettings.Strict); }
        catch { }
        if (receipt is not null && DateTimeOffset.TryParse(receipt.NextCheckAtUtc, out var next) && next > now && next <= now.AddDays(1))
        {
            if (receipt.Status == "CandidateVerified")
            {
                try { return openAssets(receipt.RulesDirectory); }
                catch { return previous; }
            }
            return previous;
        }

        var parent = Path.Combine(options.PrivateRoot, "bypass");
        var staging = Path.Combine(parent, "stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
            if (ActiveGenerationStore.TryRead(options) is not null)
                handler.Proxy = new WebProxy("http://127.0.0.1:22080");
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ART-VPN-Bypass/1");
            fetch ??= (uri, maximum, token) => FetchAsync(client, uri, maximum, token);
            var source = JsonNode.Parse(File.ReadAllText(Path.Combine(options.RuntimeRoot, "rules", "manifest.json")))!.AsObject();
            var commits = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in source["files"]!.AsArray())
            {
                var item = entry!.AsObject();
                var repository = item["repository"]!.GetValue<string>();
                var branch = item["branch"]!.GetValue<string>();
                var path = item["path"]!.GetValue<string>();
                if (!Sources.Contains(repository + "|" + branch + "|" + path))
                    throw new InvalidDataException("BypassSourceRejected");
                var key = repository + "|" + branch;
                if (!commits.TryGetValue(key, out var commit))
                {
                    var metadata = await fetch(new Uri($"https://api.github.com/repos/{repository}/commits/{branch}"),
                        512 * 1024, deadline.Token).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(metadata, JsonSettings.Document);
                    commit = document.RootElement.GetProperty("sha").GetString() ?? "";
                    if (!Regex.IsMatch(commit, "^[a-f0-9]{40}$", RegexOptions.CultureInvariant))
                        throw new InvalidDataException("BypassCommitRejected");
                    commits.Add(key, commit);
                }
                var payload = await fetch(new Uri($"https://raw.githubusercontent.com/{repository}/{commit}/{path}"),
                    MaxRuleBytes, deadline.Token).ConfigureAwait(false);
                var previousRule = previous.Rules.Single(rule => rule.Tag == item["tag"]!.GetValue<string>());
                ValidatePayload(payload, new FileInfo(previousRule.Path).Length);
                var hash = Convert.ToHexString(SHA256.HashData(payload));
                var name = item["tag"]!.GetValue<string>() + "-" + hash[..16] + ".srs";
                await File.WriteAllBytesAsync(Path.Combine(staging, name), payload, deadline.Token).ConfigureAwait(false);
                // Exact fetched commit is retained separately from the content-addressed blob.
                item["branch"] = commit;
                var gitHeader = Encoding.ASCII.GetBytes("blob " + payload.Length + "\0");
                using var gitHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                gitHash.AppendData(gitHeader); gitHash.AppendData(payload);
                item["git_blob"] = Convert.ToHexString(gitHash.GetHashAndReset()).ToLowerInvariant();
                item["installed_name"] = name;
                item["bytes"] = payload.Length;
                item["sha256"] = hash;
            }
            source["source"] = "Original rule publishers; exact commits fetched over HTTPS; isolated qualification required";
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), source.ToJsonString(JsonSettings.Output), deadline.Token).ConfigureAwait(false);
            _ = openAssets(staging);
            var final = Path.Combine(parent, "rules-" + Guid.NewGuid().ToString("N"));
            Directory.Move(staging, final);
            var candidate = openAssets(final);
            AtomicFile.ReplaceJson(ReceiptPath(options), new BypassRefreshReceipt("CandidateVerified", now.ToString("o"), now.AddDays(1).ToString("o"), final));
            return candidate;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Rule-source failure must not take the subscription or existing VPN down.
            try { AtomicFile.ReplaceJson(ReceiptPath(options), new BypassRefreshReceipt("PreviousRulesRetained", now.ToString("o"), now.AddHours(1).ToString("o"), "")); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return previous;
        }
        finally
        {
            // Exact generated directory, never an active rules directory.
            try
            {
                if (Directory.Exists(staging) && Path.GetDirectoryName(staging) == parent && Path.GetFileName(staging).StartsWith("stage-", StringComparison.Ordinal))
                    Directory.Delete(staging, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static VerifiedRuntimeAssets CurrentAssets(RuntimeOptions options, VerifiedRuntimeAssets bundled)
    {
        var active = ActiveGenerationStore.TryRead(options);
        if (active is null) return bundled;
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(active.ConfigPath), JsonSettings.Document);
            var paths = config.RootElement.GetProperty("route").GetProperty("rule_set").EnumerateArray()
                .Select(item => item.GetProperty("path").GetString()!).ToArray();
            var roots = paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (roots.Length != 1 || string.Equals(roots[0], Path.Combine(options.RuntimeRoot, "rules"), StringComparison.OrdinalIgnoreCase)) return bundled;
            return RuntimeAssetVerifier.Open(options, rulesDirectory: roots[0]);
        }
        catch { return bundled; }
    }

    internal static void ValidatePayload(byte[] bytes, long previousBytes)
    {
        if (bytes.Length < 32 || bytes.Length > MaxRuleBytes || bytes[0] != 'S' || bytes[1] != 'R' || bytes[2] != 'S' || bytes[3] is not (1 or 2)
            || bytes.Length < previousBytes / 4 || bytes.Length > Math.Max(previousBytes * 4, 65536))
            throw new InvalidDataException("BypassPayloadRejected");
    }

    private static async Task<byte[]> FetchAsync(HttpClient client, Uri uri, int maximum, CancellationToken token)
    {
        if (uri.Scheme != "https" || uri.DnsSafeHost is not ("api.github.com" or "raw.githubusercontent.com"))
            throw new InvalidDataException("BypassHostRejected");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("BypassSourceUnavailable");
        return await BoundedHttpContent.ReadAsync(response.Content, maximum, token).ConfigureAwait(false);
    }
}
