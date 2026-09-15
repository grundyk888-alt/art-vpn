using System.Drawing.Imaging;
using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Setup;

// Только проверка собственных WinForms-контролов: установка не запускается,
// браузер заменён записью адреса. Это не подтверждение UAC или SmartScreen.
internal static class SetupFormQa
{
    internal static int Run(string payload, string receiptPath)
    {
        var failures = new List<string>();
        var opened = new List<string>();
        var scales = new[] { 1f, 1.25f, 1.5f, 2f };
        var directory = Path.GetDirectoryName(Path.GetFullPath(receiptPath))!;
        Directory.CreateDirectory(directory);
        foreach (var scale in scales)
        {
            using var form = new SetupForm(payload, opened.Add);
            form.AutoScaleMode = AutoScaleMode.None;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            if (scale != 1) form.Scale(new SizeF(scale, scale));
            var before = opened.Count;
            form.Show();
            form.PerformLayout();
            Application.DoEvents();
            if (opened.Count != before) failures.Add("AutomaticRecommendationNavigation");
            var recommendation = Descendants(form).Single(item => item.Name == "SetupQuattroRecommendation");
            foreach (var child in Descendants(form))
                if (!(child.Parent is ScrollableControl { AutoScroll: true } scrolling
                        ? scrolling.DisplayRectangle : child.Parent!.ClientRectangle).Contains(child.Bounds))
                    failures.Add("ClippedControl:" + child.Name + ":" + scale);
            var install = Descendants(form).OfType<Button>().Single(item => item.Name == "InstallButton");
            var status = Descendants(form).Single(item => item.Name == "SetupStatus");
            if (status.RectangleToScreen(status.ClientRectangle).Bottom > install.RectangleToScreen(install.ClientRectangle).Top || !install.Enabled ||
                !form.RectangleToScreen(form.ClientRectangle).Contains(install.RectangleToScreen(install.ClientRectangle)))
                failures.Add("InstallationControlsObstructed:" + scale);
            if (recommendation.Parent is not ScrollableControl { AutoScroll: true })
                failures.Add("RecommendationCannotScrollOnSmallScreen:" + scale);
            Descendants(form).OfType<Button>().Single(item => item.Name == "SetupQuattroRecommendationButton").PerformClick();
            if (opened.Count != before + 1 || opened[^1] != QuattroRecommendation.Url || !install.Enabled)
                failures.Add("RecommendationClickContract:" + scale);
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, $"ARTVpn.Setup.{(int)(100 * scale)}.png"), ImageFormat.Png);
            form.Close();
        }
        var diagnosticChecks = 0;
        foreach (var (level, actionRequired, beforeInstall, automatic) in new[] {
            ("Info", false, true, true), ("Warning", false, true, true),
            ("Warning", true, true, false), ("Blocked", true, true, false),
            ("Info", false, false, false) })
        {
            var canInstall = level != "Blocked";
            using var diagnostics = new InstallationDiagnosticsForm(null, beforeInstall, _ =>
                Task.FromResult(new InstallationReport(DateTimeOffset.UtcNow,
                    [new("Fixture", level, "Проверочный сценарий", "Сеть и установка не изменялись.", actionRequired)], canInstall)));
            diagnostics.StartPosition = FormStartPosition.Manual;
            diagnostics.Location = new Point(-20000, -20000);
            diagnostics.ShowInTaskbar = false;
            var repeat = Descendants(diagnostics).OfType<Button>().Single(item => item.Name == "RepeatDiagnosticButton");
            var continueButton = Descendants(diagnostics).OfType<Button>().Single(item => item.Name == "ContinueInstallButton");
            diagnostics.Show();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!diagnostics.IsDisposed && !diagnostics.ContinueRequested && !repeat.Enabled && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            if (automatic)
            {
                if (!diagnostics.ContinueRequested || diagnostics.Visible) failures.Add("DiagnosticsDidNotAutoContinue:" + level);
                diagnosticChecks++;
                continue;
            }
            if (diagnostics.ContinueRequested || !diagnostics.Visible) failures.Add("DiagnosticsUnexpectedAutoContinue:" + level);
            if (!repeat.Enabled || continueButton.Enabled != canInstall) failures.Add("DiagnosticsInstallationGate:" + canInstall);
            diagnosticChecks++;
            if (canInstall)
            {
                continueButton.PerformClick();
                if (diagnostics.ContinueRequested != beforeInstall) failures.Add("DiagnosticsContinueNotRecorded");
            }
            else
            {
                continueButton.PerformClick();
                if (diagnostics.ContinueRequested) failures.Add("DiagnosticsBlockedInstallationAllowed");
                using var bitmap = new Bitmap(diagnostics.Width, diagnostics.Height);
                diagnostics.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(directory, "ARTVpn.Setup.Diagnostics.png"), ImageFormat.Png);
            }
            diagnosticChecks++;
            diagnostics.Close();
        }
        foreach (var cancelled in new[] { false, true })
        {
            var pending = new TaskCompletionSource<InstallationReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var diagnostics = new InstallationDiagnosticsForm(null, true, _ => pending.Task);
            diagnostics.StartPosition = FormStartPosition.Manual; diagnostics.Location = new Point(-20000, -20000);
            diagnostics.ShowInTaskbar = false;
            var repeat = Descendants(diagnostics).OfType<Button>().Single(item => item.Name == "RepeatDiagnosticButton");
            var next = Descendants(diagnostics).OfType<Button>().Single(item => item.Name == "ContinueInstallButton");
            diagnostics.Show(); Application.DoEvents();
            if (next.Enabled || diagnostics.ContinueRequested) failures.Add("PendingDiagnosticAllowedInstallation");
            if (cancelled)
            {
                diagnostics.Close();
                pending.SetResult(new(DateTimeOffset.UtcNow, [new("Fixture", "Info", "Готово", "Проверка")], true));
            }
            else pending.SetException(new IOException("Fixture"));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!diagnostics.IsDisposed && !repeat.Enabled && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Application.DoEvents();
            if (diagnostics.ContinueRequested || (!cancelled && (next.Enabled || !diagnostics.Visible)))
                failures.Add("FailedOrCancelledDiagnosticAllowedInstallation");
            diagnosticChecks += 2;
            diagnostics.Close();
        }
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
        {
            status = failures.Count == 0 ? "Passed" : "Failed",
            scaledLayoutFixtures = scales, recommendationClicks = opened.Count,
            browserNavigation = "injected callback", installationStarted = false,
            diagnosticChecks,
            nativeMouseOrUacAcceptance = false, failures
        }, new JsonSerializerOptions { WriteIndented = true }));
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
}
