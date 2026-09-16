using System.Drawing.Imaging;
using System.Reflection;
using System.Security;
using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Ui;

internal sealed class QaController : IArtVpnUiController
{
    public readonly List<string> Calls = [];
    public int ExternalSideEffects = 0; // Fixture controller; not proof of live network behaviour.
    public WindowsRouteStatus Route = WindowsRouteStatus.Describe(1, "127.0.0.1:22080", "");
    public UiOperationResult? NextResult;
    public Exception? NextException;
    public TaskCompletionSource<UiOperationResult>? NextPending;
    public string Mode = "Авто";
    public string? ActiveChannelOverride;
    public string? ProblemCode;

    public Task<VpnViewState> GetStatusAsync(CancellationToken cancellationToken)
    {
        WindowsRouteStatus.VerifyContract();
        Calls.Add("GetStatus");
        var state = Route.Apply(new VpnViewState(
            VpnHealth.Healthy,
            "VPN работает стабильно",
            "Канал выбран по стабильности ChatGPT, а не только по скорости.",
            Mode,
            "Нидерланды",
            DateTimeOffset.Now.AddHours(-5).AddMinutes(-17),
            "Отличная",
            "84 мс",
            "99,98 %",
            "31.08.2026 08:15",
            "подтверждённый отказ прежнего канала",
            [
                new("● ОСНОВНОЙ", "Нидерланды", "узел NL-04", "стабилен 5 ч"),
                new("○ РЕЗЕРВ 1", "Германия", "узел DE-02", "проверен"),
                new("○ РЕЗЕРВ 2", "Эстония", "узел EE-07", "проверен")
            ],
            "актуальна • 12 узлов",
            "31.08.2026 09:00 • новых: 2",
            "Avito, Ozon и российские сайты идут напрямую",
            "проверено • обновлений нет",
            "0.1.0",
            true,
            false,
            "Подхват Codex: отдельной привязки к старому VPN нет; запуск следует системному прокси Windows.",
            WindowsRouteStatus.Describe(1, "127.0.0.1:22080", "").Message, true));
        if (ProblemCode is { } code && ConnectionProblemText.Describe(code) is { } problem)
            return Task.FromResult(state with { Health = VpnHealth.Unavailable, Headline = problem.Headline,
                Explanation = problem.Explanation, Quality = "Нужна проверка", ActiveChannelVerified = false });
        var selectedChannel = ActiveChannelOverride ?? ExternalProxyEndpoint.ForMode(Route.Endpoint)?.DisplayName ?? "ART VPN";
        var externalVerified = selectedChannel is "Throne" or "HAPP" && state.ConfirmedRouteMode.Length > 0;
        return Task.FromResult(state with
        {
            ActiveChannel = selectedChannel,
            ActiveChannelVerified = state.ConfirmedRouteMode.Length > 0,
            SelectedAt = ActiveChannelOverride is "Throne" or "HAPP" || Route.Endpoint != "ArtVpn" ? null : state.SelectedAt,
            Health = externalVerified ? VpnHealth.Fallback : state.Health,
            Headline = externalVerified ? $"Работает внешний VPN — {selectedChannel}" : state.Headline,
            Country = externalVerified ? $"Страна в {selectedChannel}" : state.Country,
            Quality = externalVerified ? "Внешний канал проверен" : state.Quality,
            Latency = externalVerified ? "—" : state.Latency,
            Stability = externalVerified ? "—" : state.Stability,
            Nodes = externalVerified ? state.Nodes.Select(node => node with { Quality = "сейчас не используется" }).ToArray() : state.Nodes
        });
    }

    public Task<UiOperationResult> CheckNowAsync(CancellationToken cancellationToken) => Result("CheckNow", "Проверка завершена.");
    public Task<UiOperationResult> CheckUpdatesAsync(CancellationToken cancellationToken) => Result("CheckUpdates", "Обновлений нет.");
    public Task<UiOperationResult> RefreshSubscriptionAsync(CancellationToken cancellationToken) => Result("RefreshSubscription", "Новый список проверяется отдельно.");
    public Task<UiOperationResult> CollectDiagnosticsAsync(CancellationToken cancellationToken) => Result("CollectDiagnostics", "Отчёт собран без секретов.");
    public Task<UiOperationResult> SubmitDiagnosticsAsync(CancellationToken cancellationToken) => Result("SubmitDiagnostics", "Отчёт принят.");
    public Task<UiOperationResult> SetAutoAsync(CancellationToken cancellationToken) => Result("SetAuto", "Авто включено.");
    public async Task<UiOperationResult> SetModeAsync(string mode, CancellationToken cancellationToken)
    {
        var result = await Result("SetMode:" + mode, "Выбранный маршрут проверен.");
        if (result.Success)
        {
            Mode = mode == "Auto" ? "Авто" : mode == "ArtVpn" ? "ART VPN" : mode;
            Route = WindowsRouteStatus.Describe(1, $"127.0.0.1:{ExternalProxyEndpoint.ForMode(mode)?.Port ?? 22080}", "");
        }
        return result;
    }
    public Task<UiOperationResult> EnableSystemProxyAsync(CancellationToken cancellationToken) =>
        Result("EnableSystemProxy", "ART VPN подключён.");
    public Task<UiOperationResult> RestoreSystemProxyAsync(CancellationToken cancellationToken) =>
        Result("RestoreSystemProxy", "Прежнее подключение восстановлено.");
    public Task<UiOperationResult> AcquireClientAsync(string client, CancellationToken cancellationToken) =>
        Result("AcquireClient:" + client, "Официальный клиент помещён в карантин.");

    public Task<UiOperationResult> ConfigureProviderAsync(SecureString first, SecureString confirmation,
        CancellationToken cancellationToken)
    {
        if (first.Length < 12 || first.Length != confirmation.Length)
            return Task.FromResult(new UiOperationResult(false, "Тестовая ссылка не прошла проверку."));
        return Result("ConfigureProvider", "ART VPN подключён. Режим «Авто» включён.");
    }

    private Task<UiOperationResult> Result(string call, string message)
    {
        Calls.Add(call);
        if (NextPending is { } pending) { NextPending = null; return pending.Task; }
        if (NextException is { } error) { NextException = null; return Task.FromException<UiOperationResult>(error); }
        if (NextResult is { } result) { NextResult = null; return Task.FromResult(result); }
        return Task.FromResult(new UiOperationResult(true, message));
    }
}

