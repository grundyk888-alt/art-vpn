using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Setup;

// Настоящие обработчики формы, но никакой установки, сети и закрытия Codex.
internal static class SetupProcessPageTests
{
    internal static int Run(Action<string>? trace = null)
    {
        var checks = 0;
        void Check(bool value, string code) { if (!value) throw new InvalidDataException(code); checks++; }
        var flow = new SetupConnectionTests.FakeWorkflow
        {
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously),
            PendingInstall = new(TaskCreationOptions.RunContinuationsAsynchronously),
            PendingConnect = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using (var form = Create(flow))
        {
            trace?.Invoke("page:stages");
            var entry = Find<Panel>(form, "SetupEntryPage");
            var process = Find<TableLayoutPanel>(form, "SetupProcessPage");
            var button = Find<Button>(form, "InstallButton");
            var field = Find<TextBox>(form, "SetupSubscriptionBox");
            var elapsed = Find<Label>(form, "SetupElapsed");
            Check(entry.Visible && !process.Visible && !elapsed.Visible, "ProcessVisibleBeforeAction");
            Check(button.Text == "Установить", "InitialInstallLabelChanged");
            Check(Find<Button>(form, "InstallationDiagnosticsButton").Text == "Диагностика перед установкой", "PreinstallDiagnosticsLabelChanged");
            field.Text = "https://provider.example/private-fixture";
            Check(button.Text == "Установить", "SubscriptionChangedInstallLabel");
            button.PerformClick(); Application.DoEvents();
            Check(!entry.Visible && process.Visible && !button.Enabled, "SeparateProcessStepMissing");
            Check(!Find<Button>(form, "InstallationDiagnosticsButton").Visible, "DiagnosticsVisibleOnHappyPath");
            Check(!Step(form, 0).Text.StartsWith("✓") && !Step(form, 1).Text.StartsWith("✓"), "UnfinishedPreflightMarkedDone");
            PumpUntil(() => elapsed.Text != "Прошло 00:00");
            trace?.Invoke("page:elapsed");
            Check(elapsed.Visible && elapsed.Text.StartsWith("Прошло "), "ElapsedNotUpdating");
            flow.Pending.SetResult(true); PumpUntil(() => flow.Installs == 1);
            trace?.Invoke("page:install");
            Check(Step(form, 0).Text.StartsWith("✓") && !Step(form, 1).Text.StartsWith("✓"), "InstallStageNotHonest");
            Check(!button.Enabled && Find<ProgressBar>(form, "SetupProgress").Visible, "PendingInstallerLooksIdle");
            flow.PendingInstall.SetResult(true); PumpUntil(() => flow.Connects == 1);
            trace?.Invoke("page:connect");
            Check(Step(form, 1).Text.StartsWith("✓") && !Step(form, 2).Text.StartsWith("✓") &&
                !Step(form, 3).Text.StartsWith("✓"), "PendingChannelsClaimedConnected");
            Check(!button.Enabled && Find<ProgressBar>(form, "SetupProgress").Visible, "PendingChannelsLookIdle");
            flow.PendingConnect.SetResult(new ProviderConnectionResult(false, "QualifiedPoolEmpty"));
            PumpUntil(() => button.Enabled);
            trace?.Invoke("page:failure");
            Check(process.Visible && !entry.Visible && elapsed.Visible && !Find<ProgressBar>(form, "SetupProgress").Visible,
                "FailureLostProcessReport");
            Check(Find<Label>(form, "SetupProcessTitle").Text == "Подключение не подтверждено" &&
                !Step(form, 3).Text.StartsWith("✓"), "FailedConnectClaimedSuccess");
            Find<Button>(form, "SetupBackButton").PerformClick(); Application.DoEvents();
            Check(entry.Visible && !process.Visible && field.Text == "https://provider.example/private-fixture",
                "BackLostSubscriptionOrPage");
            Check(button.Text == "Подключить", "RetryButtonClaimsReinstallation");
            Check(Find<Button>(form, "InstallationDiagnosticsButton").Text == "Диагностика перед установкой", "BackLostDiagnosticsLabel");
            flow.PendingConnect = null; flow.Connected = true;
            button.PerformClick(); PumpUntil(() => !form.Visible);
            trace?.Invoke("page:retry-completed");
            Check(flow.Diagnoses == 1 && flow.Installs == 1 && flow.Connects == 2 && flow.Opens == 1,
                "BackRetryRepeatedInstall");
        }

        foreach (var empty in new[] { "", "   " })
        {
            trace?.Invoke("page:empty:" + empty.Length);
            var plain = new SetupConnectionTests.FakeWorkflow { FailOpen = true };
            using var form = Create(plain);
            Find<TextBox>(form, "SetupSubscriptionBox").Text = empty;
            var button = Find<Button>(form, "InstallButton");
            Check(button.Text == "Установить" &&
                Find<Label>(form, "SetupConnectionExplanation").Text.Contains("без подключения"), "EmptyInputAmbiguous");
            button.PerformClick(); PumpUntil(() => button.Enabled);
            Check(plain.Installs == 1 && plain.Connects == 0 && plain.Advice == 0, "EmptyInputChangedConnection");
            Check(Find<Label>(form, "SetupProcessTitle").Text == "Программа установлена" &&
                !Step(form, 2).Text.StartsWith("✓") && !Step(form, 3).Text.StartsWith("✓"), "NoSubscriptionClaimsVpn");
            Check(button.Text == "Открыть ART VPN" && !Find<Button>(form, "SetupBackButton").Visible, "OpenRetryMissing");
            plain.FailOpen = false; button.PerformClick(); PumpUntil(() => !form.Visible);
            Check(plain.Installs == 1 && plain.Connects == 0 && plain.Opens == 2, "OpenRetryMutatedInstallation");
        }
        trace?.Invoke("page:invalid");
        var invalid = new SetupConnectionTests.FakeWorkflow();
        using (var form = Create(invalid))
        {
            Find<TextBox>(form, "SetupSubscriptionBox").Text = "not a subscription";
            Find<Button>(form, "InstallButton").PerformClick(); Application.DoEvents();
            Check(invalid.Diagnoses == 0 && invalid.Installs == 0 &&
                Find<Panel>(form, "SetupEntryPage").Visible && !Find<TableLayoutPanel>(form, "SetupProcessPage").Visible,
                "InvalidNonemptyInputInstalled");
            form.Close();
        }
        trace?.Invoke("page:post-commit");
        var committed = new SetupConnectionTests.FakeWorkflow { FailOpen = true, FailAdvisory = true, AdvisoryText = "Fixture" };
        using (var form = Create(committed))
        {
            Find<TextBox>(form, "SetupSubscriptionBox").Text = "https://provider.example/subscription";
            var button = Find<Button>(form, "InstallButton");
            button.PerformClick(); PumpUntil(() => button.Enabled);
            Check(form.Succeeded && form.ConnectionCode == "ProviderConnectedAuto" && Step(form, 3).Text.StartsWith("✓"),
                "AncillaryFailureInvalidatedCommit");
            Check(Find<Label>(form, "SetupProcessTitle").Text == "ART VPN подключён" &&
                Find<Label>(form, "SetupStatus").Text.Contains("Подключение сохранено"), "AncillaryFailureClaimsVpnFailure");
            Check(!Find<ProgressBar>(form, "SetupProgress").Visible && button.Text == "Открыть ART VPN", "CommittedProgressStillRunning");
            committed.FailOpen = false; button.PerformClick(); PumpUntil(() => !form.Visible);
            Check(committed.Installs == 1 && committed.Connects == 1 && committed.Advice == 1 && committed.Opens == 2,
                "PostCommitRetryRepeatedNetworkOperation");
        }
        return checks;
    }

    private static SetupForm Create(SetupConnectionTests.FakeWorkflow flow)
    {
        var form = new SetupForm(Path.Combine(Path.GetTempPath(), "no-real-payload"), _ => { }, flow)
        { StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000), ShowInTaskbar = false };
        form.Show(); Application.DoEvents();
        return form;
    }
    private static Label Step(Control form, int index) => Find<Label>(form, "SetupProcessStep" + index);
    private static T Find<T>(Control root, string name) where T : Control => (T)root.Controls.Find(name, true).Single();
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        if (!done()) throw new TimeoutException("ProcessPageFixtureTimedOut");
    }
}
