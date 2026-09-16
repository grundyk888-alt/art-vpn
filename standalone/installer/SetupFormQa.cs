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
        var layouts = scales.Select(scale => (scale, compact: false)).Concat(new[] { (1f, true), (1.5f, true) }).ToArray();
        var entryChecks = 0;
        var directory = Path.GetDirectoryName(Path.GetFullPath(receiptPath))!;
        Directory.CreateDirectory(directory);
        foreach (var (scale, compact) in layouts)
        {
            using var form = new SetupForm(payload, opened.Add);
            form.AutoScaleMode = AutoScaleMode.None;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            if (compact) form.ClientSize = new Size(650, 520);
            if (scale != 1)
            {
                // Control.Scale при AutoScaleMode.None не увеличивает шрифт.
                // Масштабируем и текст, иначе тест скроет обрезанные подписи.
                var fonts = Descendants(form).Prepend(form).Select(control =>
                    (control, font: new Font(control.Font.FontFamily, control.Font.SizeInPoints * scale, control.Font.Style))).ToArray();
                form.Scale(new SizeF(scale, scale));
                foreach (var (control, font) in fonts) control.Font = font;
                form.Disposed += (_, _) => { foreach (var (_, font) in fonts) font.Dispose(); };
            }
            var before = opened.Count;
            form.Show();
            form.PerformLayout();
            Application.DoEvents();
            if (opened.Count != before) failures.Add("AutomaticRecommendationNavigation");
            var recommendation = Descendants(form).Single(item => item.Name == "SetupQuattroRecommendation");
            if (Descendants(recommendation).Single(item => item.Name == "SetupQuattroRecommendationDescription").Text != QuattroRecommendation.BenefitText ||
                Descendants(recommendation).Any(item => (item.Text + item.AccessibleDescription).Contains("рефераль", StringComparison.OrdinalIgnoreCase)))
                failures.Add("RecommendationCopyContract:" + scale);
            var subscription = Descendants(form).OfType<TextBox>().Single(item => item.Name == "SetupSubscriptionBox");
            if (Descendants(form).Any(item => item.Name == "FindSubscriptionButton" || item.Name == "SubscriptionChoices"))
                failures.Add("RemovedSubscriptionSearchReturned:" + scale);
            var subscriptionCard = Descendants(form).Single(item => item.Name == "SetupSubscriptionCard");
            var later = Descendants(form).OfType<CheckBox>().Single(item => item.Name == "SetupConfigureLater");
            if (recommendation.Parent != subscriptionCard.Parent || recommendation.Bottom >= subscriptionCard.Top)
                failures.Add("RecommendationNotAboveSubscription:" + scale);
            if (!subscription.UseSystemPasswordChar || subscription.AccessibleName != "Ссылка подписки VPN")
                failures.Add("SubscriptionFieldContract:" + scale);
            if (scale == 1 && !compact && !recommendation.Parent!.ClientRectangle.Contains(subscriptionCard.Bounds))
                failures.Add("FirstWindowRequiresUnnecessaryScrolling");
            entryChecks += 4;
            foreach (var label in Descendants(recommendation).OfType<Label>())
            {
                var measured = TextRenderer.MeasureText(label.Text, label.Font, label.ClientSize,
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                if (measured.Height > label.ClientSize.Height)
                    failures.Add("RecommendationTextClipped:" + label.Name + ":" + scale);
            }
            // Hidden wizard pages have no on-screen geometry to accept yet.
            // Their visible geometry is checked below after navigation.
            foreach (var child in Descendants(form).Where(c => c.Visible))
                if (!(child.Parent is ScrollableControl { AutoScroll: true } scrolling
                        ? scrolling.DisplayRectangle : child.Parent!.ClientRectangle).Contains(child.Bounds))
                    failures.Add("ClippedControl:" + child.Name + ":" + scale);
            var install = Descendants(form).OfType<Button>().Single(item => item.Name == "InstallButton");
            if (install.Text != "Установить" || Descendants(form).OfType<Button>().Single(item => item.Name == "InstallationDiagnosticsButton").Text != "Диагностика перед установкой")
                failures.Add("EntryButtonLabelsChanged:" + scale);
            entryChecks++;
            var status = Descendants(form).Single(item => item.Name == "SetupStatus");
            if (status.RectangleToScreen(status.ClientRectangle).Bottom > install.RectangleToScreen(install.ClientRectangle).Top || !install.Enabled ||
                !form.RectangleToScreen(form.ClientRectangle).Contains(install.RectangleToScreen(install.ClientRectangle)))
                failures.Add("InstallationControlsObstructed:" + scale);
            if (recommendation.Parent is not ScrollableControl { AutoScroll: true })
                failures.Add("RecommendationCannotScrollOnSmallScreen:" + scale);
            foreach (var button in Descendants(form).OfType<Button>().Where(c => c.Visible))
                if (TextRenderer.MeasureText(button.Text, button.Font).Width > button.ClientSize.Width - 8)
                    failures.Add("ButtonTextClipped:" + button.Name + ":" + scale);
            var savedText = subscription.Text;
            var savedLater = later.Checked;
            subscription.Text = "https://provider.example/not-a-real-subscription";
            Descendants(form).OfType<Button>().Single(item => item.Name == "SetupQuattroRecommendationButton").PerformClick();
            if (opened.Count != before + 1 || opened[^1] != QuattroRecommendation.Url || !install.Enabled)
                failures.Add("RecommendationClickContract:" + scale);
            if (subscription.Text != "https://provider.example/not-a-real-subscription" || later.Checked != savedLater)
                failures.Add("RecommendationChangedSubscription:" + scale);
            subscription.Text = savedText;
            entryChecks++;
            if (compact && recommendation.Parent is ScrollableControl scroll)
            {
                scroll.ScrollControlIntoView(subscriptionCard);
                Application.DoEvents();
                if (!scroll.RectangleToScreen(scroll.ClientRectangle).Contains(subscription.RectangleToScreen(subscription.ClientRectangle)) ||
                    !form.RectangleToScreen(form.ClientRectangle).Contains(install.RectangleToScreen(install.ClientRectangle)))
                    failures.Add("SmallScreenCannotReachInputAndInstall:" + scale);
                entryChecks++;
            }
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, $"ARTVpn.Setup.{(int)(100 * scale)}{(compact ? ".Compact" : "")}.png"), ImageFormat.Png);
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
        var progressVisualChecks = 0;
        foreach (var (scale, compact) in layouts)
        {
            var workflow = new SetupConnectionTests.FakeWorkflow { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var progressForm = new SetupForm(payload, _ => { }, workflow);
            progressForm.AutoScaleMode = AutoScaleMode.None;
            progressForm.StartPosition = FormStartPosition.Manual; progressForm.Location = new(-20000, -20000);
            progressForm.ShowInTaskbar = false;
            if (compact) progressForm.ClientSize = new Size(650, 520);
            var progressFonts = Descendants(progressForm).Prepend(progressForm).Select(c =>
                (control: c, font: new Font(c.Font.FontFamily, c.Font.SizeInPoints * scale, c.Font.Style))).ToArray();
            progressForm.Scale(new SizeF(scale, scale));
            foreach (var (control, font) in progressFonts) control.Font = font;
            progressForm.Disposed += (_, _) => { foreach (var (_, font) in progressFonts) font.Dispose(); };
            progressForm.Show(); Application.DoEvents();
            Descendants(progressForm).OfType<CheckBox>().Single(c => c.Name == "SetupConfigureLater").Checked = true;
            var install = Descendants(progressForm).OfType<Button>().Single(c => c.Name == "InstallButton");
            install.PerformClick(); Application.DoEvents();
            var progress = Descendants(progressForm).Single(c => c.Name == "SetupProgress");
            var status = Descendants(progressForm).Single(c => c.Name == "SetupStatus");
            var processPage = Descendants(progressForm).Single(c => c.Name == "SetupProcessPage");
            if (!processPage.Visible || Descendants(progressForm).Single(c => c.Name == "SetupEntryPage").Visible)
                failures.Add("ProcessIsNotSeparatePage:" + scale + ":" + compact);
            foreach (var label in Descendants(processPage).OfType<Label>().Where(c => c.Visible))
            {
                var available = new Size(label.ClientSize.Width - label.Padding.Horizontal, label.ClientSize.Height - label.Padding.Vertical);
                if (TextRenderer.MeasureText(label.Text, label.Font, available, TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height > available.Height)
                    failures.Add("ProcessTextClipped:" + label.Name + ":" + scale + ":" + compact);
            }
            if (processPage is ScrollableControl { HorizontalScroll.Visible: true })
                failures.Add("ProcessHorizontalScroll:" + scale + ":" + compact);
            if (!progress.Visible || install.Enabled || TextRenderer.MeasureText(status.Text, status.Font, status.ClientSize,
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height > status.ClientSize.Height)
                failures.Add("BusyProgressOrTextHidden:" + scale);
            using (var bitmap = new Bitmap(progressForm.Width, progressForm.Height))
            { progressForm.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, $"ARTVpn.Setup.Progress.{(int)(scale * 100)}{(compact ? ".Compact" : "")}.png")); }
            workflow.Pending.SetResult(false);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!install.Enabled && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
            if (!install.Enabled || progress.Visible) failures.Add("BusyProgressNotStopped:" + scale);
            var processScroll = (ScrollableControl)processPage;
            var note = Descendants(processPage).Single(c => c.Name == "SetupProcessNote");
            processScroll.ScrollControlIntoView(note); Application.DoEvents();
            if (!processScroll.RectangleToScreen(processScroll.ClientRectangle).Contains(note.RectangleToScreen(note.ClientRectangle)))
                failures.Add("CannotReachProcessEnd:" + scale + ":" + compact);
            processScroll.AutoScrollPosition = Point.Empty; Application.DoEvents();
            foreach (var child in Descendants(progressForm).Where(c => c.Visible))
                if (!(child.Parent is ScrollableControl { AutoScroll: true } scrolling ? scrolling.DisplayRectangle : child.Parent!.ClientRectangle).Contains(child.Bounds))
                    failures.Add("ProcessResultControlClipped:" + child.Name + ":" + scale + ":" + compact + ":" + child.Bounds);
            foreach (var button in Descendants(progressForm).OfType<Button>().Where(c => c.Visible))
                if (TextRenderer.MeasureText(button.Text, button.Font).Width > button.ClientSize.Width - 8)
                    failures.Add("ProcessButtonTextClipped:" + button.Name + ":" + scale + ":" + compact);
            using (var bitmap = new Bitmap(progressForm.Width, progressForm.Height))
            { progressForm.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, $"ARTVpn.Setup.ProcessStopped.{(int)(scale * 100)}{(compact ? ".Compact" : "")}.png")); }
            progressForm.Close(); progressVisualChecks += 2;

            if (compact) continue;

            using var countdownForm = new CodexCloseCountdownForm(() => Task.FromResult("Проверка без закрытия приложений."));
            countdownForm.AutoScaleMode = AutoScaleMode.None;
            countdownForm.StartPosition = FormStartPosition.Manual; countdownForm.Location = new(-20000, -20000); countdownForm.ShowInTaskbar = false;
            var countdownFonts = Descendants(countdownForm).Prepend(countdownForm).Select(c =>
                (control: c, font: new Font(c.Font.FontFamily, c.Font.SizeInPoints * scale, c.Font.Style))).ToArray();
            countdownForm.Scale(new SizeF(scale, scale));
            foreach (var (control, font) in countdownFonts) control.Font = font;
            countdownForm.Disposed += (_, _) => { foreach (var (_, font) in countdownFonts) font.Dispose(); };
            countdownForm.Show(); Application.DoEvents();
            foreach (var label in Descendants(countdownForm).OfType<Label>())
                if (TextRenderer.MeasureText(label.Text, label.Font, label.ClientSize, TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height > label.ClientSize.Height)
                    failures.Add("CountdownTextClipped:" + scale);
            using (var bitmap = new Bitmap(countdownForm.Width, countdownForm.Height))
            { countdownForm.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, $"ARTVpn.Setup.CodexCountdown.{(int)(scale * 100)}.png")); }
            countdownForm.Close(); progressVisualChecks++;
        }
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
        {
            status = failures.Count == 0 ? "Passed" : "Failed",
            scaledLayoutFixtures = scales, recommendationClicks = opened.Count,
            compactLayoutFixtures = 2, entryChecks, fontsScaledWithControls = true,
            browserNavigation = "injected callback", installationStarted = false,
            diagnosticChecks, progressVisualChecks,
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
