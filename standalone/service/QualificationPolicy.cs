using System.Text.Json.Nodes;

namespace ArtSport.ArtVpn.Service;

internal sealed record NodeProbeEvidence(
    string Tag,
    string ProbeIdentity,
    string Country,
    string ServerKey,
    int DestinationPort,
    bool Clean,
    bool TlsIntegrityFailure,
    int RoundsRequested,
    int RoundsCompleted,
    double OpenAiPct,
    double ChatGptPct,
    double TelegramPct,
    double IntegrityPct,
    double RemoteConnectPct,
    bool EgressStable,
    int EgressIpChanges,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double JitterMs,
    double ThroughputFloorMbps,
    double ScoreMs,
    string ObservedCountry,
    string FailureClass,
    double GoogleAvailabilityPct = -1);

internal sealed record QualifiedPool(
    string PrimaryTag,
    string[] PoolTags,
    string[] PreferredTags,
    NodeProbeEvidence[] Pool,
    int EligibleNodes);

internal static class QualificationPolicy
{
    public static QualifiedPool Select(
        IReadOnlyList<ProviderNodeView> manifest,
        IReadOnlyList<NodeProbeEvidence> evidence,
        string previousPrimaryTag = "",
        int requiredRounds = 6,
        int minimum = 1,
        int maximum = 12)
    {
        if (manifest.Count != evidence.Count || manifest.Count < minimum || maximum < minimum)
            throw new InvalidDataException("QualificationCoverageRejected");
        var byTag = manifest.ToDictionary(node => node.Tag, StringComparer.Ordinal);
        if (byTag.Count != manifest.Count) throw new InvalidDataException("QualificationManifestDuplicate");
        var requiredAvailabilityPct = requiredRounds >= 6 ? 83.0 : 100.0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var eligible = new List<NodeProbeEvidence>();
        foreach (var result in evidence)
        {
            if (!byTag.TryGetValue(result.Tag, out var source) || !seen.Add(result.Tag) ||
                result.ProbeIdentity != source.ProbeIdentity || result.Country != source.Country ||
                result.ServerKey != source.ServerKey || result.DestinationPort != source.DestinationPort)
                throw new InvalidDataException("QualificationIdentityRejected");
            if (result.FailureClass.Contains("://", StringComparison.Ordinal) || result.FailureClass.Length > 64)
                throw new InvalidDataException("QualificationEvidenceRejected");
            if (result.Clean && !result.TlsIntegrityFailure && result.RoundsRequested == requiredRounds &&
                result.RoundsCompleted >= requiredRounds && result.OpenAiPct >= requiredAvailabilityPct &&
                result.ChatGptPct >= requiredAvailabilityPct && result.TelegramPct >= requiredAvailabilityPct &&
                result.IntegrityPct >= requiredAvailabilityPct &&
                result.EgressStable && result.EgressIpChanges == 0 && result.ThroughputFloorMbps >= 1.0)
                eligible.Add(result);
        }
        if (seen.Count != manifest.Count) throw new InvalidDataException("QualificationCoverageRejected");
        // Prefer full-service nodes only during qualification; do not cut a live OpenAI session for Google.
        var ranked = eligible.OrderBy(item => GoogleReachability.Rank(item.GoogleAvailabilityPct))
            .ThenBy(item => item.ScoreMs).ThenByDescending(item => item.ThroughputFloorMbps)
            .ThenBy(item => item.Tag, StringComparer.Ordinal).ToList();
        if (ranked.Count == 0) throw new InvalidDataException("QualifiedPoolEmpty");
        if (ranked.Count < minimum) throw new InvalidDataException("QualifiedPoolInsufficient");
        var primary = ranked[0];
        var incumbent = ranked.SingleOrDefault(item => item.Tag == previousPrimaryTag);
        if (incumbent is not null && GoogleReachability.Rank(incumbent.GoogleAvailabilityPct) <= GoogleReachability.Rank(primary.GoogleAvailabilityPct)
            && incumbent.ScoreMs <= primary.ScoreMs / 0.75 + 25)
            primary = incumbent;

        var selected = new List<NodeProbeEvidence> { primary };
        var servers = new HashSet<string>(StringComparer.Ordinal) { primary.ServerKey };
        var countries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [primary.Country] = 1 };
        foreach (var candidate in ranked)
        {
            if (selected.Count >= maximum) break;
            if (candidate.Tag == primary.Tag || servers.Contains(candidate.ServerKey) ||
                countries.GetValueOrDefault(candidate.Country) >= 3) continue;
            selected.Add(candidate);
            servers.Add(candidate.ServerKey);
            countries[candidate.Country] = countries.GetValueOrDefault(candidate.Country) + 1;
        }
        // Different destination ports are not an availability requirement:
        // an otherwise valid provider may use 443 for every country. Prefer
        // independent servers, but one strictly qualified node is still usable.
        if (selected.Count < minimum || servers.Count < minimum)
            throw new InvalidDataException("QualifiedPoolDiversityRejected");
        var tags = selected.Select(item => item.Tag).ToArray();
        return new QualifiedPool(tags[0], tags, tags.Take(Math.Min(4, tags.Length)).ToArray(), selected.ToArray(), eligible.Count);
    }

    public static ProviderNodeView[] Shortlist(
        IReadOnlyList<ProviderNodeView> manifest,
        IReadOnlyList<NodeProbeEvidence> prefilter,
        IReadOnlyCollection<string>? previousPool = null,
        int maximum = 12,
        int minimum = 1)
    {
        if (manifest.Count != prefilter.Count) throw new InvalidDataException("PrefilterCoverageRejected");
        var byTag = manifest.ToDictionary(item => item.Tag, StringComparer.Ordinal);
        var clean = prefilter.Where(item => item.Clean && !item.TlsIntegrityFailure && item.RoundsCompleted >= 1 &&
                                             item.OpenAiPct == 100 && item.ChatGptPct == 100 && item.TelegramPct == 100 &&
                                              item.IntegrityPct == 100 && item.EgressStable)
            .OrderBy(item => item.ScoreMs).ThenBy(item => item.Tag, StringComparer.Ordinal).ToList();
        if (clean.Count == 0) throw new InvalidDataException("PrefilterEmpty");
        if (clean.Count < minimum) throw new InvalidDataException("PrefilterInsufficient");
        var previous = new HashSet<string>(previousPool ?? [], StringComparer.Ordinal);
        clean = clean.OrderByDescending(item => previous.Contains(item.Tag)).ThenBy(item => item.ScoreMs).ToList();
        var selected = new List<ProviderNodeView>();
        var serverCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var countryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in clean)
        {
            if (selected.Count >= maximum) break;
            if (!byTag.TryGetValue(item.Tag, out var source) || source.ProbeIdentity != item.ProbeIdentity ||
                serverCounts.GetValueOrDefault(source.ServerKey) >= 2 ||
                countryCounts.GetValueOrDefault(source.Country) >= 4) continue;
            selected.Add(source);
            serverCounts[source.ServerKey] = serverCounts.GetValueOrDefault(source.ServerKey) + 1;
            countryCounts[source.Country] = countryCounts.GetValueOrDefault(source.Country) + 1;
        }
        if (selected.Count < minimum || serverCounts.Count < minimum)
            throw new InvalidDataException("PrefilterDiversityRejected");
        return selected.ToArray();
    }
}