internal sealed class QaSetupLauncher : ISetupLauncher
{
    public int OpenCount;
    public void Open(IWin32Window owner, IArtVpnUiController controller) => OpenCount++;
}

internal sealed class QaClientInstaller : IClientInstallerLauncher
{
    public int Calls;
    public bool IsHappInstalled { get; set; }
    public int PreflightCalls;
    public TaskCompletionSource<bool>? NextPreflightPending;
    public Exception? NextPreflightError;
    public CancellationToken LastPreflightToken;
    public Task<bool> IsHappInstalledAsync(CancellationToken cancellationToken)
    {
        PreflightCalls++;
        LastPreflightToken = cancellationToken;
        if (NextPreflightPending is { } pending) { NextPreflightPending = null; return pending.Task; }
        if (NextPreflightError is { } error) { NextPreflightError = null; return Task.FromException<bool>(error); }
        return Task.FromResult(IsHappInstalled);
    }
    public UiOperationResult Result = new(true, "HAPP установлен; резерв ещё не проверен.");
    public Task<UiOperationResult> InstallHappAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

internal sealed class QaClientDiscovery : IClientDiscovery
{
    public TaskCompletionSource<ClientDiscoveryResult>? NextPending;
    public Exception? NextError;
    public Task<ClientDiscoveryResult> FindAsync(string product, IEnumerable<string> knownPaths)
    {
        if (NextPending is { } pending) { NextPending = null; return pending.Task; }
        if (NextError is { } error) { NextError = null; return Task.FromException<ClientDiscoveryResult>(error); }
        return Task.FromResult(new ClientDiscoveryResult(true, product,
            @"C:\QA\" + product + @"\" + (product == "Throne" ? "Throne.exe" : "Happ.exe"),
            "fixture", "fixture", product == "Throne"));
    }
}

internal static class UiQaHarness
{
    public static int Run(string receiptPath, string screenshotPath)
    {
        var failures = new List<string>();
        var controller = new QaController();
        var setup = new QaSetupLauncher();
        var recommendations = new List<string>();
        var codexRestartNotices = new List<string>();

        var discovery = ClientDiscoveryTests.Run();
        var routeHealthChecks = RouteHealthPresentationTests.Verify();
        var codexStatusReadOnlyChecks = CodexStatusReadOnlyTests.Verify();
        var codexRestartPolicyChecks = RouteChangeAdvisoryTests.VerifyAsync().GetAwaiter().GetResult();
        var codexRestartUiChecks = 0;
        var clientDiscoveryUiChecks = 0;
        try { clientDiscoveryUiChecks = VerifyClientDiscoveryUi(); }
        catch (Exception error) { failures.Add("ClientDiscoveryUi: " + error.Message); }
        var clientAcquisitionUiChecks = 0;
        try { clientAcquisitionUiChecks = ClientAcquisitionUiTests.Run(); }
        catch (Exception error) { failures.Add("ClientAcquisitionUi: " + error.Message); }
        var clientInstallationLifetimeChecks = 0;
        try { clientInstallationLifetimeChecks = ClientInstallationGateTests.RunAsync().GetAwaiter().GetResult(); }
        catch (Exception error) { failures.Add("ClientInstallationLifetime: " + error.Message); }
        StatusText.VerifyContract();
        var controlReplyChecks = ArtSport.Vpn.Shared.ControlReplyPolicy.VerifyContract();
        using var form = new MainForm(controller, setup, recommendations.Add,
            (_, message) => codexRestartNotices.Add(message));
        try
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            form.Show();
            var browserFixture = JsonSerializer.Deserialize<SanitizedStatus>(
                "{\"health\":\"Healthy\",\"browserDegraded\":true}", JsonOptions.Strict)!;
            Assert(browserFixture.ToViewState().Headline == "VPN работает, но Google требует проверки",
                "Browser degradation is hidden by a fully healthy headline.", failures);
            Wait(() => !form.IsBusy && controller.Calls.Contains("GetStatus"));
            Assert(form.Visible && Application.OpenForms.Count == 1, "Main window did not open exactly once.", failures);
            var countryLabel = Descendants(form).OfType<Label>().Single(item => item.Name == "CurrentCountry");
            Assert(!countryLabel.AutoSize && countryLabel.AutoEllipsis && countryLabel.Right < 330,
                "A long external-client label can overlap connection metrics.", failures);
            var explanation = Descendants(form).OfType<Label>().Single(item => item.Name == "ConnectionExplanation");
            var checkButton = Descendants(form).OfType<Button>().Single(item => item.Name == "CheckNowButton");
            var age = Descendants(form).OfType<Label>().Single(item => item.Name == "ConnectionAge");
            var reason = Descendants(form).OfType<Label>().Single(item => item.Name == "ConnectionSwitchReason");
            foreach (var label in new[] { explanation, age, reason }) label.Text = new string('Ж', 500);
            form.PerformLayout();
            Assert(!explanation.AutoSize && explanation.AutoEllipsis && explanation.Right < checkButton.Left &&
                   explanation.Bottom < Descendants(form).Single(item => item.Name == "ActiveChannelStatus").Top &&
                   age.Right < 330 && age.Bottom <= reason.Top && reason.Bottom < 171,
                "Long status text overlaps actions, metrics or reserve cards.", failures);
            var headline = Descendants(form).OfType<Label>().Single(item => item.Name == "ConnectionHeadline");
            var diagnosisSize = form.Size;
            foreach (var code in new[] { "NetworkUnavailable", "DirectInternetUnconfirmed", "VpnUnavailableInternetWorks",
                "AllTestedVpnUnavailable", "SubscriptionExpired", "SubscriptionQuotaExceeded", "SubscriptionAccessRejected", "ProviderClockMismatch" })
            {
                controller.ProblemCode = code;
                var refreshProblem = form.RefreshStatusAsync();
                Wait(() => refreshProblem.IsCompleted);
                foreach (var size in new[] { diagnosisSize, form.MinimumSize })
                {
                    form.Size = size;
                    Application.DoEvents();
                    var measured = TextRenderer.MeasureText(explanation.Text, explanation.Font, new Size(explanation.Width, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                    var measuredHeadline = TextRenderer.MeasureText(headline.Text, headline.Font, new Size(headline.Width, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                    Assert(explanation.Text == ConnectionProblemText.Describe(code)!.Explanation &&
                        measured.Height <= explanation.Height && measuredHeadline.Height <= headline.Height &&
                        headline.Right < checkButton.Left && headline.Bottom < explanation.Top &&
                        explanation.Bottom < Descendants(form).Single(item => item.Name == "ActiveChannelStatus").Top,
                        "Diagnosis text clipped or overlapped: " + code, failures);
                }
                if (code == "AllTestedVpnUnavailable") SaveRender(form, screenshotPath, "ARTVpn.UI.NetworkDiagnosisQA.png");
                if (code == "SubscriptionExpired") SaveRender(form, screenshotPath, "ARTVpn.UI.ExpiredSubscriptionQA.png");
            }
            controller.ProblemCode = null;
            form.Size = diagnosisSize;
            var recoveredProblem = form.RefreshStatusAsync();
            Wait(() => recoveredProblem.IsCompleted);
            var nodeLabels = Descendants(form).Where(item => item.Name.StartsWith("NodeCard", StringComparison.Ordinal))
                .SelectMany(item => item.Controls.OfType<Label>()).ToArray();
            Assert(nodeLabels.Length == 9 && nodeLabels.All(label => !label.AutoSize && label.AutoEllipsis &&
                label.Right <= label.Parent!.ClientSize.Width - 10 && label.Bottom <= label.Parent.ClientSize.Height),
                "Node captions escape their reserve cards.", failures);
            Assert(form.Text == "ART VPN для ChatGPT, YouTube и других сервисов" &&
                   Descendants(form).OfType<LinkLabel>().Any(item => item.Name == "QuattroReferralLink"),
                "Product title or prominent Quattro recommendation is missing.", failures);
            Assert(recommendations.Count == 0, "Recommendation opened without a click.", failures);
            var beforeRecommendation = controller.Calls.Count;
            ClickRecommendationLink(form, "QuattroReferralLink");
            Click(form, "MainQuattroRecommendationButton");
            Assert(recommendations.Count == 2 && controller.Calls.Count == beforeRecommendation,
                "Main recommendations did not open once per click or invoked the VPN controller.", failures);
            CheckRecommendationBounds(form, "MainQuattroRecommendation", failures);
            var originalSize = form.Size;
            form.Size = form.MinimumSize;
            Application.DoEvents();
            var headerRecommendation = Descendants(form).Single(item => item.Name == "QuattroReferralLink");
            var headerHelp = Descendants(form).Single(item => item.Name == "HelpButton");
            Assert(!headerRecommendation.Bounds.IntersectsWith(headerHelp.Bounds),
                "Quattro header overlaps Help at the minimum window size.", failures);
            var compactConnection = Descendants(form).Single(item => item.Name == "ConnectionCard");
            foreach (Control child in compactConnection.Controls)
                Assert(compactConnection.ClientRectangle.Contains(child.Bounds), "Connection card clips data at minimum size: " + child.Name, failures);
            var compactCountry = Descendants(form).OfType<Label>().Single(item => item.Name == "CurrentCountry");
            var originalCountryText = compactCountry.Text;
            compactCountry.Text = "Очень длинное название страны и канала для проверки границ интерфейса";
            Assert(compactCountry.AutoEllipsis && !compactCountry.AutoSize && compactConnection.ClientRectangle.Contains(compactCountry.Bounds) &&
                compactCountry.Text.EndsWith("интерфейса", StringComparison.Ordinal), "Long country name lost or overlaps metrics.", failures);
            compactCountry.Text = originalCountryText;
            var compactScroll = Descendants(form).OfType<FlowLayoutPanel>().Single(item => item.Name == "MainScroll");
            Assert(!compactScroll.HorizontalScroll.Visible, "Unexpected horizontal scrolling in minimum window. " +
                JsonSerializer.Serialize(new { client = compactScroll.ClientSize.ToString(), display = compactScroll.DisplayRectangle.ToString(),
                    cards = compactScroll.Controls.Cast<Control>().Select(c => new { c.Name, bounds = c.Bounds.ToString(), preferred = c.PreferredSize.ToString() }) }), failures);
            SaveRender(form, screenshotPath, "ARTVpn.UI.MinimumQA.png");
            form.Size = originalSize;
            Application.DoEvents();
            var buttons = Descendants(form).OfType<Button>().ToArray();
            Assert(!Descendants(form).Any(item => item.Name is "CodexRouteCard" or "CodexRouteStatus"),
                "Removed Codex information panel remains on the main screen.", failures);
            var mainScroll = Descendants(form).OfType<FlowLayoutPanel>().Single(item => item.Name == "MainScroll");
            Assert(mainScroll.Controls.Count == 5 && mainScroll.FlowDirection == FlowDirection.TopDown,
                "Main card flow left a placeholder after panel removal.", failures);
            var windowsRoute = Descendants(form).OfType<Label>().Single(item => item.Name == "WindowsRouteStatus");
            Assert(windowsRoute.Text.Contains("22080", StringComparison.Ordinal), "Actual Windows route is absent.", failures);
            controller.Route = WindowsRouteStatus.Describe(1, "127.0.0.1:2080", "");
            var routeRefresh = form.RefreshStatusAsync();
            Wait(() => routeRefresh.IsCompleted && !form.IsBusy);
            Assert(windowsRoute.Text.Contains("Throne", StringComparison.Ordinal) &&
                Descendants(form).OfType<Label>().Any(label => label.Text == "ART VPN готов, но Windows пока не подключена"),
                "Healthy backend was misrepresented as the active Windows route.", failures);
            controller.Route = WindowsRouteStatus.Describe(0, "127.0.0.1:22080", "");
            routeRefresh = form.RefreshStatusAsync();
            Wait(() => routeRefresh.IsCompleted && !form.IsBusy);
            Assert(windowsRoute.Text.Contains("выключен", StringComparison.Ordinal), "Disabled Windows proxy is not visible.", failures);
            controller.Route = WindowsRouteStatus.Describe(1, "127.0.0.1:22080", "");
            routeRefresh = form.RefreshStatusAsync();
            Wait(() => routeRefresh.IsCompleted && !form.IsBusy);
            Assert(!buttons.Any(button => button.Name == "CompleteChatGptUpdateButton") &&
                   typeof(MainForm).Assembly.GetType("ArtSport.ArtVpn.Ui.ProductionChatGptUpdateHelper") is null,
                "Removed ChatGPT updater remains reachable or compiled.", failures);
            foreach (var expected in new[] { "SetupButton", "HelpButton", "CheckNowButton", "AutoModeButton", "UseArtVpnButton", "ThroneModeButton", "HappModeButton", "RestoreProxyButton", "CheckUpdatesButton", "RefreshSubscriptionButton", "DiagnosticsButton" })
                Assert(buttons.Any(button => button.Name == expected), "Missing main action: " + expected, failures);
            foreach (var button in buttons)
            {
                Assert(button.Height >= 44, "Action is shorter than 44 px: " + button.Name, failures);
                Assert(button.GetPreferredSize(new Size(button.Width, 0)).Height <= button.Height,
                    "Action text is clipped: " + button.Name, failures);
            }

            Click(form, "SetupButton");
            Assert(setup.OpenCount == 1, "Setup action did not invoke exactly once.", failures);
            ClickAndWait(form, controller, "CheckNowButton", "CheckNow");
            ClickAndWait(form, controller, "UseArtVpnButton", "SetMode:ArtVpn");
            var trayMode = form.Tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "TrayModeStatus");
            Assert(trayMode.Text == "Режим: ART VPN", "Tray falsely reports Auto for ART-only mode.", failures);
            ClickAndWait(form, controller, "ThroneModeButton", "SetMode:Throne");
            Assert(trayMode.Text == "Режим: Throne — вручную" &&
                buttons.Single(item => item.Name == "ThroneModeButton").BackColor == Palette.Blue,
                "Manual Throne selection is not reflected in the window and tray.", failures);
            Assert(age.Text == "Внешний канал подключён",
                "Missing external node timestamp was misrepresented as disconnected Throne.", failures);
            ClickAndWait(form, controller, "HappModeButton", "SetMode:Happ");
            Assert(trayMode.Text == "Режим: HAPP — вручную" &&
                buttons.Single(item => item.Name == "HappModeButton").BackColor == Palette.Blue &&
                age.Text == "Внешний канал подключён",
                "HAPP mode and external connection age do not agree.", failures);
            using (var happBitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(happBitmap, form.ClientRectangle);
                happBitmap.Save(Path.Combine(Path.GetDirectoryName(screenshotPath)!, "ARTVpn.UI.HappModeQA.png"));
            }
            ClickAndWait(form, controller, "ThroneModeButton", "SetMode:Throne");
            controller.NextResult = new(false, "QA: резерв не готов.", "SelectedRoutePreflightFailed");
            ClickAndWait(form, controller, "AutoModeButton", "SetMode:Auto");
            Assert(trayMode.Text == "Режим: Throne — вручную" &&
                buttons.Single(item => item.Name == "AutoModeButton").BackColor != Palette.Blue,
                "Rejected mode request optimistically changed the selected mode.", failures);
            ClickAndWait(form, controller, "AutoModeButton", "SetMode:Auto");
            Assert(trayMode.Text == "Режим: Авто", "Auto mode was not confirmed after the successful request.", failures);
            var activeChannel = form.Controls.Find("ActiveChannelStatus", true).OfType<Label>().Single();
            var trayChannel = form.Tray.ContextMenuStrip.Items.OfType<ToolStripMenuItem>()
                .Single(item => item.Name == "TrayActiveChannelStatus");
            Assert(activeChannel.Text == "Сейчас: ART VPN" && trayChannel.Text == activeChannel.Text,
                "Auto does not show the actual ART channel in both window and tray.", failures);
            controller.ActiveChannelOverride = "Throne";
            routeRefresh = form.RefreshStatusAsync();
            Wait(() => routeRefresh.IsCompleted && !form.IsBusy);
            Assert(trayMode.Text == "Режим: Авто" && activeChannel.Text == "Сейчас: Throne — внешний VPN" &&
                trayChannel.Text == activeChannel.Text, "Auto fallback is falsely rendered as ART VPN.", failures);
            Assert(age.Text == "Внешний канал подключён",
                "Auto external fallback is falsely labelled disconnected.", failures);
            SaveRender(form, screenshotPath, "ARTVpn.UI.ExternalAgeQA.png");
            controller.ActiveChannelOverride = null;
            controller.Route = WindowsRouteStatus.Describe(1, "127.0.0.1:2080", "");
            routeRefresh = form.RefreshStatusAsync();
            Wait(() => routeRefresh.IsCompleted && !form.IsBusy);
            Assert(trayMode.Text == "Режим: не подтверждён" &&
                   buttons.Single(item => item.Name == "AutoModeButton").BackColor != Palette.Blue,
                "Stale controller mode was trusted despite a different Windows proxy.", failures);
            Assert(activeChannel.Text == "Сейчас: канал не подтверждён" && trayChannel.Text == activeChannel.Text,
                "Stale route retained a verified active-channel label.", failures);
            Assert(age.Text == "Время подключения не подтверждено",
                "Unconfirmed external route retained a connected-time assertion.", failures);
            foreach (var mode in new[] { "Throne", "Happ", "ArtVpn", "Auto" })
            {
                var previousCalls = controller.Calls.Count(item => item == "SetMode:" + mode);
                form.Tray.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "TrayMode" + mode).PerformClick();
                Wait(() => !form.IsBusy && controller.Calls.Count(item => item == "SetMode:" + mode) > previousCalls);
                Assert(form.Tray.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "TrayMode" + mode).Checked,
                    "Tray mode action does not match its confirmed route: " + mode, failures);
            }
            ClickAndWait(form, controller, "RestoreProxyButton", "RestoreSystemProxy");
            ClickAndWait(form, controller, "RefreshSubscriptionButton", "RefreshSubscription");
            ClickAndWait(form, controller, "CheckUpdatesButton", "CheckUpdates");

            var footer = (Label)typeof(MainForm).GetField("_footer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            controller.NextResult = new UiOperationResult(false, "QA: результат команды не подтверждён.", "ServiceUnavailable");
            ClickAndWait(form, controller, "CheckNowButton", "CheckNow");
            Assert(footer.Text.Contains("не подтверждён", StringComparison.Ordinal),
                "Operation error was immediately erased by a status refresh.", failures);
            routeRefresh = form.RefreshStatusAsync();
            Wait(() => routeRefresh.IsCompleted && !form.IsBusy);
            Assert(footer.Text.Contains("не подтверждён", StringComparison.Ordinal),
                "A timer-style status refresh erased an unacknowledged operation error.", failures);
            foreach (var detail in new[] { "ControllerResultRejected", "ControllerResultTimedOut", "UnknownOutcome" })
            {
                var message = (string)typeof(ServiceClient).GetMethod("Humanize", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [detail])!;
                Assert(!message.Contains("сохранён", StringComparison.Ordinal) && !message.Contains("не прерывается", StringComparison.Ordinal),
                    "Unconfirmed command outcome falsely promises an unchanged route: " + detail, failures);
            }
            ClickAndWait(form, controller, "CheckNowButton", "CheckNow");

            controller.NextException = new TimeoutException("QA lost acknowledgement");
            ClickAndWait(form, controller, "CheckNowButton", "CheckNow");
            Assert(footer.Text.Contains("не подтверждено", StringComparison.Ordinal) && !form.IsBusy &&
                Descendants(form).OfType<Button>().Single(item => item.Name == "CheckNowButton").Enabled,
                "Unexpected controller failure crashed the handler or left controls locked.", failures);
            ClickAndWait(form, controller, "CheckNowButton", "CheckNow");

            // HAPP -> Auto (and any confirmed route command): the advisory
            // is explicit, one-off, and independent of status-footer polling.
            var noticesBefore = codexRestartNotices.Count;
            controller.NextResult = new(true, "Выбранный маршрут проверен.", "SelectedRouteVerified", true);
            var autoAction = form.Tray.ContextMenuStrip.Items.OfType<ToolStripMenuItem>()
                .Single(item => item.Name == "TrayModeAuto");
            autoAction.PerformClick();
            Wait(() => !form.IsBusy && codexRestartNotices.Count > noticesBefore);
            Assert(codexRestartNotices.Count == noticesBefore + 1 &&
                   codexRestartNotices.Last().Contains("полностью закройте", StringComparison.OrdinalIgnoreCase) &&
                   codexRestartNotices.Last().Contains("VPN выключать не нужно", StringComparison.Ordinal),
                "Confirmed Auto switch did not show the explicit Codex restart notice exactly once.", failures);
            codexRestartUiChecks++;
            routeRefresh = form.RefreshStatusAsync();
            Wait(() => routeRefresh.IsCompleted && !form.IsBusy);
            Assert(codexRestartNotices.Count == noticesBefore + 1,
                "A status poll reopened the Codex restart dialog.", failures);
            codexRestartUiChecks++;
            controller.NextResult = new(false, "Переключение не подтверждено.", "UnknownOutcome", true);
            autoAction.PerformClick();
            Wait(() => !form.IsBusy);
            Assert(codexRestartNotices.Count == noticesBefore + 1,
                "A failed route command displayed a success-dependent restart notice.", failures);
            codexRestartUiChecks++;
            controller.NextResult = new(true, "Выбранный маршрут проверен.");
            autoAction.PerformClick();
            Wait(() => !form.IsBusy);
            Assert(codexRestartNotices.Count == noticesBefore + 1,
                "An ordinary route result without an old Codex process requested a restart.", failures);
            codexRestartUiChecks++;

            Click(form, "HelpButton");
            Wait(() => Application.OpenForms.OfType<HelpDialog>().Any());
            var help = Application.OpenForms.OfType<HelpDialog>().Single();
            Assert(Descendants(help).OfType<Button>().Any(item => item.Name == "HelpCloseButton"),
                "Help close action is missing.", failures);
            Assert(Descendants(help).OfType<Button>().Any(item => item.Name == "HelpQuattroRecommendationButton"),
                "Quattro recommendation is missing from help.", failures);
            Assert(recommendations.Count == 2, "Opening help navigated to Quattro automatically.", failures);
            Descendants(help).OfType<Button>().Single(item => item.Name == "HelpQuattroRecommendationButton").PerformClick();
            CheckRecommendationBounds(help, "HelpQuattroRecommendation", failures);
            var helpClose = Descendants(help).OfType<Button>().Single(item => item.Name == "HelpCloseButton");
            Assert(helpClose.Parent!.ClientRectangle.Contains(helpClose.Bounds),
                "Help close button is clipped by its footer.", failures);
            SaveRender(help, screenshotPath, "ARTVpn.UI.HelpQA.png");
            var helpText = string.Join("|", Descendants(help).Select(item => item.Text));
            Assert(helpText.Contains("Авто", StringComparison.Ordinal) && helpText.Contains("Throne", StringComparison.Ordinal) &&
                   helpText.Contains("Вернуть прежнее", StringComparison.Ordinal) &&
                   helpText.Contains("до двух резервов", StringComparison.Ordinal),
                "Help does not explain explicit activation, rollback, or bounded reserve availability.", failures);
            Assert(helpText.Contains("Основной канал — ART VPN", StringComparison.Ordinal) &&
                   helpText.Contains("не требуется автозапуск HAPP", StringComparison.Ordinal) &&
                   helpText.Contains("а не выбирает HAPP", StringComparison.Ordinal),
                "Help must distinguish ART-first, optional reserve and restoring old network settings.", failures);
            Assert(helpText.Contains("в первом окне установки", StringComparison.Ordinal) &&
                   helpText.Contains("подключится автоматически", StringComparison.Ordinal) &&
                   !helpText.Contains("Интернет пока не переключается", StringComparison.Ordinal),
                "Help still describes the obsolete separate activation after first connection.", failures);
            help.Close();

            Click(form, "DiagnosticsButton");
            Wait(() => Application.OpenForms.OfType<DiagnosticsDialog>().Any() && controller.Calls.Contains("CollectDiagnostics"));
            var diagnostics = Application.OpenForms.OfType<DiagnosticsDialog>().Single();
            var consent = Descendants(diagnostics).OfType<CheckBox>().Single(item => item.Name == "DiagnosticsConsentCheckBox");
            var send = Descendants(diagnostics).OfType<Button>().Single(item => item.Name == "SendDiagnosticsButton");
            Assert(consent.Text.Contains("grundyk888@proton.me", StringComparison.Ordinal),
                "Developer support address is not shown to the user.", failures);
            Assert(!send.Enabled && !controller.Calls.Contains("SubmitDiagnostics"),
                "Diagnostics could be sent without explicit consent.", failures);
            consent.Checked = true;
            Application.DoEvents();
            Assert(send.Enabled, "Diagnostics send action did not unlock after consent.", failures);
            send.PerformClick();
            Wait(() => controller.Calls.Contains("SubmitDiagnostics"));
            diagnostics.Close();
            Assert(controller.ExternalSideEffects == 0, "QA controller reported an external side effect.", failures);
            Assert(!UiRequestPolicy.WaitForCompletion("RefreshSubscription") &&
                   UiRequestPolicy.WaitForCompletion("CheckNow"),
                "Long subscription refresh is not backgrounded safely.", failures);
            _ = UiRequestRouter.ExecuteAsync(controller, "EnableSystemProxy", CancellationToken.None).GetAwaiter().GetResult();
            _ = UiRequestRouter.ExecuteAsync(controller, "RestoreSystemProxy", CancellationToken.None).GetAwaiter().GetResult();
            Assert(controller.Calls.Count(item => item == "EnableSystemProxy") >= 1 &&
                   controller.Calls.Count(item => item == "RestoreSystemProxy") >= 2,
                "Support request router does not expose exact proxy enable/restore actions.", failures);

            var scroll = Descendants(form).OfType<FlowLayoutPanel>().Single(item => item.Name == "MainScroll");
            scroll.AutoScrollPosition = Point.Empty;
            form.ActiveControl = null;
            form.PerformLayout();
            form.Refresh();
            Application.DoEvents();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshotPath))!);
            using (var image = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(screenshotPath, ImageFormat.Png);
            }
            Assert(new FileInfo(screenshotPath).Length > 20_000, "Rendered screenshot is missing or empty.", failures);

