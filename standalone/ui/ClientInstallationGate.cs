namespace ArtSport.ArtVpn.Ui;

// A window may stop waiting, but it does not own Windows' installer lifetime.
// Keep the operation (and its verified file lease) alive until setup actually
// exits. Another wizard in this UI process must not start a duplicate installer.
internal sealed class ClientInstallationGate
{
    private readonly object _sync = new();
    private Task<UiOperationResult>? _active;

    public async Task<UiOperationResult> RunAsync(Func<CancellationToken, Task<UiOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(false, "Установка отменена до запуска. Текущий VPN не изменён.");
        Task<UiOperationResult> task;
        lock (_sync)
        {
            if (_active is { IsCompleted: false })
                return new(false, "Установка HAPP уже идёт. Дождитесь окна Windows; повторный установщик не запущен.");
            task = _active = Task.Run(() => operation(cancellationToken), cancellationToken);
            // Also observe a late fault after the window has stopped waiting.
            _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        try { return await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            return new(false, "Ожидание установки завершено. Если установщик Windows уже открыт, он продолжает работу; повторно запускать его не нужно.");
        }
        catch
        {
            return new(false, "Установка HAPP не подтверждена. Проверьте окно Windows и повторите поиск клиента.");
        }
    }
}

internal static class ClientInstallationGateTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string code)
        {
            if (!condition) throw new InvalidOperationException(code);
            checks++;
        }
        var gate = new ClientInstallationGate();
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<UiOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var result = await gate.RunAsync(_ => { calls++; return Task.FromResult(new UiOperationResult(true, "fixture")); },
            canceled.Token).ConfigureAwait(false);
        Check(!result.Success && calls == 0, "CanceledInstallationMustNotStart");
        using var waiting = new CancellationTokenSource();
        var first = gate.RunAsync(_ => { Interlocked.Increment(ref calls); started.SetResult(); return completion.Task; }, waiting.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        result = await gate.RunAsync(_ => { Interlocked.Increment(ref calls); return Task.FromResult(new UiOperationResult(true, "unexpected")); },
            CancellationToken.None).ConfigureAwait(false);
        Check(!result.Success && calls == 1, "ParallelInstallerMustBeRejected");
        waiting.Cancel();
        result = await first.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(!result.Success && !completion.Task.IsCompleted, "CancelWaitMustNotEndWindowsInstallation");
        result = await gate.RunAsync(_ => { Interlocked.Increment(ref calls); return Task.FromResult(new UiOperationResult(true, "unexpected")); },
            CancellationToken.None).ConfigureAwait(false);
        Check(!result.Success && calls == 1, "InstallerGateMustSurviveClosingWizard");
        completion.SetResult(new(true, "fixture"));
        // Completion of the operation, not cancellation of its waiter, releases the gate.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            result = await gate.RunAsync(_ => Task.FromResult(new UiOperationResult(true, "recovered")), CancellationToken.None).ConfigureAwait(false);
            if (result.Success) break;
            await Task.Delay(5).ConfigureAwait(false);
        }
        Check(result.Success, "CompletedInstallerMustReleaseGate");
        result = await gate.RunAsync(_ => Task.FromException<UiOperationResult>(new IOException("FixtureOnly")), CancellationToken.None).ConfigureAwait(false);
        Check(!result.Success, "InstallerFaultMustNotEscapeToWindow");
        result = await gate.RunAsync(_ => Task.FromResult(new UiOperationResult(true, "retry")), CancellationToken.None).ConfigureAwait(false);
        Check(result.Success, "InstallerFaultMustAllowRetry");
        return checks;
    }
}
