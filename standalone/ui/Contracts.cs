using System.Security;

namespace ArtSport.ArtVpn.Ui;

internal enum VpnHealth { Healthy, Checking, Fallback, Stopped, Unavailable }

internal sealed record NodeView(string Role, string Country, string Label, string Quality);

internal sealed record VpnViewState(
    VpnHealth Health,
    string Headline,
    string Explanation,
    string Mode,
    string Country,
    DateTimeOffset? SelectedAt,
    string Quality,
    string Latency,
    string Stability,
    string LastSwitch,
    string LastSwitchReason,
    NodeView[] Nodes,
    string Subscription,
    string SubscriptionAt,
    string Bypass,
    string Update,
    string Version,
    bool ServiceAvailable,
    bool SafeRepairAvailable,
    string CodexRouteStatus = "",
    string WindowsRouteStatus = "Подключение Windows ещё не проверено.",
    bool? WindowsUsesArtVpn = null,
    string ConfirmedRouteMode = "",
    string ActiveChannel = "Не подтверждён",
    bool ActiveChannelVerified = false);

internal sealed record UiOperationResult(bool Success, string Message, string IncidentCode = "",
    bool CodexRestartSuggested = false);

internal interface IArtVpnUiController
{
    Task<VpnViewState> GetStatusAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> CheckNowAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> CheckUpdatesAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> RefreshSubscriptionAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> CollectDiagnosticsAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> SubmitDiagnosticsAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> SetAutoAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> SetModeAsync(string mode, CancellationToken cancellationToken);
    Task<UiOperationResult> EnableSystemProxyAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> RestoreSystemProxyAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> AcquireClientAsync(string client, CancellationToken cancellationToken);
    Task<UiOperationResult> ConfigureProviderAsync(SecureString first, SecureString confirmation,
        CancellationToken cancellationToken);
}

internal interface ISetupLauncher
{
    void Open(IWin32Window owner, IArtVpnUiController controller);
}

internal sealed class ProductionSetupLauncher : ISetupLauncher
{
    public void Open(IWin32Window owner, IArtVpnUiController controller)
    {
        using var wizard = new FirstRunWizard(controller);
        wizard.ShowDialog(owner);
        if (wizard.DialogResult == DialogResult.OK && owner is MainForm main)
            main.ShowSetupCompletion(wizard.CompletedMessage, wizard.CompletedCodexRestartSuggested);
    }
}
