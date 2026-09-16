using System.Security;
using System.Text.Json;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Setup;

internal static class SetupConnectionTests
{
    internal static int Run(Action<string>? trace = null)
    {
        var count = 0;
        void Check(bool condition, string code) { if (!condition) throw new InvalidDataException(code); count++; }
        var formerHapp = new SystemProxySnapshot(1, "127.0.0.1:10809", "<local>", null);
        var artProxy = formerHapp with { ProxyServer = "127.0.0.1:22080" };
        var lease = new SystemProxyLease(1, "art-vpn-system-proxy-lease", "S-1-5-21-111-222-333-1001", "Applied",
            formerHapp, artProxy, DateTimeOffset.UtcNow.ToString("o"), DateTimeOffset.UtcNow.ToString("o"), false);
        Check(UninstallRouteReturn.Needed(lease, artProxy), "UninstallMustReturnNativeHapp");
        Check(UninstallRouteReturn.Needed(lease, artProxy with { ProxyOverride = "new.local" }), "UninstallMustPreserveNewBypass");
        Check(!UninstallRouteReturn.Needed(null, artProxy), "UninstallInventedNativeOwnership");
        Check(!UninstallRouteReturn.Needed(lease, artProxy with { ProxyServer = "127.0.0.1:39001" }), "UninstallChangedForeignChoice");
        Check(!UninstallRouteReturn.Needed(lease with { Previous = formerHapp with { ProxyEnable = 0 } }, artProxy), "UninstallInventedPreviousHapp");
        Check(SetupConnectionText.Valid("https://provider.example/subscription"), "HttpsRejected");
        foreach (var name in new[] { "codex", "Codex", "ChatGPT", "chatgpt" })
            Check(ArtSport.Vpn.Shared.CodexClientProcesses.Matches(name, 1, 1, false), "RenamedClientNotDetected");
        foreach (var name in new[] { "codex", "ChatGPT" })
        {
            Check(!ArtSport.Vpn.Shared.CodexClientProcesses.Matches(name, 2, 1, false), "OtherUserSessionDetected");
            Check(!ArtSport.Vpn.Shared.CodexClientProcesses.Matches(name, 1, 1, true), "ExitedClientDetected");
        }
        Check(!ArtSport.Vpn.Shared.CodexClientProcesses.Matches("chrome", 1, 1, false), "UnrelatedAppDetected");
        foreach (var code in new[] { "Ready", "NotConfigured", "CodexNotFound", "ExternalRoute", "RouteUpdateSuggested", "RestartSuggested" })
            Check(!InstallationContinuation.CodexNeedsAction(code), "BenignCodexCheckStoppedInstallation");
        foreach (var code in new[] { "CustomOverride", "EnvironmentOverride", "UnsafeFile", "PacConflict", "UnknownFutureCode" })
            Check(InstallationContinuation.CodexNeedsAction(code), "CodexConflictSilentlyIgnored");
        foreach (var bad in new[] { "", "http://provider.example/key", "https://user:pass@provider.example/", "https://localhost/key" })
            Check(!SetupConnectionText.Valid(bad), "InvalidSubscriptionAccepted");
        var id = Guid.NewGuid().ToString("N");
        string Ack(bool accepted, string code, string? request = null, bool secret = false) => JsonSerializer.Serialize(new
        { schema = 1, kind = "art-vpn-provider-response", accepted, detailCode = code, requestId = request, secretDisplayed = secret });
        Check(ProviderConnectionClient.ParseAcknowledgement(Ack(true, "ProviderConnectQueued", id)).RequestId == id, "AckRejected");
        Check(ProviderConnectionClient.ParseAcknowledgement(Ack(true, "ProviderSaved", id)).RequestId is null, "SaveOnlyConnected");
        Check(ProviderConnectionClient.ParseAcknowledgement(Ack(true, "ProviderConnectQueued", "../bad")).RequestId is null, "RequestPathAccepted");
        Check(ProviderConnectionClient.ParseAcknowledgement(Ack(true, "ProviderConnectQueued", id, true)).RequestId is null, "SecretReceiptAccepted");
        Check(ProviderConnectionClient.ParseAcknowledgement(Ack(false, "ProviderValueRejected")).Code == "ProviderValueRejected", "FailureLost");
        string Done(string request, string status, string code, bool secret = false) => JsonSerializer.Serialize(new
        { schema = 1, kind = "art-vpn-controller-result", requestId = request, status, detailCode = code, containsProviderSecret = secret });
        Check(ProviderConnectionClient.ParseCompletion(Done(id, "Completed", "ProviderConnectedAuto"), id).Success, "CommitRejected");
        Check(!ProviderConnectionClient.ParseCompletion(Done(id, "Completed", "ProviderGenerationQualifiedAndActivated"), id).Success, "PreparedClaimedConnected");
        Check(!ProviderConnectionClient.ParseCompletion(Done(Guid.NewGuid().ToString("N"), "Completed", "ProviderConnectedAuto"), id).Success, "WrongRequestAccepted");
        Check(!ProviderConnectionClient.ParseCompletion(Done(id, "Deferred", "ProviderConnectedAuto"), id).Success, "DeferredClaimedConnected");

        foreach (var scenario in new[] { "success", "later", "empty", "whitespace", "invalid", "blocked", "install-failed", "connection-failed", "retry", "busy" })
        {
            trace?.Invoke("scenario:" + scenario);
            var flow = new FakeWorkflow { Diagnostic = scenario != "blocked", Installed = scenario != "install-failed",
                Connected = scenario is not ("connection-failed" or "retry") };
            if (scenario == "busy") flow.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var form = new SetupForm(Path.Combine(Path.GetTempPath(), "no-real-payload"), _ => { }, flow);
            form.StartPosition = FormStartPosition.Manual; form.Location = new(-20000, -20000); form.ShowInTaskbar = false;
            form.Show(); Application.DoEvents();
            var field = Find<TextBox>(form, "SetupSubscriptionBox");
            var later = Find<CheckBox>(form, "SetupConfigureLater");
            later.Checked = scenario == "later";
            field.Text = scenario switch { "invalid" => "invalid", "empty" => "", "whitespace" => "   ", _ => "https://provider.example/subscription" };
            Check(field.UseSystemPasswordChar, "SubscriptionNotMasked");
            var button = Find<Button>(form, "InstallButton");
            button.PerformClick(); Application.DoEvents();
            if (scenario == "busy")
            {
                button.PerformClick();
                Check(flow.Installs == 0 && flow.Diagnoses == 1 && !button.Enabled, "DuplicateInstallation");
                flow.Pending!.SetResult(true);
            }
            Pump(form, button);
            if (scenario == "invalid") Check(flow.Diagnoses == 0, "InvalidInputChangedSystem");
            else if (scenario == "blocked") Check(flow.Installs == 0 && flow.Connects == 0, "BlockedInstallContinued");
            else if (scenario == "install-failed") Check(flow.Connects == 0 && flow.Opens == 0, "FailedInstallConnected");
            else if (scenario is "connection-failed" or "retry")
            {
                Check(flow.Opens == 0 && form.Visible && form.ConnectionCode == "QualifiedPoolEmpty", "FailureHidden");
                if (scenario == "retry")
                {
                    flow.Connected = true; button.PerformClick(); Pump(form, button);
                    Check(flow.Installs == 1 && flow.Connects == 2 && flow.Opens == 1, "RetryReinstalled");
                }
            }
            else Check(flow.Installs == 1 && flow.Connects == (scenario is "later" or "empty" or "whitespace" ? 0 : 1) && flow.Opens == 1, "OneClickFlowFailed");
            Check(flow.Advice == (scenario is "success" or "retry" or "busy" ? 1 : 0), "AdvisoryBeforeConnection");
            Check(flow.FailureDiagnostics == (scenario is "install-failed" or "connection-failed" or "retry" ? 1 : 0), "FailureDiagnosticsNotExactlyOnce");
            if (!form.IsDisposed) form.Close();
        }
        trace?.Invoke("installer-progress");
        count += InstallerProgressTests.Run(trace);
        trace?.Invoke("process-page");
        count += SetupProcessPageTests.Run(trace);
        trace?.Invoke("complete");
        return count;
    }

