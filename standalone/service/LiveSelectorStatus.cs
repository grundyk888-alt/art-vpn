using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Service;

internal sealed record LiveSelectorSnapshot(
    string CurrentTag,
    string CurrentCountry,
    string Quality,
    string Latency,
    SanitizedNodeStatus[] Nodes,
    string ObservedAtUtc,
    string GenerationId);

/// <summary>
/// Reads the actual outbound selected by the embedded core.  The qualification
/// primary is only the initial preference; the core's auto selector can later
/// choose another already-qualified member of the same sealed pool.
/// </summary>
internal static partial class LiveSelectorStatusReader
{
    private const string SelectorName = "art-codex-auto";
    private const int MaximumResponseBytes = 16_384;
    private static readonly HttpClient Client = CreateClient();

    public static async Task<LiveSelectorSnapshot?> TryReadAsync(
        RuntimeOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var active = ActiveGenerationStore.TryRead(options);
            if (active is null) return null;
            var qualificationPath = Path.Combine(
                options.PrivateGenerationRoot, active.GenerationId, "qualification.v1.json");
            if (!File.Exists(qualificationPath)) return null;
            var qualification = JsonSerializer.Deserialize<QualificationReceipt>(
                                    File.ReadAllText(qualificationPath), JsonSettings.Strict)
                                ?? throw new InvalidDataException("LiveSelectorQualificationRejected");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await Client.GetAsync(
                    $"http://127.0.0.1:{active.ApiPort}/proxies/{SelectorName}",
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentLength is > MaximumResponseBytes)
                return null;
            var bytes = await BoundedHttpContent.ReadAsync(response.Content, MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            try
            {
                if (bytes.Length is < 2 or > MaximumResponseBytes) return null;
                var snapshot = ParseAndValidate(active, qualification, bytes, DateTimeOffset.UtcNow);
                using var health = await Client.GetAsync($"http://127.0.0.1:{active.ApiPort}/auto-selectors",
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (health.StatusCode != HttpStatusCode.OK)
                    return snapshot with { Nodes = snapshot.Nodes.Take(1).ToArray(), Quality = "состояние резерва пока не подтверждено" };
                var healthBytes = await BoundedHttpContent.ReadAsync(health.Content, 65536, timeout.Token).ConfigureAwait(false);
                try { return ApplyHealth(snapshot, qualification, healthBytes, DateTimeOffset.UtcNow); }
                finally { Array.Clear(healthBytes); }
            }
            finally { Array.Clear(bytes); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    internal static LiveSelectorSnapshot ParseForTest(
        ActiveGenerationState active,
        QualificationReceipt qualification,
        string json,
        DateTimeOffset observedAtUtc) =>
        ParseAndValidate(active, qualification, System.Text.Encoding.UTF8.GetBytes(json), observedAtUtc);

    private static LiveSelectorSnapshot ParseAndValidate(
        ActiveGenerationState active,
        QualificationReceipt qualification,
        ReadOnlySpan<byte> json,
        DateTimeOffset observedAtUtc)
    {
        if (qualification.Schema != 1 || qualification.Kind != "art-vpn-generation-qualification" ||
            qualification.Status != "Passed" || qualification.GenerationId != active.GenerationId ||
            qualification.Slot != active.Slot || qualification.ContainsProviderSecret ||
            qualification.PoolTags.Length is < 1 or > 12 ||
            qualification.PoolTags.Distinct(StringComparer.Ordinal).Count() != qualification.PoolTags.Length)
            throw new InvalidDataException("LiveSelectorQualificationRejected");

        using var document = JsonDocument.Parse(json.ToArray(), JsonSettings.Document);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var type) || type.GetString() != "Auto Selector" ||
            !root.TryGetProperty("name", out var name) || name.GetString() != SelectorName ||
            !root.TryGetProperty("now", out var nowValue) ||
            !root.TryGetProperty("all", out var allValue) || allValue.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("LiveSelectorResponseRejected");

        var current = nowValue.GetString() ?? string.Empty;
        var all = allValue.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
        if (!NodeTagPattern().IsMatch(current) || all.Length is < 1 or > 12 ||
            all.Any(tag => !NodeTagPattern().IsMatch(tag)) ||
            all.Distinct(StringComparer.Ordinal).Count() != all.Length ||
            !all.Contains(current, StringComparer.Ordinal) ||
            !all.ToHashSet(StringComparer.Ordinal).SetEquals(qualification.PoolTags))
            throw new InvalidDataException("LiveSelectorPoolRejected");

        var evidence = qualification.Evidence
            .Where(item => qualification.PoolTags.Contains(item.Tag, StringComparer.Ordinal))
            .ToDictionary(item => item.Tag, StringComparer.Ordinal);
        if (evidence.Count != qualification.PoolTags.Length || all.Any(tag => !evidence.ContainsKey(tag)))
            throw new InvalidDataException("LiveSelectorEvidenceRejected");

        var ordered = new[] { current }.Concat(all.Where(tag => tag != current)).Take(3).ToArray();
        var cards = ordered.Select((tag, index) =>
        {
            var node = evidence[tag];
            if (string.IsNullOrWhiteSpace(node.Country) || node.Country.Length > 80 || !node.Clean)
                throw new InvalidDataException("LiveSelectorEvidenceRejected");
            return new SanitizedNodeStatus(
                index == 0 ? "● АКТИВНЫЙ" : $"○ РЕЗЕРВ {index}",
                NodeCountryDisplay.Resolve(node),
                node.Tag,
                index == 0
                    ? $"при отборе: {node.LatencyP95Ms:0} мс"
                    : "проверен при отборе; ждём свежую проверку");
        }).ToArray();
        var selected = evidence[current];
        return new LiveSelectorSnapshot(
            current,
            NodeCountryDisplay.Resolve(selected),
            all.Length == 1 ? "канал проверен, но рабочего резерва пока нет" : "фактический узел встроенного автовыбора",
            $"при отборе: {selected.LatencyP95Ms:0} мс",
            cards,
            observedAtUtc.ToString("o"),
            active.GenerationId);
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static LiveSelectorSnapshot ApplyHealth(LiveSelectorSnapshot snapshot, QualificationReceipt qualification,
        byte[] bytes, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(bytes, JsonSettings.Document);
        var selectors = document.RootElement.GetProperty("auto_selectors").EnumerateArray()
            .Where(item => item.GetProperty("tag").GetString() == SelectorName).ToArray();
        if (selectors.Length != 1 || selectors[0].GetProperty("selected").GetString() != snapshot.CurrentTag)
            throw new InvalidDataException("LiveSelectorChangedDuringRead");
        var members = selectors[0].GetProperty("members").EnumerateArray().ToArray();
        if (members.Length != qualification.PoolTags.Length ||
            !members.Select(item => item.GetProperty("tag").GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(qualification.PoolTags))
            throw new InvalidDataException("LiveSelectorHealthPoolRejected");
        var evidence = qualification.Evidence.ToDictionary(item => item.Tag, StringComparer.Ordinal);
        bool Fresh(JsonElement item) => item.GetProperty("qualified").GetBoolean() &&
            item.GetProperty("state").GetString() is "ok" or "degraded" &&
            item.GetProperty("average_ms").GetInt32() >= 0 &&
            item.GetProperty("cooldown_until").GetDateTimeOffset() <= now &&
            item.GetProperty("last_ok").GetDateTimeOffset() <= now.AddSeconds(10) &&
            now - item.GetProperty("last_ok").GetDateTimeOffset() <= TimeSpan.FromMinutes(3);
        var selected = members.Single(item => item.GetProperty("tag").GetString() == snapshot.CurrentTag);
        var selectedFresh = Fresh(selected);
        var latency = selectedFresh ? $"{selected.GetProperty("average_ms").GetInt32()} мс" : "нет свежей проверки";
        var reserves = members.Where(item => item.GetProperty("tag").GetString() != snapshot.CurrentTag && Fresh(item))
            .OrderBy(item => item.GetProperty("rank").GetInt32()).Take(2).ToArray();
        var cards = new List<SanitizedNodeStatus> { snapshot.Nodes[0] with
        {
            Quality = selectedFresh ? $"доступен сейчас • {latency}" : "выбран ядром; доступность требует проверки"
        } };
        foreach (var member in reserves)
        {
            var tag = member.GetProperty("tag").GetString()!;
            cards.Add(new SanitizedNodeStatus($"○ РЕЗЕРВ {cards.Count}", NodeCountryDisplay.Resolve(evidence[tag]), tag,
                $"доступен сейчас • {member.GetProperty("average_ms").GetInt32()} мс"));
        }
        return snapshot with { Nodes = cards.ToArray(), Latency = latency, Quality = !selectedFresh
            ? "активный узел требует проверки; не подтверждаем работу по старым данным"
            : reserves.Length == 0 ? "текущий узел проверен; готовый резерв пока не подтверждён"
            : "текущий узел и резерв проверены автовыбором" };
    }

    [GeneratedRegex("^n-[a-f0-9]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeTagPattern();
}

internal static class LiveSelectorStatusTests
{
    public static LiveSelectorStatusTestReceipt Run()
    {
        var generation = "20260901T190704Z-2d6fbe9600e0";
        var active = new ActiveGenerationState(1, "art-vpn-active-generation", generation,
            "A", 22086, 22096, @"C:\fixture\config.json", "n-1111111111111111", "Финляндия",
            "2026-09-01T19:07:04.0000000+00:00", 4, false);
        var evidence = new[]
        {
            Evidence("n-1111111111111111", "Финляндия", 310, 7.8),
            Evidence("n-2222222222222222", "Испания", 145, 11.2),
            Evidence("n-3333333333333333", "Германия", 190, 9.4)
        };
        var qualification = new QualificationReceipt(1, "art-vpn-generation-qualification", generation,
            "Passed", "A", evidence[0].Tag, evidence.Select(item => item.Tag).ToArray(), evidence,
            new string('A', 64), new string('B', 64), "2026-09-01T19:07:04.0000000+00:00", false, false);
        const string response =
            "{\"type\":\"Auto Selector\",\"name\":\"art-codex-auto\",\"udp\":true,\"history\":[]," +
            "\"now\":\"n-2222222222222222\",\"all\":[\"n-1111111111111111\",\"n-2222222222222222\",\"n-3333333333333333\"]}";
        var snapshot = LiveSelectorStatusReader.ParseForTest(
            active, qualification, response, DateTimeOffset.Parse("2026-09-01T20:00:00+00:00"));
        Assert(snapshot.CurrentCountry == "Испания" && snapshot.CurrentTag == "n-2222222222222222");
        Assert(snapshot.Nodes.Length == 3 && snapshot.Nodes[0].Role == "● АКТИВНЫЙ" &&
               snapshot.Nodes[0].Country == "Испания" && snapshot.Nodes[1].Country == "Финляндия");
        Assert(snapshot.Latency == "при отборе: 145 мс" && snapshot.Quality.Contains("фактический", StringComparison.Ordinal));
        Reject(active, qualification, response.Replace("n-2222222222222222\",\"all", "n-ffffffffffffffff\",\"all"));
        Reject(active, qualification, response.Replace("art-codex-auto", "unexpected-selector"));
        Reject(active, qualification with { ContainsProviderSecret = true }, response);
        var checkedAt = DateTimeOffset.Parse("2026-09-01T20:00:00+00:00");
        byte[] RuntimeHealth(bool quarantine) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            auto_selectors = new[] { new { tag = "art-codex-auto", selected = snapshot.CurrentTag,
                members = evidence.Select((item, index) => new {
                    tag = item.Tag, rank = index, qualified = true, state = "ok", average_ms = 100,
                    cooldown_until = quarantine && index == 0 ? checkedAt.AddHours(12) : checkedAt.AddMinutes(-1),
                    last_ok = index == 2 ? checkedAt.AddHours(-1) : checkedAt
                }).ToArray() } }
        });
        var liveReserves = LiveSelectorStatusReader.ApplyHealth(snapshot, qualification, RuntimeHealth(false), checkedAt);
        Assert(liveReserves.Nodes.Length == 2 && liveReserves.Nodes[1].Country == "Финляндия");
        Assert(liveReserves.Latency == "100 мс" && liveReserves.Nodes[0].Quality.Contains("100 мс"));
        var quarantined = LiveSelectorStatusReader.ApplyHealth(snapshot, qualification, RuntimeHealth(true), checkedAt);
        Assert(quarantined.Nodes.Length == 1 && quarantined.Nodes[0].Country == "Испания");
        var stale = LiveSelectorStatusReader.ApplyHealth(snapshot, qualification, RuntimeHealth(false), checkedAt.AddMinutes(4));
        Assert(stale.Latency == "нет свежей проверки" && stale.Nodes.Length == 1);
        Assert(stale.Quality.Contains("требует проверки") && !stale.Quality.Contains("работает"));
        Assert(!liveReserves.Nodes[0].Quality.Contains("Мбит/с"));
        // The reported incident: provider label unknown, but this exact node has a qualified IT exit.
        var measuredEvidence = evidence.Select((node, index) => node with
        {
            Country = index == 1 ? "Другая" : node.Country,
            ObservedCountry = index == 1 ? "IT" : index == 0 ? "NL" : "DE"
        }).ToArray();
        var measuredQualification = qualification with { Evidence = measuredEvidence };
        var measured = LiveSelectorStatusReader.ParseForTest(active, measuredQualification, response, checkedAt);
        Assert(measured.CurrentCountry == "Италия" && measured.Nodes[0].Country == "Италия");
        var measuredHealth = LiveSelectorStatusReader.ApplyHealth(measured, measuredQualification, RuntimeHealth(false), checkedAt);
        Assert(measuredHealth.Nodes[1].Country == "Нидерланды");
        var switched = LiveSelectorStatusReader.ParseForTest(active, measuredQualification,
            response.Replace("\"now\":\"n-2222222222222222\"", "\"now\":\"n-3333333333333333\""), checkedAt);
        Assert(switched.CurrentCountry == "Германия" && switched.Nodes[0].Label == evidence[2].Tag);
        Assert(NodeCountryDisplay.Resolve(measuredEvidence[1] with { EgressStable = false }) == "Страна не определена");
        Assert(NodeCountryDisplay.Resolve(measuredEvidence[1] with { TlsIntegrityFailure = true }) == "Страна не определена");
        Assert(NodeCountryDisplay.Resolve(measuredEvidence[1] with { Clean = false }) == "Страна не определена");
        Assert(NodeCountryDisplay.Resolve(measuredEvidence[1] with { FailureClass = "Rejected" }) == "Страна не определена");
        Assert(NodeCountryDisplay.Resolve(measuredEvidence[1] with { ObservedCountry = "??" }) == "Страна не определена");
        Assert(NodeCountryDisplay.Resolve(measuredEvidence[0] with { ObservedCountry = "" }) == "Финляндия");
        Assert(NodeCountryDisplay.Resolve(measuredEvidence[1] with { ObservedCountry = "it" }) == "Италия");
        Assert(qualification.Evidence[1].Country == "Испания" && measuredQualification.Evidence[1].Country == "Другая");
        return new LiveSelectorStatusTestReceipt("Passed", 25, true, true, true, false);
    }

    private static NodeProbeEvidence Evidence(string tag, string country, double latency, double throughput) =>
        new(tag, new string('a', 32), country, "server", 443, true, false, 6, 6,
            100, 100, 100, 100, 100, true, 0, latency, latency, 5, throughput, latency, "", "Passed");

    private static void Reject(ActiveGenerationState active, QualificationReceipt qualification, string json)
    {
        try
        {
            _ = LiveSelectorStatusReader.ParseForTest(active, qualification, json, DateTimeOffset.UtcNow);
            throw new InvalidOperationException("LiveSelectorInvalidFixtureAccepted");
        }
        catch (InvalidDataException) { }
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("LiveSelectorStatusInvariantFailed");
    }
}

internal sealed record LiveSelectorStatusTestReceipt(
    string Status,
    int Scenarios,
    bool CurrentCountryTracked,
    bool PoolCardsReordered,
    bool InvalidPayloadRejected,
    bool SecretDisplayed);
