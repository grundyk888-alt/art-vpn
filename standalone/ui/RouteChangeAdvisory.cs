using ArtSport.Vpn.Shared;

namespace ArtSport.ArtVpn.Ui;

// This advice follows a confirmed route command. It never changes the route,
// closes Codex or converts a committed switch into an apparent failure.
internal static class RouteChangeAdvisory
{
    internal const string RestartMessage =
        "VPN переключён. Полностью закройте и снова откройте Codex для нового соединения. VPN выключать не нужно.";

    internal static async Task<UiOperationResult> CompleteAsync(UiOperationResult result,
        Func<bool> olderCodexStillRunning, Action notifyWindows,
        Func<Task<CodexProxyResult>> refreshCompatibility)
    {
        if (!result.Success) return result with { CodexRestartSuggested = false };
        var restart = false;
        CodexProxyResult compatibility;
        try { restart = olderCodexStillRunning(); }
        catch { /* Process inspection is advisory, not a route transaction. */ }
        try
        {
            notifyWindows();
            compatibility = await refreshCompatibility().ConfigureAwait(false);
        }
        catch
        {
            // No exception details, profile paths or environment contents.
            compatibility = new("CheckUnavailable", "Подхват Codex пока не проверен.");
        }
        restart |= compatibility.Code == "RestartSuggested";
        var message = result.Message;
        if (compatibility.Code != "RestartSuggested" &&
            (compatibility.Changed || compatibility.Code is not ("Ready" or "Inactive" or "CodexNotFound")))
            message += " " + compatibility.Message;
        if (restart) message += " " + RestartMessage;
        return result with { Message = message, CodexRestartSuggested = restart };
    }
}
