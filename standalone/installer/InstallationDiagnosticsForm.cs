using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace ArtSport.ArtVpn.Setup;

internal sealed class InstallationDiagnosticsForm : Form
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<CancellationToken, Task<InstallationReport>> _collect;
    private readonly TextBox _details;
    private readonly Label _status;
    private readonly Button _repeat;
    private readonly Button _repair;
    private readonly Button _continue;
    private readonly Button _save;
    private InstallationReport? _report;
    private readonly bool _beforeInstall;
    internal bool ContinueRequested { get; private set; }

    internal InstallationDiagnosticsForm(string? payload, bool beforeInstall,
        Func<CancellationToken, Task<InstallationReport>>? collect = null)
    {
        Name = "InstallationDiagnosticsForm";
        Text = "ART VPN — проверка компьютера";
        _beforeInstall = beforeInstall;
        _collect = collect ?? (token => InstallationDiagnostics.CollectAsync(payload, token));
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 248, 252);
        ForeColor = Color.FromArgb(20, 35, 58);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(650, 460);
        Size = new Size(780, 640);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18) };
        layout.RowStyles.Add(new(SizeType.Absolute, 64));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 64));
        layout.RowStyles.Add(new(SizeType.Absolute, 96));
        layout.Controls.Add(new Label { Text = "Проверка перед подключением\nСеть и другие VPN не изменяются. Пароли и подписки не читаются.",
            Dock = DockStyle.Fill, AutoSize = false }, 0, 0);
        _details = new TextBox { Name = "DiagnosticDetails", Dock = DockStyle.Fill, Multiline = true,
            ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
        layout.Controls.Add(_details, 0, 1);
        _status = new Label { Name = "DiagnosticStatus", Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(0, 8, 0, 0) };
        layout.Controls.Add(_status, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
        _repeat = new Button { Name = "RepeatDiagnosticButton", Text = "Проверить ещё раз", AutoSize = true, Height = 38 };
        _repair = new Button { Name = "RepairStartupButton", Text = "Восстановить ART VPN", AutoSize = true, Height = 38 };
        _save = new Button { Name = "SaveDiagnosticButton", Text = "Сохранить отчёт", AutoSize = true, Height = 38 };
        _continue = new Button { Name = "ContinueInstallButton", Text = beforeInstall ? "Продолжить установку" : "Закрыть", AutoSize = true, Height = 38 };
        _repeat.Click += async (_, _) => await RefreshAsync();
        _repair.Click += async (_, _) => await RepairAsync();
        _save.Click += (_, _) => SaveReport();
        _continue.Click += (_, _) => { ContinueRequested = beforeInstall && InstallationContinuation.Allowed(_report); Close(); };
        actions.Controls.AddRange([_repeat, _repair, _save, _continue]);
        layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);
        Shown += async (_, _) => await RefreshAsync();
        FormClosing += (_, _) => _lifetime.Cancel();
        SetBusy(true);
    }

    private void SetBusy(bool busy)
    {
        _repeat.Enabled = !busy;
        _repair.Enabled = !busy && File.Exists(Path.Combine(InstallationDiagnostics.DataRoot, "state", "install.v1.json"));
        _continue.Enabled = !busy && (!_beforeInstall || InstallationContinuation.Allowed(_report));
        _save.Enabled = !busy && _report is not null;
    }

    private async Task RefreshAsync()
    {
        SetBusy(true);
        _report = null;
        _status.Text = "Проверяем файлы, настройки и сеть… Обычно это занимает несколько секунд.";
        try
        {
            var report = await Task.Run(() => _collect(_lifetime.Token), _lifetime.Token);
            if (IsDisposed || _lifetime.IsCancellationRequested) return;
            _report = report;
            if (_beforeInstall && InstallationContinuation.Automatic(report))
            {
                ContinueRequested = true;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            _details.Text = string.Join("\r\n\r\n", report.Checks.Select(check =>
                $"{(check.Level == "Blocked" ? "НУЖНО ИСПРАВИТЬ" : check.Level == "Warning" ? "ОБРАТИТЕ ВНИМАНИЕ" : "ПРОВЕРЕНО")} — {check.Title}\r\n{check.Message}"));
            _status.Text = InstallationContinuation.Allowed(report) ?
                report.Checks.Any(c => c.RequiresUserAction) ? "Есть настройка, требующая вашего решения. Проверьте замечания перед продолжением." :
                "Блокирующих условий не обнаружено. Замечания выше не означают, что VPN не работает." :
                "Есть препятствие для установки. Причина указана выше; текущая сеть сохранена.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!IsDisposed && !_lifetime.IsCancellationRequested)
                _status.Text = "Не удалось завершить проверку. Установка не началась. Повторите диагностику; защиту отключать не нужно.";
        }
        finally { if (!IsDisposed && !_lifetime.IsCancellationRequested) SetBusy(false); }
    }

    private async Task RepairAsync()
    {
        if (MessageBox.Show(this, "Проверить файлы и владельца ART VPN, включить её автозапуск и запустить остановленную службу?\n\n" +
            "Работающую службу не перезапускаем. Текущий VPN, DNS, firewall, пароли и настройки Codex не меняем. " +
            "Для версии в Art Monitor второе окно ART VPN не добавляется.", "Восстановление ART VPN", MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information) != DialogResult.OK) return;
        SetBusy(true);
        _status.Text = "Проверяем собственную установку. Windows может запросить разрешение администратора…";
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("OwnerSidUnavailable");
            bool success;
            if (InstallTransaction.IsAdministrator())
            {
                var result = await InstallTransaction.RepairStartupAsync(sid, _lifetime.Token);
                success = result.DetailCode == "StartupRepairVerified";
            }
            else
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("InstallerPathUnavailable");
                using var child = Process.Start(new ProcessStartInfo(executable)
                { UseShellExecute = true, Verb = "runas", Arguments = "--repair-owner " + sid,
                    WorkingDirectory = AppContext.BaseDirectory }) ?? throw new InvalidOperationException("ElevationStartRejected");
                // Закрытие окна не завершает уже запущенный системный процесс.
                await child.WaitForExitAsync();
                success = child.ExitCode == 0;
            }
            if (IsDisposed || _lifetime.IsCancellationRequested) return;
            await RefreshAsync();
            if (IsDisposed || _lifetime.IsCancellationRequested) return;
            _status.Text = success ? "Восстановление проверено. Автозапуск готов, текущий маршрут сохранён. Результаты повторной диагностики — выше." :
                "Восстановление не подтверждено. Чужие службы и сеть не сбрасывались; сохраните отчёт и код из сообщения Windows.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !_lifetime.IsCancellationRequested) _status.Text = ErrorCode.Describe(ex);
        }
        finally { if (!IsDisposed && !_lifetime.IsCancellationRequested) SetBusy(false); }
    }

    private void SaveReport()
    {
        if (_report is null) return;
        using var dialog = new SaveFileDialog { Filter = "Диагностика JSON|*.json", FileName = "ART-VPN-diagnostics.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_report, JsonOptions.Output)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _status.Text = "Не удалось сохранить файл. Выберите другую папку."; }
    }
}
