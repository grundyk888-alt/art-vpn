using System.Diagnostics;
using ArtSport.Vpn.Shared;

namespace ArtSport.ArtVpn.Setup;

internal sealed record CodexCloseTarget(int Id, long StartUtcTicks, int Session, CodexLaunchTarget? Launch = null);

internal static class CodexGracefulClose
{
    internal const string RestartedMessage = "Codex открыт. VPN подключён.";
    internal static CodexCloseTarget[] Capture()
    {
        var result = new List<CodexCloseTarget>();
        using var self = Process.GetCurrentProcess();
        foreach (var name in new[] { "codex", "ChatGPT" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    try
                    {
                        if (CodexClientProcesses.Matches(process.ProcessName, process.SessionId, self.SessionId, process.HasExited))
                            result.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.SessionId, CodexAppLauncher.Capture(process)));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return result.ToArray();
    }

    internal static bool IdentityMatches(CodexCloseTarget target, int id, long ticks, int session, int currentSession,
        string name, bool exited) => target.Id == id && target.StartUtcTicks == ticks && target.Session == session &&
        CodexClientProcesses.Matches(name, session, currentSession, exited);

    private static bool Matches(CodexCloseTarget target, Process process)
    {
        using var self = Process.GetCurrentProcess();
        return IdentityMatches(target, process.Id, process.StartTime.ToUniversalTime().Ticks, process.SessionId,
            self.SessionId, process.ProcessName, process.HasExited);
    }

    internal static bool IsStillTarget(CodexCloseTarget target)
    {
        try { using var process = Process.GetProcessById(target.Id); return Matches(target, process); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
        // Access denied is not evidence of exit. Keep an honest manual-close result.
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    internal static Task<string> RequestAsync(CodexCloseTarget[] targets) => Task.Run(async () =>
    {
        bool RequestClose()
        {
            if (CodexRestartManager.Request(targets)) return true;
            var requested = false;
            foreach (var target in targets)
            try
            {
                using var process = Process.GetProcessById(target.Id);
                // Only the original GUI; never Kill or close background/CLI windows.
                if (target.Launch is not null && Matches(target, process) && process.MainWindowHandle != IntPtr.Zero)
                    requested |= process.CloseMainWindow();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return requested;
        }
        var launchers = targets.Where(t => t.Launch is not null).Select(t => t.Launch!)
            .DistinctBy(t => t.ApplicationId.Length > 0 ? t.ApplicationId : t.Executable, StringComparer.OrdinalIgnoreCase).ToArray();
        return await CodexRestartSequence.RunAsync(RequestClose, () => targets.Any(IsStillTarget),
            () => Capture().Any(current => !targets.Any(old => old.Id == current.Id && old.StartUtcTicks == current.StartUtcTicks)),
            () => launchers.Length > 0 && launchers.All(CodexAppLauncher.Launch));
    });
}

internal sealed class CodexCloseCountdown
{
    internal bool Stopped { get; private set; }
    internal int Remaining(TimeSpan elapsed) => Math.Max(0, (int)Math.Ceiling(10 - Math.Max(0, elapsed.TotalSeconds)));
    internal void Cancel() => Stopped = true;
    internal bool TryBegin(TimeSpan elapsed)
    {
        if (Stopped || Remaining(elapsed) > 0) return false;
        Stopped = true;
        return true;
    }
}

internal sealed class CodexCloseCountdownForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private readonly Stopwatch _visibleTime = new();
    private readonly CodexCloseCountdown _countdown = new();
    private readonly Func<Task<string>> _close;
    private readonly Func<TimeSpan> _elapsed;
    private readonly Label _status;
    private readonly Button _cancel;
    private bool _requesting;

    internal CodexCloseCountdownForm(Func<Task<string>> close, Func<TimeSpan>? elapsedForQa = null)
    {
        _close = close;
        _elapsed = elapsedForQa ?? (() => _visibleTime.Elapsed);
        Name = "CodexCloseCountdownForm"; Text = "ART VPN — перезапуск Codex";
        Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(246, 248, 252);
        ForeColor = Color.FromArgb(20, 35, 58); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false; ClientSize = new Size(540, 184);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 44));
        _status = new Label { Name = "CodexCountdownStatus", Dock = DockStyle.Fill,
            Text = "Codex будет перезапущен через 10 секунд\nдля подключения к новому VPN.",
            ForeColor = Color.FromArgb(35, 85, 165) };
        _cancel = new Button { Name = "CodexDontClose", Text = "Отмена", Dock = DockStyle.Right, Width = 130,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(48, 94, 157), Cursor = Cursors.Hand };
        _cancel.FlatAppearance.BorderColor = Color.FromArgb(177, 195, 220);
        _cancel.Click += (_, _) => { _countdown.Cancel(); _timer.Stop(); Close(); };
        CancelButton = _cancel; AcceptButton = _cancel;
        layout.Controls.Add(_status, 0, 0); layout.Controls.Add(_cancel, 0, 1); Controls.Add(layout);
        Shown += (_, _) => { _visibleTime.Start(); _timer.Start(); _cancel.Focus(); };
        // Switching away gives the user time to save work, not a hidden countdown.
        Deactivate += (_, _) => { _visibleTime.Stop(); if (!_countdown.Stopped) _status.Text = "Перезапуск на паузе, пока вы в другом окне."; };
        Activated += (_, _) => { if (!_countdown.Stopped) _visibleTime.Start(); };
        _timer.Tick += async (_, _) => await TickAsync();
        FormClosing += (_, e) =>
        {
            if (_requesting) { e.Cancel = true; return; }
            _countdown.Cancel(); _timer.Stop(); _visibleTime.Stop();
        };
    }

    private async Task TickAsync()
    {
        if (_countdown.Stopped || !_visibleTime.IsRunning) return;
        var elapsed = _elapsed();
        _status.Text = $"Codex будет перезапущен через {_countdown.Remaining(elapsed)} секунд\nдля подключения к новому VPN.";
        if (!_countdown.TryBegin(elapsed)) return;
        _timer.Stop(); _visibleTime.Stop(); _requesting = true; _cancel.Enabled = false;
        _status.Text = "Перезапускаем Codex…";
        try { _status.Text = await _close(); }
        catch { _status.Text = "ART VPN подключён. Переоткройте Codex вручную."; }
        finally { _requesting = false; _cancel.Text = "Готово"; _cancel.Enabled = true; }
        if (_status.Text == CodexGracefulClose.RestartedMessage) Close();
    }

    // Same handler and controls; only the clock/action are fake. No real app closes.
    internal Task TickForQaAsync() => TickAsync();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _countdown.Cancel(); _timer.Dispose(); _visibleTime.Stop(); }
        base.Dispose(disposing);
    }
}
