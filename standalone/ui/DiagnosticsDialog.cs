namespace ArtSport.ArtVpn.Ui;

internal sealed class DiagnosticsDialog : Form
{
    private const string LocalReportPath = @"C:\ProgramData\ART VPN\reports\latest-support.v1.json";
    private readonly IArtVpnUiController _controller;
    private readonly Label _status;
    private readonly CheckBox _consent;
    private readonly Button _send;
    private bool _prepared;

    public DiagnosticsDialog(IArtVpnUiController controller)
    {
        _controller = controller;
        Name = "DiagnosticsDialog";
        Text = "Диагностика ART VPN";
        Font = new Font("Segoe UI", 10);
        BackColor = Palette.Canvas;
        ForeColor = Palette.Ink;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 590);

        var title = UiFactory.Label("🩺 Помочь исправить проблему", 19, true);
        title.Location = new Point(34, 28);
        var subtitle = UiFactory.Label(
            "ART VPN подготовит небольшой очищенный отчёт. Он никуда не отправится без вашего подтверждения.",
            9.5f, false, Palette.Muted);
        subtitle.Location = new Point(36, 70);
        subtitle.MaximumSize = new Size(640, 0);

        var included = new RoundedPanel
        {
            Location = new Point(32, 113), Size = new Size(656, 180), Radius = 16,
            BackColor = Color.White, Padding = new Padding(22)
        };
        var includedTitle = UiFactory.Label("В отчёт войдёт", 12, true);
        includedTitle.Location = new Point(22, 18);
        var includedText = UiFactory.Label(
            "✓ версия ART VPN и состояние службы\n✓ страна, качество канала и время последнего переключения\n✓ обезличенные коды ошибок и результат обновления\n✓ случайный номер отчёта для ответа разработчика",
            9.7f, false, Palette.Muted);
        includedText.Location = new Point(24, 53);
        included.Controls.Add(includedTitle);
        included.Controls.Add(includedText);

        var excluded = new RoundedPanel
        {
            Location = new Point(32, 309), Size = new Size(656, 112), Radius = 16,
            BackColor = Palette.GreenSoft, BorderColor = Color.FromArgb(193, 232, 214), Padding = new Padding(22)
        };
        var excludedTitle = UiFactory.Label("🔒 Не передаётся", 11.5f, true, Palette.Green);
        excludedTitle.Location = new Point(22, 16);
        var excludedText = UiFactory.Label(
            "ссылка подписки, ключи и токены, адреса узлов, имя пользователя, IP/MAC и содержимое трафика",
            9.4f, false, Palette.Muted);
        excludedText.Location = new Point(24, 51);
        excludedText.MaximumSize = new Size(600, 0);
        excluded.Controls.Add(excludedTitle);
        excluded.Controls.Add(excludedText);

        _consent = new CheckBox
        {
            Name = "DiagnosticsConsentCheckBox",
            Text = $"Я посмотрел состав отчёта и разрешаю подготовить письмо на {SupportEmailComposer.DeveloperEmail}",
            AutoSize = true,
            Location = new Point(38, 444),
            Font = new Font("Segoe UI Semibold", 9.5f),
            ForeColor = Palette.Ink
        };
        _status = UiFactory.Label("Готовим очищенный отчёт…", 9, false, Palette.Muted);
        _status.Location = new Point(38, 483);
        _status.MaximumSize = new Size(420, 0);
        var save = UiFactory.Button("SaveDiagnosticsButton", "Сохранить файл", false, 178);
        save.Location = new Point(304, 520);
        save.Click += (_, _) => SaveLocalCopy();
        var computer = UiFactory.Button("ComputerDiagnosticsButton", "Проверить компьютер", false, 245);
        computer.Location = new Point(38, 520);
        computer.Click += (_, _) =>
        {
            try { ArtSport.Vpn.Shared.VpnMaintenanceLauncher.OpenDiagnostics(); }
            catch { _status.Text = "Диагностика установки недоступна. Откройте новый установщик → «Диагностика и восстановление». Текущий VPN не изменён."; }
        };
        var close = UiFactory.Button("DiagnosticsCloseButton", "Закрыть", false, 116);
        close.Location = new Point(492, 520);
        close.Click += (_, _) => Close();
        _send = UiFactory.Button("SendDiagnosticsButton", "Отправить разработчикам", true, 218);
        _send.Location = new Point(470, 466);
        _send.Enabled = false;
        _consent.CheckedChanged += (_, _) => _send.Enabled = _prepared && _consent.Checked;
        _send.Click += async (_, _) => await SubmitAsync();

        Controls.AddRange([title, subtitle, included, excluded, _consent, _status, save, computer, close, _send]);
        Shown += async (_, _) => await PrepareAsync();
    }

    private async Task PrepareAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await _controller.CollectDiagnosticsAsync(timeout.Token);
            _prepared = result.Success;
            _status.Text = result.Success
                ? "Очищенный отчёт готов локально. Поставьте галочку, чтобы разрешить отправку."
                : "Отчёт не подготовлен: " + result.Message;
            _status.ForeColor = result.Success ? Palette.Green : Palette.Amber;
            _send.Enabled = _prepared && _consent.Checked;
        }
        catch
        {
            _status.Text = "Служба не ответила. Ничего не отправлено.";
            _status.ForeColor = Palette.Amber;
        }
    }

    private async Task SubmitAsync()
    {
        if (!_prepared || !_consent.Checked) return;
        _send.Enabled = false;
        _consent.Enabled = false;
        _status.Text = "Готовим письмо с очищенным отчётом…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = await _controller.SubmitDiagnosticsAsync(timeout.Token);
            _status.Text = result.Success
                ? result.Message
                : "Отчёт не отправлен: " + result.Message;
            _status.ForeColor = result.Success ? Palette.Green : Palette.Amber;
        }
        catch
        {
            _status.Text = "Отчёт не отправлен. Сохранённая диагностика осталась на этом компьютере.";
            _status.ForeColor = Palette.Amber;
        }
        finally
        {
            _consent.Enabled = true;
            _send.Enabled = _prepared && _consent.Checked;
        }
    }

    private void SaveLocalCopy()
    {
        try
        {
            if (!_prepared || !File.Exists(LocalReportPath))
            {
                _status.Text = "Сначала дождитесь подготовки очищенного отчёта.";
                _status.ForeColor = Palette.Amber;
                return;
            }
            using var dialog = new SaveFileDialog
            {
                Title = "Сохранить диагностику ART VPN",
                Filter = "Диагностика ART VPN (*.json)|*.json",
                FileName = $"ART-VPN-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.json",
                AddExtension = true,
                DefaultExt = "json",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var bytes = File.ReadAllBytes(LocalReportPath);
            if (bytes.Length is 0 or > 524_288) throw new InvalidDataException();
            File.WriteAllBytes(dialog.FileName, bytes);
            _status.Text = "Очищенный файл сохранён. Его можно передать разработчику вручную.";
            _status.ForeColor = Palette.Green;
        }
        catch
        {
            _status.Text = "Не удалось сохранить файл. Исходный очищенный отчёт остался на компьютере.";
            _status.ForeColor = Palette.Amber;
        }
    }
}
