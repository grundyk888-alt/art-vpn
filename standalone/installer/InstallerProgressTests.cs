namespace ArtSport.ArtVpn.Setup;

internal static class InstallerProgressTests
{
    internal static int Run(Action<string>? trace = null)
    {
        var count = 0;
        void Check(bool value, string code) { if (!value) throw new InvalidDataException(code); count++; }
        using var owner = new Form();
        foreach (var (level, action, valid, automatic) in new[] {
            ("Info", false, true, true), ("Warning", false, true, true),
            ("Warning", true, true, false), ("Blocked", true, false, false), ("Future", false, true, false) })
        {
            var report = new InstallationReport(DateTimeOffset.UtcNow,
                [new("Fixture", level, "Проверка", "Без изменений", action)], valid);
            var collected = 0; var shown = 0;
            var task = InstallationPreflight.RunAsync(owner, "unused", CancellationToken.None,
                _ => { collected++; return Task.FromResult(report); }, r => { shown++; return InstallationContinuation.Allowed(r); });
            Pump(task);
            Check(collected == 1 && shown == (automatic ? 0 : 1), "PreflightDialogOrDuplicateCollect");
            Check(task.Result == InstallationContinuation.Allowed(report), "PreflightGateChanged");
        }
        var failedShown = 0;
        var failed = InstallationPreflight.RunAsync(owner, "unused", CancellationToken.None,
            _ => throw new IOException("Fixture"), report => { failedShown++; Check(!InstallationContinuation.Allowed(report), "FailedCheckAllowedInstall"); return false; });
        Pump(failed); Check(!failed.Result && failedShown == 1, "UnknownResultHidden");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var displayed = 0;
            var task = InstallationPreflight.RunAsync(owner, "unused", cancelled.Token,
                _ => throw new InvalidDataException("MustNotCollect"), _ => { displayed++; return true; });
            Pump(task); Check(!task.Result && displayed == 0, "CancelledPreflightContinued");
        }
        var initial = new InstallationReport(DateTimeOffset.UtcNow, [new("Blocked", "Blocked", "Проверка", "Нужен ответ", true)], false);
        var repeats = 0;
        using (var form = new InstallationDiagnosticsForm(null, true,
            _ => { repeats++; return Task.FromResult(initial); }, initial))
        {
            Prepare(form); form.Show(); Application.DoEvents();
            Check(repeats == 0 && !Find<Button>(form, "ContinueInstallButton").Enabled, "InitialReportRecollected");
            form.Close();
        }
        trace?.Invoke("progress:busy-form");
        var flow = new SetupConnectionTests.FakeWorkflow { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using (var form = new SetupForm("unused", _ => { }, flow))
        {
            Prepare(form); form.Show(); Application.DoEvents();
            var progress = Find<ProgressBar>(form, "SetupProgress");
            Check(!progress.Visible, "IdleProgressVisible");
            Find<CheckBox>(form, "SetupConfigureLater").Checked = true;
            Find<Button>(form, "InstallButton").PerformClick(); Application.DoEvents();
            Check(progress.Visible && progress.Style == ProgressBarStyle.Marquee && progress.MarqueeAnimationSpeed > 0, "ProgressMissingWhileBusy");
            Check(Find<Label>(form, "SetupElapsed").Visible && Find<Label>(form, "SetupStatus").Text.Contains("Шаг 1"), "StageMissing");
            Check(!Find<Button>(form, "InstallationDiagnosticsButton").Enabled, "ConcurrentDiagnosticsAllowed");
            form.Close(); Check(!form.IsDisposed, "BusyFormClosed");
            flow.Pending.SetResult(false);
            PumpUntil(() => Find<Button>(form, "InstallButton").Enabled);
            Check(!progress.Visible && progress.MarqueeAnimationSpeed == 0 && flow.Installs == 0, "ProgressNotCleanedAfterStop");
            form.Close();
        }
        trace?.Invoke("progress:countdown-policy");
        var countdown = new CodexCloseCountdown();
        Check(countdown.Remaining(TimeSpan.Zero) == 10 && !countdown.TryBegin(TimeSpan.Zero), "CountdownStartsEarly");
        Check(countdown.Remaining(TimeSpan.FromSeconds(9.99)) == 1 && !countdown.TryBegin(TimeSpan.FromSeconds(9.99)), "CountdownLessThanTen");
        Check(countdown.TryBegin(TimeSpan.FromSeconds(10)), "CountdownNeverExpires");
        Check(!countdown.TryBegin(TimeSpan.FromSeconds(20)), "CountdownRunsTwice");
        var stopped = new CodexCloseCountdown(); stopped.Cancel();
        Check(!stopped.TryBegin(TimeSpan.FromSeconds(100)), "CancelledCountdownRuns");
        var target = new CodexCloseTarget(1, 20, 3);
        Check(CodexGracefulClose.IdentityMatches(target, 1, 20, 3, 3, "ChatGPT", false), "OriginalProcessRejected");
        Check(!CodexGracefulClose.IdentityMatches(target, 1, 21, 3, 3, "ChatGPT", false), "ReusedPidAccepted");
        Check(!CodexGracefulClose.IdentityMatches(target, 1, 20, 3, 4, "ChatGPT", false), "OtherSessionAccepted");
        Check(!CodexGracefulClose.IdentityMatches(target, 1, 20, 3, 3, "chrome", false), "OtherApplicationAccepted");
        Check(!CodexGracefulClose.IdentityMatches(target, 1, 20, 3, 3, "ChatGPT", true), "ExitedProcessAccepted");
        foreach (var cancelButton in new[] { true, false })
        {
            trace?.Invoke("progress:countdown-cancel:" + cancelButton);
            var calls = 0;
            using var form = new CodexCloseCountdownForm(() => { calls++; return Task.FromResult("Fixture"); });
            Prepare(form); form.Show(); Application.DoEvents();
            Check(Find<Label>(form, "CodexCountdownStatus").Text.Contains("10"), "CountdownWarningAbsent");
            if (cancelButton) Find<Button>(form, "CodexDontClose").PerformClick(); else form.Close();
            Application.DoEvents(); Check(calls == 0 && !form.Visible, "CountdownCancelClosedApplication");
        }
        foreach (var fails in new[] { false, true })
        {
            trace?.Invoke("progress:countdown-expiry:" + fails);
            var elapsed = TimeSpan.Zero; var calls = 0;
            using var form = new CodexCloseCountdownForm(() =>
            { calls++; return fails ? Task.FromException<string>(new IOException("Fixture")) : Task.FromResult("Проверка: обычное закрытие запрошено."); }, () => elapsed);
            Prepare(form); form.Show(); Application.DoEvents();
            elapsed = TimeSpan.FromSeconds(9.9); Pump(form.TickForQaAsync());
            Check(calls == 0, "CountdownActionBeforeDeadline");
            elapsed = TimeSpan.FromSeconds(10); Pump(form.TickForQaAsync());
            Pump(form.TickForQaAsync());
            Check(calls == 1 && Find<Button>(form, "CodexDontClose").Text == "Готово", "CountdownActionNotExactlyOnce");
            Check(Find<Button>(form, "CodexDontClose").Enabled && form.Visible, "CountdownResultNotVisible");
            Check(!fails || Find<Label>(form, "CodexCountdownStatus").Text.Contains("ART VPN подключён"), "CloseErrorBecameVpnFailure");
            form.Close();
        }
        count += CodexRestartTests.RunAsync().GetAwaiter().GetResult();
        return count;
    }

    private static T Find<T>(Control form, string name) where T : Control => (T)form.Controls.Find(name, true).Single();
    private static void Prepare(Form form) { form.StartPosition = FormStartPosition.Manual; form.Location = new(-20000, -20000); form.ShowInTaskbar = false; }
    private static void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        if (!done()) throw new TimeoutException("ProgressFixtureTimedOut");
    }
}
