namespace ArtSport.ArtVpn.Service;

internal static class RuntimeLifetime
{
    internal static async Task ObserveFaultAsync(Task runtime, CancellationToken stopping,
        Action<Exception> unexpectedFault)
    {
        try { await runtime.ConfigureAwait(false); }
        catch (Exception exception)
        {
            // A token remains readable after its source is disposed. Never use
            // a mutable service field to classify a previous start's fault.
            if (!stopping.IsCancellationRequested) unexpectedFault(exception);
        }
    }

    internal static async Task RunAsync(CancellationToken stopping,
        params Func<CancellationToken, Task>[] components)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var tasks = components.Select(run => InvokeAsync(run, lifetime.Token)).ToArray();
        try
        {
            var first = await Task.WhenAny(tasks).WaitAsync(stopping).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (!stopping.IsCancellationRequested)
                throw new InvalidOperationException("RuntimeComponentEndedUnexpectedly");
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch { /* Preserve the first fault for SCM recovery; do not hide it behind cleanup. */ }
        }
    }

    private static async Task InvokeAsync(Func<CancellationToken, Task> run, CancellationToken stopping) =>
        await run(stopping).ConfigureAwait(false);
}
