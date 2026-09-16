namespace ArtSport.ArtVpn.Setup;

internal static class CodexRestartTests
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string code) { if (!condition) throw new InvalidDataException(code); checks++; }
        var events = new List<string>();
        var alive = true;
        var result = await CodexRestartSequence.RunAsync(() => { events.Add("close"); return true; }, () => alive,
            () => false, () => { events.Add("launch"); return true; }, () => { events.Add("wait"); alive = false; return Task.CompletedTask; });
        Check(events.SequenceEqual(new[] { "close", "wait", "launch" }) && result == CodexGracefulClose.RestartedMessage, "RestartMustWaitForOldExit");
        var launches = 0; var waits = 0;
        result = await CodexRestartSequence.RunAsync(() => true, () => true, () => false,
            () => { launches++; return true; }, () => { waits++; return Task.CompletedTask; });
        Check(launches == 0 && waits == 60 && result.Contains("не закрылся"), "HungOldClientMustNotStartDuplicate");
        result = await CodexRestartSequence.RunAsync(() => false, () => false, () => false,
            () => { launches++; return true; }, () => Task.CompletedTask);
        Check(launches == 0 && result.Contains("вручную"), "AlreadyClosedClientMustNotBeReopenedUnexpectedly");
        result = await CodexRestartSequence.RunAsync(() => true, () => false, () => true,
            () => { launches++; return true; }, () => Task.CompletedTask);
        Check(launches == 0 && result.Contains("уже открыт"), "UserReopenedClientMustNotBeDuplicated");
        result = await CodexRestartSequence.RunAsync(() => true, () => false, () => false, () => false);
        Check(result.Contains("VPN подключён") && result.Contains("вручную"), "LaunchFailureMustNotBecomeVpnFailure");
        Check(CodexAppLauncher.ValidApplicationId("OpenAI.ChatGPT_12345!ChatGPT"), "ValidPackagedAppIdRejected");
        foreach (var bad in new[] { "", "http://example.com", "app --flag", "app;command", "app\ncommand" })
            Check(!CodexAppLauncher.ValidApplicationId(bad), "UnsafeAppIdAccepted");
        Check(CodexAppLauncher.ValidExecutable(@"C:\Program Files\OpenAI\ChatGPT.exe"), "CapturedGuiPathRejected");
        foreach (var bad in new[] { "ChatGPT.exe", @"\\server\share\ChatGPT.exe", @"C:\cmd.exe", @"C:\Codex.exe --flag" })
            Check(!CodexAppLauncher.ValidExecutable(bad), "UnsafeLaunchPathAccepted");
        var ticks = DateTime.UtcNow.Ticks;
        var target = new CodexCloseTarget(12, ticks, 3, new CodexLaunchTarget(@"C:\Codex.exe", "Example!App"));
        var time = new DateTime(ticks, DateTimeKind.Utc).ToFileTimeUtc();
        Check(CodexRestartManager.SafeAffected(target, 12, time, 3, 3, 1, ""), "ExactGuiShutdownRejected");
        Check(!CodexRestartManager.SafeAffected(target, 13, time, 3, 3, 1, ""), "OtherPidShutdownAllowed");
        Check(!CodexRestartManager.SafeAffected(target, 12, time + 1, 3, 3, 1, ""), "ReusedPidShutdownAllowed");
        Check(!CodexRestartManager.SafeAffected(target, 12, time, 4, 3, 1, ""), "OtherSessionShutdownAllowed");
        Check(!CodexRestartManager.SafeAffected(target, 12, time, 3, 4, 1, ""), "CallerSessionMismatchAllowed");
        foreach (var type in new[] { 0, 3, 4, 1000 })
            Check(!CodexRestartManager.SafeAffected(target, 12, time, 3, 3, type, ""), "NonGuiShutdownAllowed");
        Check(!CodexRestartManager.SafeAffected(target, 12, time, 3, 3, 1, "service"), "ServiceShutdownAllowed");
        return checks;
    }
}
