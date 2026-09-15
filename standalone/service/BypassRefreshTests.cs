using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArtSport.ArtVpn.Service;

internal static class BypassRefreshTests
{
    public static async Task<object> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-bypass-test-" + Guid.NewGuid().ToString("N"));
        var options = RuntimeOptions.Test(root, "ARTSPORT.BypassTest." + Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Check(bool ok) { if (!ok) throw new InvalidOperationException("BypassRegressionFailed:" + checks); checks++; }
        var now = DateTimeOffset.UtcNow;
        try
        {
            Check(BypassRuleRefresh.DescribeRefresh(null, "C:\\active", false).Contains("из комплекта"));
            Check(BypassRuleRefresh.DescribeRefresh(new("PreviousRulesRetained", "", "", ""), "C:\\active", true).Contains("не прошло проверку"));
            Check(BypassRuleRefresh.DescribeRefresh(new("CandidateVerified", "", "", "C:\\new"), "C:\\active", true).Contains("ожидают"));
            Check(BypassRuleRefresh.DescribeRefresh(new("CandidateVerified", "", "", "C:\\active\\"), "C:\\active", true).Contains("работают"));
            var rules = Path.Combine(options.RuntimeRoot, "rules");
            Directory.CreateDirectory(rules);
            var core = Encoding.ASCII.GetBytes("isolated-fixture-not-executable");
            File.WriteAllBytes(Path.Combine(options.RuntimeRoot, "ARTVpnCore.exe"), core);
            var coreHash = Convert.ToHexString(SHA256.HashData(core));
            var payload = new byte[128];
            payload[0] = (byte)'S'; payload[1] = (byte)'R'; payload[2] = (byte)'S'; payload[3] = 2;
            var sources = new[] {
                ("art-geoip-ru", "MetaCubeX/meta-rules-dat", "sing", "geo/geoip/ru.srs"),
                ("art-geosite-category-ru", "MetaCubeX/meta-rules-dat", "sing", "geo/geosite/category-ru.srs"),
                ("art-geosite-ru", "hiddify/hiddify-geo", "rule-set", "country/geosite-ru.srs"),
                ("art-geosite-ru-inside", "runetfreedom/russia-v2ray-rules-dat", "release", "sing-box/rule-set-geosite/geosite-ru-available-only-inside.srs")
            };
            var entries = sources.Select(s => new { tag=s.Item1, repository=s.Item2, branch=s.Item3, path=s.Item4,
                git_blob=new string('a',40), installed_name=s.Item1+".srs", bytes=payload.Length,
                sha256=Convert.ToHexString(SHA256.HashData(payload)) }).ToArray();
            foreach (var entry in entries) File.WriteAllBytes(Path.Combine(rules, entry.installed_name), payload);
            var manifest = JsonSerializer.Serialize(new { source="fixture", policy="immutable and hash-pinned; replacement requires isolated candidate validation", files=entries }, JsonSettings.Output);
            File.WriteAllText(Path.Combine(rules, "manifest.json"), manifest);
            VerifiedRuntimeAssets Open(string? directory) => RuntimeAssetVerifier.Open(options, coreHash, core.Length, directory);
            var original = Open(null);
            var updated = payload.ToArray(); updated[127] = 42;
            var calls = 0;
            Task<byte[]> Fetch(Uri uri, int maximum, CancellationToken token)
            {
                calls++;
                token.ThrowIfCancellationRequested();
                if (uri.Host == "api.github.com") return Task.FromResult(Encoding.UTF8.GetBytes("{\"sha\":\"" + new string('b',40) + "\"}"));
                Check(uri.Host == "raw.githubusercontent.com" && uri.AbsolutePath.Contains("/"+new string('b',40)+"/"));
                return Task.FromResult(updated.ToArray());
            }
            var candidate = await BypassRuleRefresh.ForCandidateAsync(options, default, Open, Fetch, now);
            Check(candidate.Rules.Length == 4 && candidate.RulesManifestSha256 != original.RulesManifestSha256);
            Check(calls == 7); // three distinct repositories/branches, four payloads
            Check(Open(null).RulesManifestSha256 == original.RulesManifestSha256);
            Check(candidate.Rules.All(r => File.ReadAllBytes(r.Path)[127] == 42));
            var cached = await BypassRuleRefresh.ForCandidateAsync(options, default, Open, Fetch, now.AddMinutes(1));
            Check(calls == 7 && cached.RulesManifestSha256 == candidate.RulesManifestSha256);
            File.AppendAllText(candidate.Rules[0].Path,"corrupt");
            var corruptCache = await BypassRuleRefresh.ForCandidateAsync(options, default, Open, Fetch, now.AddMinutes(2));
            Check(corruptCache.RulesManifestSha256 == original.RulesManifestSha256);
            var retained = await BypassRuleRefresh.ForCandidateAsync(options, default, Open,
                (_,_,_) => throw new IOException("SourceOffline"), now.AddDays(2));
            Check(retained.RulesManifestSha256 == original.RulesManifestSha256);
            var afterFailure = await BypassRuleRefresh.ForCandidateAsync(options, default, Open, Fetch, now.AddDays(2).AddMinutes(5));
            Check(calls == 7 && afterFailure.RulesManifestSha256 == original.RulesManifestSha256);
            foreach (var invalid in new[] { Array.Empty<byte>(), new byte[128], payload[..31], new byte[9*1024*1024] })
            {
                var rejected = false;
                try { BypassRuleRefresh.ValidatePayload(invalid, 128); } catch (InvalidDataException) { rejected=true; }
                Check(rejected);
            }
            var shrunk=payload[..32];
            var shrinkRejected=false;
            try { BypassRuleRefresh.ValidatePayload(shrunk, 4096); } catch (InvalidDataException) { shrinkRejected=true; }
            Check(shrinkRejected);
            var receipt=Path.Combine(options.DataRoot,"state","bypass-refresh.v1.json");
            File.Delete(receipt); Directory.CreateDirectory(receipt); // real receipt write failure
            var blocked = await BypassRuleRefresh.ForCandidateAsync(options, default, Open, Fetch, now.AddDays(3));
            Check(blocked.RulesManifestSha256 == original.RulesManifestSha256);
            Directory.Delete(receipt);
            var bundledText=File.ReadAllText(Path.Combine(rules,"manifest.json"));
            File.WriteAllText(Path.Combine(rules,"manifest.json"), bundledText.Replace("MetaCubeX/meta-rules-dat","untrusted/other"));
            var beforeCalls=calls;
            await BypassRuleRefresh.ForCandidateAsync(options, default, Open, Fetch, now.AddDays(4));
            Check(calls == beforeCalls);
            File.WriteAllText(Path.Combine(rules,"manifest.json"), bundledText);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var propagated=false;
            try { await BypassRuleRefresh.ForCandidateAsync(options, canceled.Token, Open, Fetch, now.AddDays(5)); }
            catch (OperationCanceledException) { propagated=true; }
            Check(propagated);
            Check(Open(null).RulesManifestSha256 == original.RulesManifestSha256);
            Check(!Directory.EnumerateDirectories(Path.Combine(options.PrivateRoot,"bypass"),"stage-*").Any());
            return new { status="Passed", scenarios=checks, immutableCandidates=true, sourceFailurePreservedRules=true,
                receiptFailurePreservedRules=true, productionNetworkChanged=false, secretDisplayed=false };
        }
        finally
        {
            var resolved=Path.GetFullPath(root);
            var boundary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (resolved.StartsWith(boundary,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("art-vpn-bypass-test-",StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved,true);
        }
    }
}
