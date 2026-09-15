using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Ui;

internal interface IClientInstallerLauncher
{
    Task<bool> IsHappInstalledAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> InstallHappAsync(CancellationToken cancellationToken);
}

internal sealed class ClientInstallerLauncher : IClientInstallerLauncher
{
    private static readonly ClientInstallationGate Installations = new();
    public Task<bool> IsHappInstalledAsync(CancellationToken cancellationToken) =>
        // Local process/file metadata can be slow too. Never scan on the window
        // thread, and let closing the wizard stop waiting without killing a client.
        Task.Run(() => ClientDiscovery.Snapshot().Any(item => item.Product == "HAPP" && item.Found),
            cancellationToken).WaitAsync(cancellationToken);

    public Task<UiOperationResult> InstallHappAsync(CancellationToken cancellationToken) =>
        Installations.RunAsync(InstallCoreAsync, cancellationToken);

    private async Task<UiOperationResult> InstallCoreAsync(CancellationToken cancellationToken)
    {
        // Never reinstall/downgrade an existing external client or claim it is a
        // verified VPN reserve merely because its executable is present.
        var path = AcceptedClientRelease.HappInstallerPath(@"C:\ProgramData\ART VPN");
        try
        {
            if (await IsHappInstalledAsync(cancellationToken).ConfigureAwait(false))
                return new(true, "HAPP уже установлен. Его настройки сохранены; подключение резерва ещё нужно проверить.");
            EnsureNoLinks(path);
            // Keep the exact accepted file locked against modification/deletion
            // until setup exits. No arbitrary path/arguments come from IPC.
            using var verifiedFile = await Task.Run(() => OpenVerifiedInstaller(path), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using var setup = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /NOFORCECLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /RESTARTEXITCODE=3010 /SP-",
                WorkingDirectory = Path.GetDirectoryName(path)!
            });
            if (setup is null) return new(false, "Windows не запустила установщик HAPP. Можно открыть официальный сайт.");
            // Closing a window cancels its wait, not this Windows operation.
            // The gate and exact-file lock stay held until setup really exits.
            await setup.WaitForExitAsync().ConfigureAwait(false);
            using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var found = await IsHappInstalledAsync(verification.Token).ConfigureAwait(false);
            if (setup.ExitCode == 3010 && found)
                return new(false, "HAPP установлен, но Windows сообщает о необходимости перезагрузки. ART VPN её не выполняет; резерв пока не готов.");
            return setup.ExitCode == 0 && found
                ? new(true, "HAPP установлен. Подписка и подключение резерва ещё не настроены.")
                : new(false, "Установка HAPP не подтверждена. Текущий VPN не переключался; повторите поиск или откройте официальный сайт.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return new(false, "Установка HAPP отменена в окне Windows. Скачанный файл сохранён — можно повторить.");
        }
        catch (OperationCanceledException)
        {
            // Setup belongs to Windows, not our watchdog: do not forcibly kill
            // it and risk leaving an external application half-installed.
            return new(false, "Установка HAPP ещё не подтверждена. Проверьте открытое окно Windows, затем повторите поиск клиента.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return new(false, "Не удалось безопасно запустить установщик HAPP. Используйте ссылку на официальный сайт.");
        }
    }

    private static FileStream OpenVerifiedInstaller(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length != AcceptedClientRelease.HappBytes ||
                Convert.ToHexString(SHA256.HashData(file)) != AcceptedClientRelease.HappSha256)
                throw new IOException("HappInstallerHashRejected");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("ClientInstallerLinkRejected");
    }
}