internal sealed record RuntimeRuleAsset(string Tag, string Path);

internal static class RuntimeConfigBuilder
{
    private static readonly HashSet<string> SupportedLeafTypes = new(StringComparer.Ordinal)
    {
        "http", "hysteria", "hysteria2", "shadowsocks", "socks", "trojan", "tuic", "vless", "vmess", "wireguard"
    };

    public static JsonObject Build(
        JsonObject privateProbeConfig,
        QualifiedPool pool,
        IReadOnlyList<RuntimeRuleAsset> rules,
        int proxyPort,
        int apiPort,
        string cachePath,
        string logPath)
    {
        if (pool.PoolTags.Length < 1 || pool.PoolTags.Distinct(StringComparer.Ordinal).Count() != pool.PoolTags.Length ||
            pool.PreferredTags.Length < 1 || pool.PreferredTags.Any(tag => !pool.PoolTags.Contains(tag, StringComparer.Ordinal)) ||
            proxyPort is < 1024 or > 65535 || apiPort is < 1024 or > 65535 || proxyPort == apiPort || rules.Count != 4)
            throw new InvalidDataException("RuntimeBuildContractRejected");
        var expectedRules = new HashSet<string>(StringComparer.Ordinal)
        {
            "art-geoip-ru", "art-geosite-category-ru", "art-geosite-ru", "art-geosite-ru-inside"
        };
        if (!expectedRules.SetEquals(rules.Select(rule => rule.Tag)) || rules.Any(rule => !Path.IsPathFullyQualified(rule.Path)))
            throw new InvalidDataException("RuntimeRulesRejected");

        var source = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (privateProbeConfig["outbounds"] is not JsonArray sourceOutbounds)
            throw new InvalidDataException("RuntimeSourceRejected");
        foreach (var node in sourceOutbounds.OfType<JsonObject>())
        {
            var tag = node["tag"]?.GetValue<string>() ?? "";
            var type = node["type"]?.GetValue<string>() ?? "";
            if (tag.Length > 0 && SupportedLeafTypes.Contains(type) && !source.TryAdd(tag, node))
                throw new InvalidDataException("RuntimeSourceDuplicate");
        }
        if (pool.PoolTags.Any(tag => !source.ContainsKey(tag)))
            throw new InvalidDataException("RuntimeQualifiedLeafMissing");

        var outbounds = new JsonArray();
        foreach (var tag in pool.PoolTags) outbounds.Add(source[tag].DeepClone());
        outbounds.Add(new JsonObject
        {
            ["type"] = "auto-selector",
            ["tag"] = "art-codex-auto",
            ["outbounds"] = new JsonArray(pool.PoolTags.Select(tag => JsonValue.Create(tag)).ToArray()),
            ["preferred_outbounds"] = new JsonArray(pool.PreferredTags.Select(tag => JsonValue.Create(tag)).ToArray()),
            ["url"] = "https://chatgpt.com/cdn-cgi/trace",
            ["body_policy"] = "chatgpt-trace",
            ["require_successful_status"] = true,
            ["status_url"] = "https://api.openai.com/v1/models",
            ["accepted_statuses"] = new JsonArray(401),
            ["status_body_policy"] = "openai-unauthorized-json",
            ["connectivity_urls"] = new JsonArray(
                "http://www.msftconnecttest.com/connecttest.txt", "http://detectportal.firefox.com/success.txt"),
            ["interval"] = "60s",
            ["bench_interval"] = "900s",
            ["watch_interval"] = "30s",
            ["watch_attempts"] = 3,
            ["active_size"] = pool.PoolTags.Length,
            ["sampling"] = Math.Max(8, pool.PoolTags.Length),
            ["timeout"] = "8s",
            ["concurrency"] = 4,
            // This policy must be present in the standalone release as well.
            // A 60-second margin with an 8-second probe timeout pinned a slow
            // but healthy incumbent forever. The accepted core requires fresh
            // paired evidence, material gain and dwell before optimization;
            // confirmed failure is handled independently of that dwell.
            ["tolerance"] = 250,
            ["min_switch_interval"] = "10m",
            ["improvement_rounds"] = 3,
            ["improvement_ratio"] = 0.25,
            ["fail_tolerance"] = 0.35,
            // Truncating to the fastest four evicts a healthy incumbent from
            // the candidate set and bypasses the selector's stickiness margin.
            ["expected"] = pool.PoolTags.Length,
            ["balance"] = false,
            ["dial_retries"] = 5,
            ["dial_failure_threshold"] = 3,
            ["dial_failure_window"] = "45s",
            ["interrupt_exist_connections"] = false
        });
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

        var ruleSet = new JsonArray();
        foreach (var rule in rules.OrderBy(item => item.Tag, StringComparer.Ordinal))
            ruleSet.Add(new JsonObject { ["type"] = "local", ["tag"] = rule.Tag, ["format"] = "binary", ["path"] = rule.Path });
        var ruleTags = new JsonArray(rules.OrderBy(item => item.Tag, StringComparer.Ordinal).Select(item => JsonValue.Create(item.Tag)).ToArray());
        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["output"] = logPath, ["timestamp"] = true },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "mixed", ["tag"] = "mixed-in", ["listen"] = "127.0.0.1", ["listen_port"] = proxyPort
            }),
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["rule_set"] = ruleSet,
                ["rules"] = new JsonArray(
                    new JsonObject
                    {
                        ["domain"] = new JsonArray("openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com"),
                        ["domain_suffix"] = new JsonArray(".openai.com", ".chatgpt.com", ".oaistatic.com", ".oaiusercontent.com"),
                        ["outbound"] = "art-codex-auto"
                    },
                    new JsonObject { ["rule_set"] = ruleTags, ["outbound"] = "direct" },
                    new JsonObject
                    {
                        ["domain_suffix"] = new JsonArray(".ru", ".su", ".xn--p1ai", ".avito.ru", ".avito.st", ".ozon.ru", ".ozonusercontent.com"),
                        ["outbound"] = "direct"
                    },
                    new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" }),
                ["final"] = "art-codex-auto"
            },
            ["experimental"] = new JsonObject
            {
                ["cache_file"] = new JsonObject { ["enabled"] = true, ["path"] = cachePath },
                ["clash_api"] = new JsonObject { ["external_controller"] = $"127.0.0.1:{apiPort}" }
            }
        };
    }
}