            form.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            Assert(!form.Visible && !form.ShowInTaskbar && form.Tray.Visible,
                "Minimize did not keep exactly one tray instance.", failures);
            form.RestoreFromTray();
            Assert(form.Visible && form.ShowInTaskbar, "Tray restore failed.", failures);

            var clientInstaller = new QaClientInstaller();
            using var wizard = new FirstRunWizard(controller, clientInstaller, recommendations.Add, new QaClientDiscovery());
            wizard.StartPosition = FormStartPosition.Manual;
            wizard.Location = new Point(-20000, -20000);
            wizard.ShowInTaskbar = false;
            wizard.Show();
            Application.DoEvents();
            var wizardButtons = Descendants(wizard).OfType<Button>().ToArray();
            Assert(wizardButtons.Any(item => item.Name == "WizardNextButton") &&
                   wizardButtons.Any(item => item.Name == "WizardBackButton") &&
                   wizardButtons.Any(item => item.Name == "FindThroneButton") &&
                   wizardButtons.Any(item => item.Name == "FindHAPPButton") &&
                   wizardButtons.Any(item => item.Name == "AcquireThroneButton") &&
                   wizardButtons.Any(item => item.Name == "AcquireHAPPButton") &&
                   wizardButtons.Any(item => item.Name == "PasteProviderButton") &&
                   wizardButtons.Any(item => item.Name == "ProviderHelpButton"),
                "First-run wizard actions are incomplete.", failures);
            var wizardLinks = Descendants(wizard).OfType<LinkLabel>().ToArray();
            Assert(wizardLinks.Any(item => item.Name == "OfficialThroneLink") &&
                   wizardLinks.Any(item => item.Name == "OfficialHAPPLink") &&
                   wizardButtons.Any(item => item.Name == "ProviderQuattroRecommendationButton"),
                "Official client pages are not visibly available.", failures);
            Assert(Descendants(wizard).OfType<CheckBox>().Any(item => item.Name == "RevealProviderCheckBox"),
                "Provider reveal control is missing.", failures);
            var next = wizardButtons.Single(item => item.Name == "WizardNextButton");
            Assert(recommendations.Count == 3, "Wizard opened a provider automatically.", failures);
            ClickRecommendationLink(wizard, "WelcomeQuattroReferralLink");
            for (var page = 0; page < 3; page++)
            {
                next.PerformClick();
                Application.DoEvents();
                if (wizard.CurrentPage == 2)
                {
                    var providerField = Descendants(wizard).OfType<TextBox>().Single(item => item.Name == "ProviderFirstBox");
                    providerField.Text = "https://example.invalid/existing-compatible-provider";
                    var beforeCalls = controller.Calls.Count;
                    wizardButtons.Single(item => item.Name == "ProviderQuattroRecommendationButton").PerformClick();
                    Assert(providerField.Text == "https://example.invalid/existing-compatible-provider" &&
                           controller.Calls.Count == beforeCalls && wizard.CurrentPage == 2,
                        "Recommendation changed the subscription, VPN state or wizard page.", failures);
                    providerField.Clear();
                    CheckRecommendationBounds(wizard, "ProviderQuattroRecommendation", failures);
                    SaveRender(wizard, screenshotPath, "ARTVpn.UI.SubscriptionQA.png");
                }
            }
            Assert(recommendations.Count == 5 && recommendations.All(url =>
                       url == "https://quattro.app/register?ref=_oxkaWjifVhzEeUH"),
                "A recommendation did not use the exact configured address.", failures);
            Assert(wizard.CurrentPage == 3, "Wizard did not reach client selection.", failures);
            var acquireThrone = wizardButtons.Single(item => item.Name == "AcquireThroneButton");
            var acquireHapp = wizardButtons.Single(item => item.Name == "AcquireHAPPButton");
            var wizardScreenshotPath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(screenshotPath))!, "ARTVpn.UI.WizardQA.png");
            using (var wizardImage = new Bitmap(wizard.Width, wizard.Height))
            {
                wizard.DrawToBitmap(wizardImage, new Rectangle(Point.Empty, wizardImage.Size));
                wizardImage.Save(wizardScreenshotPath, ImageFormat.Png);
            }
            Assert(new FileInfo(wizardScreenshotPath).Length > 15_000,
                "Wizard screenshot is missing or empty.", failures);
            acquireThrone.PerformClick();
            Wait(() => controller.Calls.Contains("AcquireClient:Throne"));
            var throneProgress = Descendants(wizard).OfType<ProgressBar>()
                .Single(item => item.Name == "ThroneDownloadProgress");
            Assert(throneProgress.Visible && throneProgress.Value == 100,
                "Throne download progress did not reach a visible completed state.", failures);
            var lastAcquire = controller.Calls.Count(item => item == "AcquireClient:Throne");
            var operationMessage = Descendants(wizard).OfType<Label>().Single(item => item.Name == "WizardOperationMessage");
            Assert(!operationMessage.AutoSize && operationMessage.AutoEllipsis &&
                operationMessage.Right < wizardButtons.Single(item => item.Name == "WizardBackButton").Left,
                "Long client setup messages overlap navigation buttons.", failures);
            controller.NextException = new TimeoutException("QA download interrupted");
            acquireThrone.PerformClick();
            Wait(() => acquireThrone.Enabled && controller.Calls.Count(item => item == "AcquireClient:Throne") == lastAcquire + 1);
            Assert(throneProgress.Value == 0 && next.Enabled &&
                Descendants(wizard).OfType<Label>().Any(item => item.Text.Contains("загрузка не подтверждена", StringComparison.Ordinal)),
                "Failed client acquisition crashes or leaves the wizard locked.", failures);
            var readyText = (string)typeof(ServiceClient).GetMethod("Humanize", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["ClientDownloadedAndReady"])!;
            Assert(!readyText.Contains("готов как резерв", StringComparison.Ordinal) && readyText.Contains("ещё не настроены", StringComparison.Ordinal),
                "Downloading a client is incorrectly presented as a verified reserve.", failures);
            Assert(acquireHapp.Enabled && !string.IsNullOrWhiteSpace(acquireHapp.AccessibleDescription),
                "Official HAPP download path is not visibly available.", failures);
            acquireHapp.PerformClick();
            Wait(() => clientInstaller.Calls == 1 && acquireHapp.Enabled);
            Assert(controller.Calls.Contains("AcquireClient:Happ"), "HAPP installer skipped verified acquisition.", failures);
            clientInstaller.Result = new(false, "Установка HAPP отменена в окне Windows.");
            acquireHapp.PerformClick();
            Wait(() => clientInstaller.Calls == 2 && acquireHapp.Enabled);
            Assert(next.Enabled && Descendants(wizard).OfType<Label>().Any(item => item.Text.Contains("установка не подтверждена", StringComparison.Ordinal)),
                "Cancelled HAPP installer is presented as a ready reserve or locks the wizard.", failures);
            var happDownloads = controller.Calls.Count(item => item == "AcquireClient:Happ");
            clientInstaller.IsHappInstalled = true;
            acquireHapp.PerformClick();
            Application.DoEvents();
            Assert(clientInstaller.Calls == 2 && controller.Calls.Count(item => item == "AcquireClient:Happ") == happDownloads,
                "Existing HAPP was unnecessarily downloaded or reinstalled.", failures);
            next.PerformClick();
            Application.DoEvents();
            Assert(wizard.CurrentPage == 4, "Wizard did not reach the final page.", failures);
            Assert(Descendants(wizard).OfType<Label>().Any(label =>
                   label.Visible && label.Text.Contains("Авто", StringComparison.Ordinal)),
                "Wizard completion does not show the next explicit activation step.", failures);
            var first = Descendants(wizard).OfType<TextBox>().Single(item => item.Name == "ProviderFirstBox");
            first.Text = "https://example.invalid/test-provider-value";
            Assert(next.Text == "Сохранить и подключить", "First connect is not explicit in the primary action.", failures);
            var connecting = new TaskCompletionSource<UiOperationResult>();
            controller.NextPending = connecting;
            next.PerformClick();
            Application.DoEvents();
            Assert(wizard.Visible && !next.Enabled && first.Text.Length == 0,
                "Wizard closed or allowed duplicate clicks before connection completed.", failures);
            connecting.SetResult(new UiOperationResult(false, "Канал недоступен; прежнее подключение сохранено.", "SelectedRoutePreflightFailed"));
            Wait(() => next.Enabled);
            Assert(wizard.Visible && operationMessage.Text.Contains("Канал недоступен"),
                "Connection failure disappeared instead of showing a recovery step.", failures);
            first.Text = "https://example.invalid/test-provider-value";
            controller.NextResult = new(true, "ART VPN подключён. Режим «Авто» включён.", "ProviderConnectedAuto", true);
            next.PerformClick();
            Wait(() => wizard.IsDisposed || !wizard.Visible);
            Assert(controller.Calls.Contains("ConfigureProvider"), "Wizard did not invoke protected provider handoff.", failures);
            Assert(first.IsDisposed || first.Text.Length == 0, "Wizard retained provider text after handoff.", failures);
            Assert(wizard.CompletedMessage.Contains("Авто"), "Wizard does not preserve the confirmed connection result.", failures);
            Assert(wizard.CompletedCodexRestartSuggested, "Wizard dropped the Codex restart notice after first connection.", failures);
            codexRestartUiChecks++;
            noticesBefore = codexRestartNotices.Count;
            form.ShowSetupCompletion(wizard.CompletedMessage, wizard.CompletedCodexRestartSuggested);
            Wait(() => !form.IsBusy && codexRestartNotices.Count > noticesBefore);
            Assert(codexRestartNotices.Count == noticesBefore + 1,
                "Setup completion did not display exactly one restart notice.", failures);
            codexRestartUiChecks++;

            using (var closingWizard = new FirstRunWizard(controller))
            {
                closingWizard.StartPosition = FormStartPosition.Manual;
                closingWizard.Location = new Point(-20000, -20000);
                closingWizard.ShowInTaskbar = false;
                closingWizard.Show();
                var closingNext = Descendants(closingWizard).OfType<Button>().Single(item => item.Name == "WizardNextButton");
                for (var page = 0; page < 3; page++) { closingNext.PerformClick(); Application.DoEvents(); }
                var held = new TaskCompletionSource<UiOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                controller.NextPending = held;
                Descendants(closingWizard).OfType<Button>().Single(item => item.Name == "AcquireThroneButton").PerformClick();
                Assert(!closingNext.Enabled, "Wizard navigation remains enabled while download is pending.", failures);
                closingWizard.Close();
                held.SetResult(new(true, "QA download completed after close"));
                for (var tick = 0; tick < 15; tick++) { Application.DoEvents(); Thread.Sleep(10); }
                Assert(closingWizard.IsDisposed, "Closing a downloading wizard did not dispose only that UI.", failures);
            }

            var allVisibleText = string.Join("|", Descendants(form).Select(control => control.Text));
            Assert(!allVisibleText.Contains("Рќ", StringComparison.Ordinal) &&
                   !allVisibleText.Contains("Р°", StringComparison.Ordinal),
                "Visible Russian text contains mojibake.", failures);
        }
        catch (Exception ex)
        {
            failures.Add(ex.GetType().Name + ": " + ex.Message);
        }

        var receipt = new
        {
            status = failures.Count == 0 ? "Passed" : "Failed",
            visibleMainActions = 11,
            clickedMainActions = 11,
            modeControlsChecks = 8,
            freshRouteHealthChecks = routeHealthChecks,
            codexStatusReadOnlyChecks,
            codexRestartPolicyChecks,
            codexRestartUiChecks,
            codexRestartNoticeDelivery = "injected dialog callback; no actual Codex restart or native modal acceptance",
            recommendationClicks = recommendations.Count,
            recommendationNavigation = "injected browser callback; no browser or network opened",
            wizardFailureChecks = 4,
            happInstallWorkflowFixtureChecks = 3,
            diagnosticsConsentRequired = true,
            diagnosticsSubmittedAfterConsent = controller.Calls.Contains("SubmitDiagnostics"),
            clientDiscovery = discovery.Status,
            clientDiscoveryScenarios = discovery.Scenarios,
            clientAcquisitionUiChecks,
            clientInstallationLifetimeChecks,
            clientDiscoveryUiChecks,
            wizardPages = 5,
            protectedProviderHandoff = controller.Calls.Contains("ConfigureProvider"),
            subscriptionRefreshBackgrounded = !UiRequestPolicy.WaitForCompletion("RefreshSubscription"),
            supportProxyCommands = controller.Calls.Count(item => item == "EnableSystemProxy") >= 1 &&
                                   controller.Calls.Count(item => item == "RestoreSystemProxy") >= 2,
            chatGptUpdateActionRemoved = !Descendants(form).OfType<Button>().Any(button => button.Name == "CompleteChatGptUpdateButton"),
            commandFeedbackRegressionChecks = 6,
            controlReplyChecks,
            tray = form.Tray.Visible,
            duplicateWindows = Application.OpenForms.Count > 1,
            externalSideEffects = controller.ExternalSideEffects,
            mojibake = failures.Any(item => item.Contains("mojibake", StringComparison.OrdinalIgnoreCase)),
            wizardScreenshot = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(screenshotPath))!, "ARTVpn.UI.WizardQA.png"),
            failures
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(receiptPath))!);
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        return failures.Count == 0 ? 0 : 1;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static int VerifyClientDiscoveryUi()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }
        var controller = new QaController();
        var pending = new TaskCompletionSource<ClientDiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new QaClientDiscovery { NextPending = pending };
        using var wizard = new FirstRunWizard(controller, new QaClientInstaller(), _ => { }, discovery);
        wizard.StartPosition = FormStartPosition.Manual;
        wizard.Location = new Point(-20000, -20000);
        wizard.ShowInTaskbar = false;
        wizard.Show();
        Application.DoEvents();
        var controls = Descendants(wizard).ToArray();
        var next = controls.OfType<Button>().Single(item => item.Name == "WizardNextButton");
        var find = controls.OfType<Button>().Single(item => item.Name == "FindThroneButton");
        var state = controls.OfType<Label>().Single(item => item.Name == "ThroneState");
        Check(!find.Enabled && state.Text.Contains("ищем", StringComparison.Ordinal), "PendingDiscoveryNotVisible");
        Check(next.Enabled, "ReadOnlySearchBlocksWizardNavigation");
        for (var page = 0; page < 3; page++) { next.PerformClick(); Application.DoEvents(); }
        var acquire = controls.OfType<Button>().Single(item => item.Name == "AcquireThroneButton");
        acquire.PerformClick();
        Wait(() => next.Enabled && controller.Calls.Contains("AcquireClient:Throne"));
        var downloadState = state.Text;
        pending.SetResult(new(true, "Throne", @"C:\QA\old\Throne.exe", "stale fixture", IsRunning: true));
        Wait(() => find.Enabled);
        Check(state.Text == downloadState && state.Text.Contains("файлы готовы", StringComparison.Ordinal),
            "LateDiscoveryErasedDownloadOutcome");
        discovery.NextError = new IOException("fixture");
        find.PerformClick();
        Wait(() => find.Enabled);
        Check(state.Text.Contains("повторите", StringComparison.Ordinal), "SearchErrorNotRecoverable");
        find.PerformClick();
        Wait(() => find.Enabled);
        Check(state.Text.Contains("запущен", StringComparison.Ordinal) &&
              state.Text.Contains("не проверен", StringComparison.Ordinal), "RunningClientMistakenForReadyReserve");
        Check(state.AccessibleDescription?.Contains(@"C:\QA\Throne\Throne.exe", StringComparison.Ordinal) == true,
            "SelectedClientLocationMissing");
        var closing = new TaskCompletionSource<ClientDiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.NextPending = closing;
        find.PerformClick();
        wizard.Close();
        closing.SetResult(new(false, "Throne", "", "closed fixture"));
        for (var tick = 0; tick < 10; tick++) { Application.DoEvents(); Thread.Sleep(10); }
        Check(wizard.IsDisposed, "ClosingDiscoveryMustNotReopenWizard");
        Check(controller.Calls.All(call => call is "AcquireClient:Throne"), "DiscoveryChangedVpnOrSubscription");
        return checks;
    }

    private static void ClickRecommendationLink(Control root, string name)
    {
        var link = Descendants(root).OfType<LinkLabel>().Single(item => item.Name == name);
        typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(link, [new LinkLabelLinkClickedEventArgs(link.Links[0])]);
        Application.DoEvents();
    }

    private static void CheckRecommendationBounds(Control root, string name, List<string> failures)
    {
        var card = Descendants(root).Single(item => item.Name == name);
        Assert(Descendants(card).Any(item => item.Text == ArtSport.ArtVpn.Common.QuattroRecommendation.BenefitText) &&
               !Descendants(card).Any(item => (item.Text + item.AccessibleDescription).Contains("рефераль", StringComparison.OrdinalIgnoreCase)),
            name + ": recommendation copy differs from the shared product text.", failures);
        foreach (var child in Descendants(card))
            Assert(child.Left >= 0 && child.Top >= 0 && child.Right <= child.Parent!.ClientSize.Width &&
                   child.Bottom <= child.Parent.ClientSize.Height,
                name + ": recommendation content escapes its container.", failures);
        if (root is FirstRunWizard)
            Assert(card.Bottom <= card.Parent!.ClientSize.Height && card.Right <= card.Parent.ClientSize.Width,
                name + ": recommendation is clipped by the wizard footer.", failures);
    }

    private static void SaveRender(Form form, string screenshotPath, string name)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(screenshotPath))!;
        Directory.CreateDirectory(folder);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(folder, name), ImageFormat.Png);
    }

    private static void Click(MainForm form, string name)
    {
        var button = Descendants(form).OfType<Button>().Single(item => item.Name == name);
        button.PerformClick();
        Application.DoEvents();
    }

    private static void ClickAndWait(MainForm form, QaController controller, string name, string expectedCall)
    {
        var before = controller.Calls.Count(item => item == expectedCall);
        Click(form, name);
        Wait(() => !form.IsBusy && controller.Calls.Count(item => item == expectedCall) == before + 1);
    }

    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Application.DoEvents();
        if (!condition()) throw new TimeoutException("UI QA operation timed out.");
    }

    private static void Assert(bool condition, string message, ICollection<string> failures)
    {
        if (!condition) failures.Add(message);
    }
}
