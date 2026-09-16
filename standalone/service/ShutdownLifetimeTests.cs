using System.Diagnostics;

namespace ArtSport.ArtVpn.Service;

internal static class ShutdownLifetimeTests
{
    public static async Task<object> RunAsync()
    {
        var cases = new List<string>();
        var checkAttempts = 0; var checkDelays = 0;
        await CoreProcessLease.CheckWithRetryAsync(_ => ++checkAttempts == 1
            ? Task.FromException(new InvalidDataException("CoreCheckTimedOut")) : Task.CompletedTask,
            _ => { checkDelays++; return Task.CompletedTask; }, CancellationToken.None);
        Assert(checkAttempts == 2 && checkDelays == 1, "TransientConfigTimeoutRetriesOnce");
        checkAttempts = checkDelays = 0;
        var permanentRejected = false;
        try
        {
            await CoreProcessLease.CheckWithRetryAsync(_ => { checkAttempts++; return Task.FromException(new InvalidDataException("CoreCheckTimedOut")); },
                _ => { checkDelays++; return Task.CompletedTask; }, CancellationToken.None);
        }
        catch (InvalidDataException ex) when (ex.Message == "CoreCheckTimedOut") { permanentRejected = true; }
        Assert(permanentRejected && checkAttempts == 2 && checkDelays == 1, "PersistentConfigTimeoutStillRejectedAfterBoundedRetry");
        checkAttempts = 0;
        try
        {
            await CoreProcessLease.CheckWithRetryAsync(_ => { checkAttempts++; return Task.FromException(new InvalidDataException("CoreConfigRejected")); },
                _ => Task.CompletedTask, CancellationToken.None);
        }
        catch (InvalidDataException ex) when (ex.Message == "CoreConfigRejected") { }
        Assert(checkAttempts == 1, "InvalidConfigNeverRetriedAsTransientTimeout");
        var stopping = new CancellationTokenSource();
        var previousToken = stopping.Token;
        var lateFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faults = 0;
        var observation = RuntimeLifetime.ObserveFaultAsync(lateFailure.Task, previousToken,
            _ => Interlocked.Increment(ref faults));
        stopping.Cancel();
        stopping.Dispose();
        using var nextStart = new CancellationTokenSource();
        lateFailure.SetException(new IOException("FixtureLateShutdownFailure"));
        await observation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(faults == 0 && !nextStart.IsCancellationRequested, "LateOldRuntimeFaultIgnoredAfterStopAndDispose");

        await RuntimeLifetime.ObserveFaultAsync(Task.FromException(new IOException("FixtureUnexpectedFault")),
            nextStart.Token, _ => Interlocked.Increment(ref faults));
        Assert(faults == 1, "UnexpectedRunningFaultStillReachesRecovery");
        await RuntimeLifetime.ObserveFaultAsync(Task.CompletedTask, nextStart.Token,
            _ => Interlocked.Increment(ref faults));
        Assert(faults == 1, "SuccessfulTaskDoesNotRequestRecovery");

        using var alreadyStopped = new CancellationTokenSource();
        alreadyStopped.Cancel();
        var rejectedBeforeValidation = false;
        try { await CoreProcessLease.CheckConfigAsync("absent.exe", "absent.json", alreadyStopped.Token); }
        catch (OperationCanceledException) { rejectedBeforeValidation = true; }
        Assert(rejectedBeforeValidation, "CancelledCheckDoesNotLaunchProcess");
        rejectedBeforeValidation = false;
        try { await CoreProcessLease.StartAsync("absent.exe", "absent.json", [24000], alreadyStopped.Token); }
        catch (OperationCanceledException) { rejectedBeforeValidation = true; }
        Assert(rejectedBeforeValidation, "CancelledStartDoesNotLaunchProcess");

        using (var child = NewChild("--job-test-child"))
        using (var cancel = new CancellationTokenSource())
        {
            var check = CoreProcessLease.CheckProcessAsync(child, TimeSpan.FromSeconds(10), cancel.Token);
            // CheckProcessAsync starts and assigns the child before its first await.
            cancel.Cancel();
            var cancellationPreserved = false;
            try { await check.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { cancellationPreserved = true; }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert(cancellationPreserved && child.HasExited, "InFlightCancellationPreservedAndChildReaped");
        }
        using (var child = NewChild("--job-test-child"))
        {
            var timedOut = false;
            try { await CoreProcessLease.CheckProcessAsync(child, TimeSpan.FromMilliseconds(250), CancellationToken.None); }
            catch (InvalidDataException exception) { timedOut = exception.Message == "CoreCheckTimedOut"; }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert(timedOut && child.HasExited, "RealTimeoutStillReportedAndChildReaped");
        }
        using (var child = NewChild("--unsupported-test-argument"))
        {
            var invalid = false;
            try { await CoreProcessLease.CheckProcessAsync(child, TimeSpan.FromSeconds(10), CancellationToken.None); }
            catch (InvalidDataException exception) { invalid = exception.Message == "CoreConfigRejected"; }
            Assert(invalid && child.HasExited, "NonZeroExitStillRejected");
        }
        return new { status = "Passed", scenarios = cases.Count, cases, productionNetworkChanged = false };

        void Assert(bool passed, string name)
        {
            if (!passed) throw new InvalidOperationException("ShutdownRegressionFailed" + name);
            cases.Add(name);
        }
    }

    private static Process NewChild(string argument)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("TestExecutableUnavailable"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(argument);
        return new Process { StartInfo = start };
    }
}
