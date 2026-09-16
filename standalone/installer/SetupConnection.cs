using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using ArtSport.ArtVpn.Common;
using ArtSport.Vpn.Shared;

namespace ArtSport.ArtVpn.Setup;

internal interface ISetupWorkflow
{
    Task<bool> DiagnoseAsync(IWin32Window owner, string payload, CancellationToken token);
    Task<bool> InstallAsync(string payload, string ownerSid);
    Task<ProviderConnectionResult> ConnectAsync(SecureString subscription, CancellationToken token);
    Task<string> CompleteConnectionAsync();
    void ShowAdvisory(IWin32Window owner, string message);
    void ShowFailureDiagnostics(IWin32Window owner, string payload, string failure);
    void OpenUi();
}

internal sealed class SetupWorkflow : ISetupWorkflow
{
    private CodexCloseTarget[] _codexBeforeConnect = [];
    public Task<bool> DiagnoseAsync(IWin32Window owner, string payload, CancellationToken token) =>
        InstallationPreflight.RunAsync(owner, payload, token);

    public void ShowFailureDiagnostics(IWin32Window owner, string payload, string failure)
    {
        using var dialog = new InstallationDiagnosticsForm(payload, false, failureContext: failure);
        dialog.ShowDialog(owner);
    }

    public async Task<bool> InstallAsync(string payload, string ownerSid)
    {
        if (InstallTransaction.IsAdministrator())
            return (await Task.Run(() => InstallTransaction.InstallAsync(payload, ownerSid, CancellationToken.None))).Status == "Installed";
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("InstallerPathUnavailable");
        using var elevated = await Task.Run(() => Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas", Arguments = "--install-owner " + ownerSid,
            WorkingDirectory = AppContext.BaseDirectory
        })) ?? throw new InvalidOperationException("ElevationStartRejected");
        await elevated.WaitForExitAsync();
        return elevated.ExitCode == 0;
    }

    public Task<ProviderConnectionResult> ConnectAsync(SecureString subscription, CancellationToken token)
    {
        _codexBeforeConnect = CodexGracefulClose.Capture();
        return ProviderConnectionClient.ConnectAsync(subscription, subscription, token);
    }

    public async Task<string> CompleteConnectionAsync()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        var compatibility = await Task.Run(() => CodexProxyCompatibility.RefreshCurrentUser(true));
        return IsCodexRunning()
            ? "VPN подключён. Переоткройте Codex для нового соединения."
            : compatibility.Code is "Ready" or "Migrated" or "CodexNotFound" ? "" : compatibility.Message;
    }

    internal static bool IsCodexRunning() => CodexClientProcesses.IsRunningInCurrentSession();

    public void OpenUi()
    {
        const string ui = @"C:\Program Files\ART VPN\runtime\ARTVpn.UI.exe";
        if (!File.Exists(ui)) throw new FileNotFoundException("InstalledUiMissing");
        Process.Start(new ProcessStartInfo(ui) { UseShellExecute = true });
    }

    public void ShowAdvisory(IWin32Window owner, string message)
    {
        if (_codexBeforeConnect.Any(CodexGracefulClose.IsStillTarget))
        {
            using var dialog = new CodexCloseCountdownForm(() => CodexGracefulClose.RequestAsync(_codexBeforeConnect));
            dialog.ShowDialog(owner);
        }
        else MessageBox.Show(owner, message, "ART VPN подключён", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    [DllImport("wininet.dll")]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);
}

internal static class SetupConnectionText
{
    internal static bool Valid(string text) => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && !uri.IsLoopback && text.Length <= 8192;

    internal static string Failure(string code) => code switch
    {
        "ExternalTunnelOwnsRoute" => "ART VPN установлен, но другой VPN удерживает TUN. Переключение не выполнено; текущее подключение сохранено.",
        "ProviderValueRejected" => "Проверьте ссылку подписки: нужен полный HTTPS-адрес.",
        "ProviderSubscriptionExpired" => "Провайдер сообщил, что подписка закончилась. Продлите её и повторите подключение.",
        "ProviderSubscriptionQuotaExceeded" => "По данным провайдера закончился трафик подписки. Проверьте её лимит.",
        "ProviderSubscriptionAccessRejected" or "ProviderSubscriptionLinkRejected" => "Провайдер не принял ссылку. Проверьте её в личном кабинете.",
        "QualifiedPoolEmpty" or "PrefilterEmpty" => "Не найден канал, прошедший проверку связи. Прежнее подключение не заменено.",
        "ControllerResultTimedOut" or "FirstConnectionTimedOut" => "Нет подтверждения подключения за время проверки. Проверьте текущее состояние ART VPN перед повтором.",
        "ServiceIdentityRejected" => "Не удалось подтвердить защищённую службу. Ссылка ей не передана.",
        "ServiceUnavailable" => "Служба не подтвердила операцию. Проверьте состояние ART VPN перед повтором.",
        _ => "Подключение не подтверждено. Откройте ART VPN и проверьте состояние канала; установка завершена."
    };
}
