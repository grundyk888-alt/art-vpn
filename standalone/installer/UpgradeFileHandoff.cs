using System.Diagnostics;

namespace ArtSport.ArtVpn.Setup;

internal static class UpgradeFileHandoff
{
    // A terminated process can briefly retain its image/working-directory lock.
    // Never unlock foreign handles, kill foreign processes, or weaken ACLs.
    internal static async Task MoveAsync(string source, string destination, CancellationToken cancellationToken,
        int budgetMs = 5_000)
    {
        var elapsed = Stopwatch.StartNew();
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ++attempts;
            try { Directory.Move(source, destination); return; }
            catch (Exception error) when (IsTemporaryFileAccess(error))
            {
                if (elapsed.ElapsedMilliseconds >= budgetMs)
                {
                    var failure = new IOException("UpgradeFilesBusy", error);
                    failure.Data["RenameAttempts"] = attempts;
                    failure.Data["NativeHResult"] = $"0x{error.HResult:X8}";
                    throw failure;
                }
                await Task.Delay(Math.Min(200, Math.Max(1, budgetMs - (int)elapsed.ElapsedMilliseconds)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsTemporaryFileAccess(Exception error) =>
        error is IOException or UnauthorizedAccessException && (error.HResult & 0xffff) is 5 or 32 or 33;

    internal static async Task<string[]> TestAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ARTVpn-UpgradeFileQA-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        try
        {
            string Make(string name)
            {
                var directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "fixture.txt"), "fixture");
                return directory;
            }
            var normal = Make("normal");
            await MoveAsync(normal, normal + "-moved", CancellationToken.None);
            checks.Add("UnlockedDirectoryMoved");
            var transient = Make("transient");
            using (var held = new FileStream(Path.Combine(transient, "fixture.txt"), FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite))
            {
                // First prove the real Windows lock rejects a parent rename.
                try { Directory.Move(transient, transient + "-moved"); throw new InvalidOperationException("LockFixtureDidNotLock"); }
                catch (Exception error) when (IsTemporaryFileAccess(error)) { }
                var release = Task.Run(async () => { await Task.Delay(300); held.Dispose(); });
                await MoveAsync(transient, transient + "-moved", CancellationToken.None, 3_000);
                await release;
            }
            checks.Add("RealTransientWindowsLockRecovered");
            var permanent = Make("permanent");
            using (var held = new FileStream(Path.Combine(permanent, "fixture.txt"), FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite))
            {
                try { await MoveAsync(permanent, permanent + "-moved", CancellationToken.None, 300); throw new InvalidOperationException("BusyDirectoryAccepted"); }
                catch (IOException error) when (error.Message == "UpgradeFilesBusy") { }
                if (!Directory.Exists(permanent) || Directory.Exists(permanent + "-moved")) throw new InvalidOperationException("OriginalFilesLost");
            }
            checks.Add("PermanentLockBoundedAndOriginalPreserved");
            var cancelled = Make("cancelled");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { await MoveAsync(cancelled, cancelled + "-moved", cancel.Token); throw new InvalidOperationException("CancellationIgnored"); }
                catch (OperationCanceledException) { }
            }
            checks.Add("CancelledBeforeMutation");
            var conflict = Make("conflict");
            Make("conflict-moved");
            try { await MoveAsync(conflict, conflict + "-moved", CancellationToken.None); throw new InvalidOperationException("ExistingDestinationAccepted"); }
            catch (IOException error) when (error.Message != "UpgradeFilesBusy") { }
            checks.Add("ExistingDestinationNeverOverwritten");
            return checks.ToArray();
        }
        finally { Directory.Delete(root, recursive: true); } // exclusively owned, random fixture root
    }
}
