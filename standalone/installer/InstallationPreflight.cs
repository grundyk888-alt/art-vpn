namespace ArtSport.ArtVpn.Setup;

internal static class InstallationPreflight
{
    internal static async Task<bool> RunAsync(IWin32Window owner, string payload, CancellationToken token,
        Func<CancellationToken, Task<InstallationReport>>? collect = null,
        Func<InstallationReport, bool>? show = null)
    {
        collect ??= ct => InstallationDiagnostics.CollectAsync(payload, ct);
        show ??= report =>
        {
            // Reuse exactly the preflight result. A repeat is only a user's action.
            using var form = new InstallationDiagnosticsForm(payload, true, initialReport: report);
            form.ShowDialog(owner);
            return form.ContinueRequested;
        };
        InstallationReport report;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            report = await Task.Run(() => collect(timeout.Token), timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception)
        {
            report = new(DateTimeOffset.UtcNow,
                [new("PreflightIncomplete", "Blocked", "Проверка не завершена",
                    "Не удалось подтвердить состояние компьютера. Повторите проверку; установка и переключение сети не начались.", true)], false);
        }
        if (token.IsCancellationRequested) return false;
        return InstallationContinuation.Automatic(report) || show(report);
    }
}
