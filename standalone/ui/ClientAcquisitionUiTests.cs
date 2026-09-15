using System.Diagnostics;

namespace ArtSport.ArtVpn.Ui;

// Real window handlers with controlled delays/failures. This intentionally does
// not download software, request elevation, install clients or change the VPN.
internal static class ClientAcquisitionUiTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }
        var controller = new QaController();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new QaClientInstaller { NextPreflightPending = pending };
        using (var wizard = Open(controller, installer))
        {
            var acquire = Find<Button>(wizard, "AcquireHAPPButton");
            var next = Find<Button>(wizard, "WizardNextButton");
            var state = Find<Label>(wizard, "HAPPState");
            var message = Find<Label>(wizard, "WizardOperationMessage");
            var progress = Find<ProgressBar>(wizard, "HAPPDownloadProgress");
            acquire.PerformClick();
            Check(!acquire.Enabled && !next.Enabled && !wizard.IsDisposed,
                "HappPreflightMustHoldActionGate");
            Check(state.Text.Contains("проверяем установку", StringComparison.Ordinal) &&
                progress.Visible && progress.Style == ProgressBarStyle.Marquee,
                "HappPreflightMustShowProgress");
            var ticks = 0;
            using var timer = new System.Windows.Forms.Timer { Interval = 10 };
            timer.Tick += (_, _) => ticks++;
            timer.Start();
            PumpUntil(() => ticks >= 2);
            Check(!pending.Task.IsCompleted, "HappPreflightMustNotBlockWindowEvents");
            acquire.PerformClick();
            Find<Button>(wizard, "AcquireThroneButton").PerformClick();
            Check(installer.PreflightCalls == 1 && installer.Calls == 0 && controller.Calls.Count == 0,
                "RepeatedAcquisitionDuringPreflightMustNotStartAnything");
            pending.SetResult(true);
            PumpUntil(() => acquire.Enabled && next.Enabled);
            Check(installer.Calls == 0 && controller.Calls.Count == 0,
                "ExistingHappMustNotDownloadOrInstall");
            Check(!progress.Visible && state.Text.Contains("уже установлен", StringComparison.Ordinal) &&
                message.Text.Contains("ещё не подтверждена", StringComparison.Ordinal),
                "ExistingHappMustNotMeanReadyReserve");

            installer.NextPreflightError = new IOException("FixtureOnly");
            acquire.PerformClick();
            PumpUntil(() => acquire.Enabled && next.Enabled);
            Check(state.Text.Contains("проверка установки не завершена", StringComparison.Ordinal) &&
                message.Text.Contains("Ничего не скачивали", StringComparison.Ordinal),
                "PreflightErrorMustBeReadableAndRecoverable");
            Check(installer.Calls == 0 && controller.Calls.Count == 0,
                "UnknownInstallationMustNotTriggerDownload");
            installer.NextPreflightError = new OperationCanceledException("FixtureOnly");
            acquire.PerformClick();
            PumpUntil(() => acquire.Enabled && next.Enabled);
            Check(installer.Calls == 0 && controller.Calls.Count == 0 && progress.Value == 0,
                "PreflightCancellationMustNotContinueInstallation");
            installer.IsHappInstalled = true;
            acquire.PerformClick();
            PumpUntil(() => acquire.Enabled && next.Enabled);
            Check(state.Text.Contains("уже установлен", StringComparison.Ordinal),
                "PreflightFailureMustAllowRetry");
            installer.IsHappInstalled = false;
            acquire.PerformClick();
            PumpUntil(() => acquire.Enabled && next.Enabled);
            Check(installer.Calls == 1 && controller.Calls.SequenceEqual(["AcquireClient:Happ"]),
                "MissingHappMustUseVerifiedDownloadThenInstaller");
            Check(progress.Value == 100 && state.Text.Contains("резерв ещё не настроен", StringComparison.Ordinal),
                "DownloadedClientMustNotMeanConnectedReserve");
            wizard.Close();
        }

        var closeController = new QaController();
        var closePending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeInstaller = new QaClientInstaller { NextPreflightPending = closePending };
        using (var wizard = Open(closeController, closeInstaller))
        {
            Find<Button>(wizard, "AcquireHAPPButton").PerformClick();
            wizard.Close();
            Check(closeInstaller.LastPreflightToken.IsCancellationRequested && wizard.IsDisposed,
                "ClosingWizardMustCancelPreflightWait");
            closePending.SetResult(false);
            DrainEvents();
            Check(closeController.Calls.Count == 0 && closeInstaller.Calls == 0,
                "LatePreflightMustNotInstallAfterWindowClosed");
        }

        var downloadPending = new TaskCompletionSource<UiOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadController = new QaController { NextPending = downloadPending };
        var downloadInstaller = new QaClientInstaller();
        using (var wizard = Open(downloadController, downloadInstaller))
        {
            Find<Button>(wizard, "AcquireHAPPButton").PerformClick();
            Check(downloadController.Calls.SequenceEqual(["AcquireClient:Happ"]),
                "DownloadMustWaitUntilPreflightConfirmedMissing");
            wizard.Close();
            downloadPending.SetResult(new(true, "Fixture only"));
            DrainEvents();
            Check(wizard.IsDisposed && downloadInstaller.Calls == 0,
                "DownloadCompletionMustNotLaunchInstallerAfterClose");
        }
        return checks;
    }

    private static FirstRunWizard Open(QaController controller, QaClientInstaller installer)
    {
        var wizard = new FirstRunWizard(controller, installer, _ =>
            throw new InvalidOperationException("UnexpectedExternalBrowser"), new QaClientDiscovery())
        { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
        wizard.Show();
        Application.DoEvents();
        var next = Find<Button>(wizard, "WizardNextButton");
        for (var page = 0; page < 3; page++) { next.PerformClick(); Application.DoEvents(); }
        if (wizard.CurrentPage != 3) throw new InvalidOperationException("ClientPageNotReached");
        return wizard;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        Descendants(root).OfType<T>().Single(item => item.Name == name);

    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(5))
        { Application.DoEvents(); Thread.Sleep(5); }
        if (!done()) throw new TimeoutException("ClientAcquisitionUiFixtureTimeout");
        Application.DoEvents();
    }

    private static void DrainEvents()
    {
        for (var tick = 0; tick < 10; tick++) { Application.DoEvents(); Thread.Sleep(10); }
    }
}
