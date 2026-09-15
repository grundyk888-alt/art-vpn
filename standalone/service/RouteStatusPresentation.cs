using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

// Presentation only: these helpers never select a node or write the Windows proxy.
// A successful network switch must not keep the previous route's country/cards.
internal static class RouteStatusPresentation
{
    internal static SanitizedServiceStatus BeforeStartup(SanitizedServiceStatus status) => status with
    {
        // A previous process's Healthy flag is historical, not a readiness proof
        // for the new service instance. Preserve history but clear live metrics.
        Health = "Checking", Country = "Уточняем страну…", Nodes = [],
        Quality = "Проверка после запуска", Latency = "—",
        Stability = "Канал восстанавливается после запуска службы; готовность ещё не подтверждена",
        SafeRepairAvailable = false
    };

    internal static SanitizedServiceStatus AfterCommit(SanitizedServiceStatus status, string mode,
        int previousTarget, int target, bool changed, DateTimeOffset now)
    {
        var client = ExternalProxyEndpoint.ForMode(mode) ?? ExternalProxyEndpoint.ForPort(target);
        var external = client is not null;
        var needsObservation = !external && (ExternalProxyEndpoint.ForPort(previousTarget) is not null || status.Health == "Fallback" ||
            status.Country.StartsWith("Throne", StringComparison.OrdinalIgnoreCase));
        return status with
        {
            Mode = mode == "Auto" ? "Авто" : ExternalProxyEndpoint.ForMode(mode)?.Mode ?? "ART VPN",
            Health = external ? "Fallback" : "Healthy",
            Country = external ? client!.DisplayName : needsObservation ? "Уточняем страну…" : status.Country,
            Nodes = external || needsObservation ? [] : status.Nodes,
            Quality = external ? "Внешний резерв" : needsObservation ? "Уточняем узел" : status.Quality,
            Latency = external || needsObservation ? "—" : status.Latency,
            LastSwitchAtUtc = changed ? now.ToString("o") : status.LastSwitchAtUtc,
            LastSwitchReason = ExternalProxyEndpoint.ForMode(mode) is { } manual
                ? $"{manual.DisplayName} проверен и выбран; автоматический возврат ART VPN выключен, старые сессии завершаются мягко"
                : "Выбранный маршрут проверен; обновление подписки и смена поколений не меняют адрес системного прокси",
            Stability = "OpenAI и ChatGPT проверены; авторизованный удалённый доступ проверяется в Codex отдельно"
        };
    }

    internal static bool CanApplySelector(long beforeRevision, long afterRevision, int targetPort,
        bool allowsObservation, ActiveGenerationState? active, LiveSelectorSnapshot live) =>
        allowsObservation && beforeRevision == afterRevision && targetPort is 22086 or 22087 or 22088 &&
        active?.ProxyPort == targetPort && active.GenerationId == live.GenerationId;

    // A corrected country label is not a network switch and must not reset connection history.
    internal static bool SelectorNodeChanged(SanitizedServiceStatus status, LiveSelectorSnapshot live) =>
        !string.Equals(status.Nodes.FirstOrDefault()?.Label, live.CurrentTag, StringComparison.Ordinal);

    internal static int VerifyContract()
    {
        var before = SanitizedServiceStatus.Initial("fixture") with
        {
            Mode = "Throne", Health = "Fallback", Country = "Throne • страну показывает внешний клиент",
            Quality = "Старое качество", Latency = "42 мс",
            Nodes = [new("● ОСНОВНОЙ", "Старая страна", "old-tag", "старые данные")],
            LastSwitchAtUtc = "2026-09-01T00:00:00+00:00"
        };
        var now = DateTimeOffset.Parse("2026-09-08T11:00:00+00:00");
        var startup = BeforeStartup(before with { Health = "Healthy" });
        if (startup.Health != "Checking" || startup.Nodes.Length != 0 || startup.Country == before.Country ||
            startup.LastSwitchAtUtc != before.LastSwitchAtUtc)
            throw new InvalidOperationException("RouteStatusStartupReusedPreviousReadiness");
        var restored = AfterCommit(before, "Auto", 2080, 22086, true, now);
        if (restored.Mode != "Авто" || restored.Health != "Healthy" ||
            restored.Country != "Уточняем страну…" || restored.Nodes.Length != 0 || restored.Latency != "—")
            throw new InvalidOperationException("RouteStatusRetainedExternalMetadata");
        var fallback = AfterCommit(restored, "Auto", 22086, 2080, true, now);
        if (fallback.Mode != "Авто" || fallback.Health != "Fallback" || fallback.Country != "Throne" ||
            fallback.Nodes.Length != 0) throw new InvalidOperationException("RouteStatusFalseNativeFallback");
        var native = restored with { Country = "Германия", Health = "Healthy", Nodes = before.Nodes };
        var idempotent = AfterCommit(native, "ArtVpn", 22086, 22086, false, now.AddMinutes(1));
        if (idempotent.Country != native.Country || idempotent.Nodes != native.Nodes ||
            idempotent.LastSwitchAtUtc != native.LastSwitchAtUtc)
            throw new InvalidOperationException("RouteStatusIdempotentSelectionChangedHistory");
        var manual = AfterCommit(native, "Throne", 22086, 2080, true, now);
        if (manual.Mode != "Throne" || manual.Country != "Throne" || manual.Latency != "—" || manual.Nodes.Length != 0)
            throw new InvalidOperationException("RouteStatusExternalShowsNativeNodes");
        var active = new ActiveGenerationState(1, "art-vpn-active-generation", "fixture-generation", "A",
            22086, 22096, "fixture-config", "fixture-tag", "Германия", now.ToString("o"), 7, false);
        var live = new LiveSelectorSnapshot("fixture-tag", "Германия", "Проверен", "50 мс", [],
            now.ToString("o"), active.GenerationId);
        if (!CanApplySelector(7, 7, 22086, true, active, live) ||
            CanApplySelector(7, 8, 22086, true, active, live) ||
            CanApplySelector(7, 7, 2080, true, active, live) ||
            CanApplySelector(7, 7, 22087, true, active, live) ||
            CanApplySelector(7, 7, 22086, false, active, live) ||
            CanApplySelector(7, 7, 22086, true, active with { GenerationId = "other" }, live) ||
            CanApplySelector(7, 7, 22086, true, null, live))
            throw new InvalidOperationException("RouteStatusAcceptedStaleSelector");
        var relabelled = live with { CurrentCountry = "Италия" };
        var sameNode = before with { Country = "Другая", Nodes = [new("● АКТИВНЫЙ", "Другая", live.CurrentTag, "проверен")] };
        if (SelectorNodeChanged(sameNode, relabelled) || !SelectorNodeChanged(sameNode, relabelled with { CurrentTag = "other-tag" }))
            throw new InvalidOperationException("RouteStatusCountryCorrectionChangedHistory");
        return 14;
    }
}
