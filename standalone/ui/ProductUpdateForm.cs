using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace ArtSport.ArtVpn.Ui;

internal sealed class ProductUpdateForm : Form
{
    private readonly string _directory;
    private readonly Label _message;
    private readonly Button _cancel;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromMinutes(20));
    private bool _installing;
    private bool _running = true;
    private bool _closeRequested;
    internal bool Succeeded { get; private set; }

    internal ProductUpdateForm(string directory)
    {
        _directory = directory;
        Text = "Обновление ART VPN 1.0";
        ClientSize = new Size(560, 210); BackColor = Color.FromArgb(246, 248, 252);
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        Controls.Add(new Label { Text = "Обновление ART VPN", Font = new Font("Segoe UI", 18), AutoSize = true, Location = new Point(24, 20) });
        _message = new Label { Text = "Проверяем обновление…", Font = new Font("Segoe UI", 11), Location = new Point(24, 65), Size = new Size(508, 75) };
        Controls.Add(_message);
        _cancel = new Button { Text = "Отмена", Location = new Point(410, 158), Size = new Size(120, 32) };
        _cancel.Click += (_, _) => { if (!_installing) Close(); };
        Controls.Add(_cancel);
        FormClosing += (_, e) =>
        {
            if (_installing) { e.Cancel = true; return; }
            if (_running) { _closeRequested = true; e.Cancel = true; _stop.Cancel(); }
        };
        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        try
        {
            var result = await new ServiceClient().CheckUpdatesAsync(token);
            if (!result.Success || result.IncidentCode != "SignedUpdateAvailable") throw new InvalidDataException("UpdateNotAvailable");
            var installJson = await File.ReadAllTextAsync(Path.Combine(UpdateDownloadLink.StateRoot, "install.v1.json"), token);
            var update = UpdatePackageTransfer.Verify(
                await File.ReadAllTextAsync(Path.Combine(UpdateDownloadLink.StateRoot, "update-check.v1.json"), token),
                await File.ReadAllTextAsync(Path.Combine(UpdateDownloadLink.InstallRoot, "runtime", "update", "channel.v1.json"), token),
                installJson, DateTimeOffset.UtcNow);
            using var install = JsonDocument.Parse(installJson);
            if (new DriveInfo(Path.GetPathRoot(_directory)!).AvailableFreeSpace < 1_500_000_000)
                throw new IOException("UpdateSpaceInsufficient");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ART-VPN-Update/1");
            var progress = new Progress<string>(text => { if (!IsDisposed) _message.Text = text; });
            var exe = await UpdatePackageTransfer.DownloadAsync(client, update, _directory, progress, token);
            token.ThrowIfCancellationRequested();
            _message.Text = "Установка обновления…\nПодписка и настройки сохраняются. Связь может кратко прерваться.";
            _installing = true; _cancel.Enabled = false;
            using (var executableLock = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = _directory };
                start.ArgumentList.Add("--update-owner");
                start.ArgumentList.Add(install.RootElement.GetProperty("ownerSid").GetString()!);
                start.ArgumentList.Add(update.Version);
                start.ArgumentList.Add(update.ManifestSha256);
                using var setup = Process.Start(start) ?? throw new IOException("UpdateInstallerNotStarted");
                // Do not cancel/kill a running installer on the download timeout.
                await setup.WaitForExitAsync(CancellationToken.None);
                if (setup.ExitCode != 0) throw new IOException("UpdateInstallerFailed");
            }
            using var after = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(UpdateDownloadLink.StateRoot, "install.v1.json")));
            if (after.RootElement.GetProperty("version").GetString() != update.Version ||
                after.RootElement.GetProperty("installId").GetString() != install.RootElement.GetProperty("installId").GetString())
                throw new IOException("UpdateResultUnconfirmed");
            _message.Text = "Обновление установлено. Открываем ART VPN…";
            using var ui = Process.Start(new ProcessStartInfo(Path.Combine(UpdateDownloadLink.InstallRoot, "runtime", "ARTVpn.UI.exe")) { UseShellExecute = false });
            Succeeded = ui is not null;
            if (!Succeeded) _message.Text = "Обновление установлено. Откройте ART VPN.";
        }
        catch (OperationCanceledException) { if (!IsDisposed) _message.Text = "Загрузка отменена. Текущий VPN не изменён."; }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        { if (!IsDisposed) _message.Text = "Установка отменена в Windows. Текущая версия сохранена."; }
        catch
        {
            if (!IsDisposed) _message.Text = _installing
                ? "Обновление не подтверждено. Откройте ART VPN и проверьте связь. Подписку повторно вводить не нужно."
                : "Не удалось загрузить проверенное обновление. Текущая версия сохранена. Попробуйте позже.";
        }
        finally
        {
            _installing = false;
            _running = false;
            UpdatePackageTransfer.TryDelete(Path.Combine(_directory, "package.zip"));
            UpdatePackageTransfer.TryDelete(Path.Combine(_directory, "ART-VPN-Setup.exe"));
            try { File.WriteAllText(Path.Combine(_directory, "completed"), "1"); } catch (IOException) { }
            if (!IsDisposed) { _cancel.Text = "Закрыть"; _cancel.Enabled = true; if (Succeeded || _closeRequested) Close(); }
        }
    }
}
