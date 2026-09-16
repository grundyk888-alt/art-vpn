using ArtSport.Vpn.Shared;

namespace ArtSport.ArtVpn.Ui;

internal static class RouteChangeAdvisoryTests
{
    internal static async Task<int> VerifyAsync()
    {
        var checks = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("RouteChangeAdvisoryRegression"); checks++; }
        var committed = new UiOperationResult(true, "Выбранный маршрут проверен.", "SelectedRouteVerified");
        var ready = new CodexProxyResult("Ready", "Готово");
        var calls = 0;
        var result = await RouteChangeAdvisory.CompleteAsync(committed, () => true,
            () => calls++, () => Task.FromResult(ready));
        Check(result.Success && result.CodexRestartSuggested && calls == 1);
        Check(result.IncidentCode == committed.IncidentCode && result.Message.StartsWith(committed.Message));
        Check(result.Message.Contains("Полностью закройте") && result.Message.Contains("VPN выключать не нужно") && RouteChangeAdvisory.RestartMessage.Length < 150);
        Check(!result.Message.Contains("Маршрут Codex исправлен")); // No .env migration is needed to advise a restart.

        result = await RouteChangeAdvisory.CompleteAsync(committed, () => false,
            () => { }, () => Task.FromResult(ready));
        Check(result == committed); // No old client, or it was already restarted during the switch.
        result = await RouteChangeAdvisory.CompleteAsync(committed, () => false,
            () => { }, () => Task.FromResult(new CodexProxyResult("RestartSuggested", "old advice")));
        Check(result.Success && result.CodexRestartSuggested && !result.Message.Contains("old advice"));

        var failed = new UiOperationResult(false, "Переключение не подтверждено.", "UnknownOutcome", true);
        result = await RouteChangeAdvisory.CompleteAsync(failed,
            () => throw new Exception("must-not-inspect"), () => throw new Exception("must-not-notify"),
            () => throw new Exception("must-not-migrate"));
        Check(!result.Success && !result.CodexRestartSuggested && result.Message == failed.Message);

        result = await RouteChangeAdvisory.CompleteAsync(committed, () => true,
            () => throw new IOException("fixture-private-detail"), () => Task.FromResult(ready));
        Check(result.Success && result.CodexRestartSuggested && result.IncidentCode == committed.IncidentCode);
        Check(!result.Message.Contains("fixture-private-detail"));
        result = await RouteChangeAdvisory.CompleteAsync(committed, () => true, () => { },
            () => Task.FromException<CodexProxyResult>(new OperationCanceledException("fixture-private-detail")));
        Check(result.Success && result.CodexRestartSuggested && result.Message.Contains("пока не проверен"));

        result = await RouteChangeAdvisory.CompleteAsync(committed, () => true, () => { },
            () => Task.FromResult(new CodexProxyResult("CustomOverride", "Отдельные настройки сохранены.")));
        Check(result.Success && result.CodexRestartSuggested && result.Message.Contains("Отдельные настройки сохранены."));
        result = await RouteChangeAdvisory.CompleteAsync(committed, () => throw new IOException(), () => { },
            () => Task.FromResult(ready));
        Check(result == committed); // Inspect failure must not break an already committed VPN command.
        return checks;
    }
}