    private static T Find<T>(Control root, string name) where T : Control => (T)root.Controls.Find(name, true).Single();
    private static void Pump(Form form, Button button)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!form.IsDisposed && !button.Enabled && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        if (!form.IsDisposed && !button.Enabled) throw new TimeoutException("FixtureDidNotFinish");
    }

    internal sealed class FakeWorkflow : ISetupWorkflow
    {
        internal bool Diagnostic = true, Installed = true, Connected = true;
        internal int Diagnoses, Installs, Connects, Opens, Advice, FailureDiagnostics;
        internal TaskCompletionSource<bool>? Pending;
        internal TaskCompletionSource<bool>? PendingInstall;
        internal TaskCompletionSource<ProviderConnectionResult>? PendingConnect;
        internal bool FailOpen, FailAdvisory;
        internal string AdvisoryText = "";
        public Task<bool> DiagnoseAsync(IWin32Window owner, string payload, CancellationToken token) { Diagnoses++; return Pending?.Task ?? Task.FromResult(Diagnostic); }
        public Task<bool> InstallAsync(string payload, string owner) { Installs++; return PendingInstall?.Task ?? Task.FromResult(Installed); }
        public Task<ProviderConnectionResult> ConnectAsync(SecureString subscription, CancellationToken token)
        {
            if (subscription.Length == 0) throw new InvalidDataException("SubscriptionLost");
            Connects++; return PendingConnect?.Task ?? Task.FromResult(new ProviderConnectionResult(Connected, Connected ? "ProviderConnectedAuto" : "QualifiedPoolEmpty"));
        }
        public Task<string> CompleteConnectionAsync() { Advice++; return Task.FromResult(AdvisoryText); }
        public void ShowAdvisory(IWin32Window owner, string message) { if (FailAdvisory) throw new IOException("FixtureAdvisory"); }
        public void OpenUi() { Opens++; if (FailOpen) throw new IOException("FixtureOpen"); }
        public void ShowFailureDiagnostics(IWin32Window owner, string payload, string failure) { FailureDiagnostics++; }
    }
}