internal static class QualificationPolicyTests
{
    public static QualificationPolicyTestReceipt Run()
    {
        var manifest = Enumerable.Range(0, 6).Select(index => new ProviderNodeView(
            "n-" + index, "node-" + index, index % 3 == 0 ? "Нидерланды" : index % 3 == 1 ? "Германия" : "Эстония",
            "tcp", "server-" + index, index % 2 == 0 ? 443 : 8443, 24000 + index,
            new string((char)('a' + index), 32))).ToArray();
        var evidence = manifest.Select((node, index) => Good(node, 70 + index * 10)).ToArray();
        var pool = QualificationPolicy.Select(manifest, evidence, maximum: 4);
        Assert(pool.PoolTags.Length == 4 && pool.PrimaryTag == "n-0" && pool.EligibleNodes == 6);
        var incumbent = QualificationPolicy.Select(manifest, evidence, previousPrimaryTag: "n-1", maximum: 4);
        Assert(incumbent.PrimaryTag == "n-1");
        var browserEvidence = evidence.Select(item => item with { GoogleAvailabilityPct = 100 }).ToArray();
        browserEvidence[0] = browserEvidence[0] with { GoogleAvailabilityPct = 0 };
        Assert(QualificationPolicy.Select(manifest, browserEvidence, previousPrimaryTag: "n-0", maximum: 4).PrimaryTag == "n-1");
        Assert(QualificationPolicy.Select(manifest, evidence.Select(item => item with { GoogleAvailabilityPct=0 }).ToArray(), maximum:4).PoolTags.Length==4);
        Assert(GoogleReachability.Classify(429)=="VerificationRequired" && GoogleReachability.Classify(200)=="Available");
        var bad = evidence.ToArray();
        bad[0] = bad[0] with { TlsIntegrityFailure = true, Clean = false };
        Assert(QualificationPolicy.Select(manifest, bad, maximum: 4).PrimaryTag != "n-0");
        var shortlist = QualificationPolicy.Shortlist(manifest,
            evidence.Select(item => item with { RoundsRequested = 1, RoundsCompleted = 1 }).ToArray(), maximum: 5);
        Assert(shortlist.Length == 5);
        var all443 = manifest.Select(node => node with { DestinationPort = 443 }).ToArray();
        var all443Evidence = all443.Select((node, index) => Good(node, 80 + index)).ToArray();
        Assert(QualificationPolicy.Select(all443, all443Evidence).PoolTags.Length >= 3);
        Assert(QualificationPolicy.Shortlist(all443, all443Evidence.Select(item => item with {RoundsCompleted=1}).ToArray()).Length >= 3);
        var single = QualificationPolicy.Select([manifest[0]], [evidence[0]]);
        Assert(single.PoolTags.Length == 1 && single.PrimaryTag == manifest[0].Tag);
        Assert(QualificationPolicy.Shortlist([manifest[0]], [evidence[0] with {RoundsCompleted=1}]).Length == 1);
        // An empty tested pool is different from having one working node but
        // fewer reserves than the requested minimum. Do not label both "all VPNs down".
        var none = evidence.Select(item => item with { Clean = false }).ToArray();
        var one = none.ToArray(); one[0] = evidence[0];
        void Rejected(Action action, string code)
        {
            try { action(); throw new InvalidOperationException("QualificationRejectionExpected"); }
            catch (InvalidDataException ex) { Assert(ex.Message == code); }
        }
        Rejected(() => QualificationPolicy.Select(manifest, none), "QualifiedPoolEmpty");
        Rejected(() => QualificationPolicy.Shortlist(manifest, none), "PrefilterEmpty");
        Rejected(() => QualificationPolicy.Select(manifest, one, minimum: 2), "QualifiedPoolInsufficient");
        Rejected(() => QualificationPolicy.Shortlist(manifest, one, minimum: 2), "PrefilterInsufficient");

        var privateProbe = new JsonObject { ["outbounds"] = new JsonArray() };
        foreach (var node in manifest)
            ((JsonArray)privateProbe["outbounds"]!).Add(new JsonObject
            {
                ["type"] = "vless", ["tag"] = node.Tag, ["server"] = "198.51.100.1", ["server_port"] = node.DestinationPort,
                ["uuid"] = "00000000-1111-2222-3333-444444444444"
            });
        var rules = new[]
        {
            new RuntimeRuleAsset("art-geoip-ru", @"C:\rules\geoip.srs"),
            new RuntimeRuleAsset("art-geosite-category-ru", @"C:\rules\category.srs"),
            new RuntimeRuleAsset("art-geosite-ru", @"C:\rules\ru.srs"),
            new RuntimeRuleAsset("art-geosite-ru-inside", @"C:\rules\inside.srs")
        };
        var config = RuntimeConfigBuilder.Build(privateProbe, pool, rules, 22086, 22096,
            @"C:\state\cache.db", @"C:\state\runtime.log").ToJsonString();
        Assert(config.Contains("art-codex-auto", StringComparison.Ordinal) &&
               config.Contains("dial_failure_threshold", StringComparison.Ordinal) &&
               config.Contains("interrupt_exist_connections", StringComparison.Ordinal) &&
               config.IndexOf(".openai.com", StringComparison.Ordinal) < config.IndexOf(".ru", StringComparison.Ordinal) &&
               !config.Contains("mtalk.google.com", StringComparison.Ordinal));
        var broadPool = QualificationPolicy.Select(manifest, evidence, maximum: 6);
        var broadConfig = RuntimeConfigBuilder.Build(privateProbe, broadPool, rules, 22086, 22096,
            @"C:\state\cache.db", @"C:\state\runtime.log");
        var selector = broadConfig["outbounds"]!.AsArray().Single(item => item?["tag"]?.GetValue<string>() == "art-codex-auto")!;
        Assert(broadPool.PoolTags.Length > 4 && selector["expected"]!.GetValue<int>() == broadPool.PoolTags.Length);
        Assert(selector["tolerance"]!.GetValue<int>() == 250 &&
               selector["improvement_ratio"]!.GetValue<double>() == 0.25);
        Assert(selector["min_switch_interval"]!.GetValue<string>() == "10m" &&
               selector["improvement_rounds"]!.GetValue<int>() == 3);
        Assert(!selector["interrupt_exist_connections"]!.GetValue<bool>() &&
               selector["timeout"]!.GetValue<string>() == "8s" &&
               selector["interval"]!.GetValue<string>() == "60s" &&
               selector["watch_interval"]!.GetValue<string>() == "30s");
        var priority = broadConfig["route"]!["rules"]!.AsArray()[0]!;
        Assert(priority["outbound"]!.GetValue<string>() == "art-codex-auto" &&
               priority["domain"]!.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
                   .SetEquals(new[] { "openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com" }));
        // Export only non-secret settings taken from the actual generated
        // runtime config. The isolated live replay consumes this contract, not
        // a manually tuned configuration disconnected from the release path.
        var policy = new JsonObject();
        foreach (var key in new[] { "tolerance", "min_switch_interval", "improvement_rounds", "improvement_ratio",
                     "interval", "bench_interval", "watch_interval", "watch_attempts", "timeout", "concurrency",
                     "interrupt_exist_connections", "dial_failure_threshold", "dial_failure_window" })
            policy[key] = selector[key]!.DeepClone();
        return new QualificationPolicyTestReceipt("Passed", 28, true, true, true, true, false, policy);
    }

    private static NodeProbeEvidence Good(ProviderNodeView node, double score) => new(
        node.Tag, node.ProbeIdentity, node.Country, node.ServerKey, node.DestinationPort,
        true, false, 6, 6, 100, 100, 100, 100, 100, true, 0,
        score - 10, score, 5, 20, score, "NL", "Passed");

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("QualificationPolicyInvariantFailed");
    }
}

internal sealed record QualificationPolicyTestReceipt(
    string Status,
    int Scenarios,
    bool Hysteresis,
    bool TlsQuarantine,
    bool Diversity,
    bool BypassOwnedByProduct,
    bool SecretDisplayed,
    JsonObject RuntimeSelector);
