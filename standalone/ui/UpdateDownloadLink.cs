using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Ui;

internal static class UpdateDownloadLink
{
    public static void Offer(IWin32Window owner)
    {
        try
        {
            // Receipt is produced by the protected service after RSA-PSS verification.
            using var json = JsonDocument.Parse(File.ReadAllText(@"C:\ProgramData\ART VPN\state\update-check.v1.json"));
            var root = json.RootElement;
            if (root.GetProperty("status").GetString() != "Available" ||
                !DateTimeOffset.TryParse(root.GetProperty("checkedAtUtc").GetString(), out var checkedAt) ||
                DateTimeOffset.UtcNow - checkedAt > TimeSpan.FromHours(1) || checkedAt > DateTimeOffset.UtcNow.AddMinutes(5)) return;
            if (!Uri.TryCreate(root.GetProperty("packageUri").GetString(), UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || !uri.IsDefaultPort || uri.Host != "drive.google.com" ||
                uri.UserInfo.Length != 0 || !Regex.IsMatch(uri.AbsolutePath, "^/file/d/[A-Za-z0-9_-]{15,}/view$"))
                throw new InvalidDataException();
            var version = root.GetProperty("availableVersion").GetString();
            if (MessageBox.Show(owner, "Доступна версия " + version + ".\n\nОткрыть проверенную ссылку на ZIP в Google Диске?\nПосле скачивания запустите установщик — подписка и настройки сохранятся.\n\nУстановку лучше выполнять после завершения удалённой работы: обновление фоновой службы может потребовать переподключения.",
                    "Обновление ART VPN", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show(owner, "Ссылка обновления не подтверждена. Повторите проверку позже; действующий VPN не изменён.",
                "ART VPN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
