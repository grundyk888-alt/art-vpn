using System.Text.Json;

namespace ArtSport.ArtVpn.Ui;

internal static class RouteHealthPresentationTests
{
    internal static int Verify()
    {
        var now = new DateTimeOffset(2026, 9, 8, 13, 0, 0, TimeSpan.Zero);
        var state = new QaController().GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
        var actual = WindowsRouteStatus.Describe(1, "127.0.0.1:22080", "");
        var checks = 0;
        string Mode(string name, string endpoint, DateTimeOffset at, string phase = "Committed") => JsonSerializer.Serialize(new
        { schema = 1, kind = "art-vpn-route-mode", mode = name, phase, expected = new { proxyEnable = 1, proxyServer = endpoint }, atUtc = at });
        string Front(DateTimeOffset at, int revision = 1, int targetPort = 22086) => JsonSerializer.Serialize(new
        { schema = 1, kind = "art-vpn-front-target", targetPort, revision, switchedAtUtc = at });
        string Health(bool ready, DateTimeOffset at, string endpoint = "127.0.0.1:22080") => JsonSerializer.Serialize(new
        { schema = 1, kind = "art-vpn-route-health", proxy = endpoint, ready, atUtc = at });
        string Diagnosis(string code, DateTimeOffset at, string endpoint = "127.0.0.1:22080", int revision = 1) => JsonSerializer.Serialize(new
        { schema = 1, kind = "art-vpn-connection-diagnosis", proxy = endpoint, frontRevision = revision, atUtc = at, code });
        Dictionary<string, string> Baseline() => new()
        {
            ["route-mode.v1.json"] = Mode("Auto", "127.0.0.1:22080", now.AddMinutes(-1)),
            ["front-target.v1.json"] = Front(now.AddSeconds(-50)),
            ["route-health-22080.v1.json"] = Health(true, now.AddSeconds(-5))
        };
        void Check(bool passed) { if (!passed) throw new InvalidOperationException("RouteHealthUiInvariantFailed:" + checks); checks++; }
        var files = Baseline();
        VpnViewState Apply(Func<string, string>? reader = null) => RouteHealthPresentation.Apply(state, actual, reader ?? (name => files[name]), now);
        Check(Apply().Health == VpnHealth.Healthy);
        Check(Apply().ActiveChannelVerified && Apply().ActiveChannel == "ART VPN");
        foreach (var backend in new[] { 22086, 22087, 22088 })
        {
            files["front-target.v1.json"] = Front(now.AddSeconds(-50), targetPort: backend);
            Check(Apply().ActiveChannel == "ART VPN" && Apply().ConfirmedRouteMode == "Auto");
        }
        files["front-target.v1.json"] = Front(now.AddSeconds(-50), targetPort: 2080);
        var external = Apply();
        Check(external.ActiveChannelVerified && external.ActiveChannel == "Throne" && external.ConfirmedRouteMode == "Auto");
        Check(external.Country == "Страна в Throne" && external.Nodes.All(node => node.Quality == "сейчас не используется"));
        files["front-target.v1.json"] = Front(now.AddSeconds(-50), targetPort: 10809);
        var happFallback = Apply();
        Check(happFallback.ActiveChannelVerified && happFallback.ActiveChannel == "HAPP" && happFallback.ConfirmedRouteMode == "Auto");
        Check(happFallback.Country == "Страна в HAPP" && happFallback.SelectedAt is null &&
            happFallback.Nodes.All(node => node.Quality == "сейчас не используется"));
        files["front-target.v1.json"] = Front(now.AddSeconds(-50), targetPort: 29999);
        Check(!Apply().ActiveChannelVerified && Apply().Health == VpnHealth.Checking);
        files = Baseline();
        files["route-health-22080.v1.json"] = Health(false, now.AddSeconds(-5));
        Check(Apply().Health == VpnHealth.Unavailable);
        foreach (var code in new[] { "VpnUnavailableInternetWorks", "AllTestedVpnUnavailable", "NetworkUnavailable",
            "DirectInternetUnconfirmed", "SubscriptionExpired", "SubscriptionQuotaExceeded", "SubscriptionAccessRejected", "ProviderClockMismatch" })
        {
            files["connection-diagnosis.v1.json"] = Diagnosis(code, now.AddSeconds(-5));
            Check(Apply().Headline == ArtSport.ArtVpn.Common.ConnectionProblemText.Describe(code)!.Headline);
        }
        foreach (var rejected in new[]
        {
            Diagnosis("SubscriptionExpired", now.AddMinutes(-3)),
            Diagnosis("SubscriptionExpired", now.AddSeconds(-51)),
            Diagnosis("SubscriptionExpired", now.AddSeconds(1)),
            Diagnosis("SubscriptionExpired", now.AddSeconds(-5), "127.0.0.1:2080"),
            Diagnosis("SubscriptionExpired", now.AddSeconds(-5), revision: 2),
            Diagnosis("https://private.invalid/secret", now.AddSeconds(-5)), "{}", "not-json", new string('x', 32769)
        })
        {
            files["connection-diagnosis.v1.json"] = rejected;
            Check(Apply().Headline == "Выбранный канал не прошёл проверку связи");
        }
        files["connection-diagnosis.v1.json"] = Diagnosis("SubscriptionExpired", now.AddSeconds(-5));
        files["route-health-22080.v1.json"] = Health(true, now.AddSeconds(-3));
        Check(Apply().Health == VpnHealth.Healthy && !Apply().Headline.Contains("истёк"));
        var freshHealth = files["route-health-22080.v1.json"];
        files["route-health-22080.v1.json"] = freshHealth[..^1] + ",\"frontRevision\":2}";
        Check(!Apply().ActiveChannelVerified && Apply().Health == VpnHealth.Checking);
        files["route-health-22080.v1.json"] = freshHealth;
        state = state with { Health = VpnHealth.Unavailable };
        Check(Apply().Health == VpnHealth.Healthy && Apply().Headline == "Выбранный канал снова доступен");
        state = state with { Health = VpnHealth.Healthy };
        foreach (var at in new[] { now.AddMinutes(-3), now.AddMinutes(-2), now.AddSeconds(10) })
        {
            files["route-health-22080.v1.json"] = Health(true, at);
            Check(Apply().Health == VpnHealth.Checking);
        }
        files = Baseline(); files["front-target.v1.json"] = Front(now, 2);
        Check(Apply().Health == VpnHealth.Checking);
        files = Baseline(); files["route-mode.v1.json"] = Mode("Auto", "127.0.0.1:22080", now);
        Check(Apply().Health == VpnHealth.Checking);
        files = Baseline(); files["route-health-22080.v1.json"] = Health(true, now, "127.0.0.1:2080");
        Check(Apply().Health == VpnHealth.Checking);
        foreach (var invalid in new[] { "{}", "not-json", new string('x', 32_769) })
        {
            files = Baseline(); files["route-health-22080.v1.json"] = invalid;
            Check(Apply().Health == VpnHealth.Checking);
        }
        files = Baseline(); files.Remove("route-health-22080.v1.json");
        Check(Apply().Health == VpnHealth.Checking);
        files = Baseline(); var frontReads = 0;
        Check(Apply(name => name == "front-target.v1.json" && ++frontReads > 1 ? Front(now, 2) : files[name]).Health == VpnHealth.Checking);
        files = Baseline(); files["route-mode.v1.json"] = Mode("Auto", "127.0.0.1:22080", now.AddSeconds(-10), "Prepared");
        Check(Apply().Health == VpnHealth.Checking);
        state = state with { Health = VpnHealth.Fallback, Mode = "Throne", ConfirmedRouteMode = "Throne" };
        actual = WindowsRouteStatus.Describe(1, "127.0.0.1:2080", "");
        files = new()
        {
            ["route-mode.v1.json"] = Mode("Throne", "127.0.0.1:2080", now.AddSeconds(-10)),
            ["route-health-2080.v1.json"] = Health(true, now, "127.0.0.1:2080")
        };
        Check(Apply().Health == VpnHealth.Fallback); // No unrelated ART slot file required.
        Check(Apply().ActiveChannelVerified && Apply().ActiveChannel == "Throne");
        state = state with { Mode = "Auto", ConfirmedRouteMode = "" };
        Check(!Apply().ActiveChannelVerified); // Manual receipt must not authorize cached Auto.
        files["route-mode.v1.json"] = Mode("Auto", "127.0.0.1:2080", now.AddSeconds(-10));
        Check(Apply().ActiveChannelVerified && Apply().ConfirmedRouteMode == "Auto" && Apply().ActiveChannel == "Throne");
        files["route-health-2080.v1.json"] = Health(false, now, "127.0.0.1:2080");
        Check(Apply().Health == VpnHealth.Unavailable);
        Check(!Apply().ActiveChannelVerified);
        state = state with { Health = VpnHealth.Fallback, Mode = "Happ", ConfirmedRouteMode = "Happ" };
        actual = WindowsRouteStatus.Describe(1, "127.0.0.1:10809", "");
        files = new()
        {
            ["route-mode.v1.json"] = Mode("Happ", "127.0.0.1:10809", now.AddSeconds(-10)),
            ["route-health-10809.v1.json"] = Health(true, now, "127.0.0.1:10809")
        };
        Check(Apply().ActiveChannelVerified && Apply().ActiveChannel == "HAPP" && Apply().Health == VpnHealth.Fallback);
        files["route-health-10809.v1.json"] = Health(true, now, "127.0.0.1:2080");
        Check(!Apply().ActiveChannelVerified); // Throne's proof cannot certify HAPP.
        files["route-health-10809.v1.json"] = Health(true, now.AddMinutes(-5), "127.0.0.1:10809");
        Check(!Apply().ActiveChannelVerified);
        state = state with { Health = VpnHealth.Stopped };
        Check(Apply().Health == VpnHealth.Stopped);
        return checks;
    }
}
