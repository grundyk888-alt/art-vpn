using System.Text.Json;

namespace ArtSport.ArtVpn.Service;

internal static class ResourceBudgetTests
{
    public static async Task<object> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-resource-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var count = 0;
        try
        {
            var logRoot = Path.Combine(root, "logs");
            var path = Path.Combine(logRoot, "service-events.jsonl");
            SafeLog.Write(root, "InitialEvent");
            Assert(File.Exists(path)); count++;
            File.WriteAllText(Path.Combine(logRoot, "unrelated.txt"), "keep");
            for (var i = 0; i < 4; i++)
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(SafeLog.MaxFileBytes);
                SafeLog.Write(root, "RotationEvent" + i);
            }
            var owned = Directory.GetFiles(logRoot, "service-events.jsonl*");
            Assert(owned.Length == 3 && owned.All(file => new FileInfo(file).Length <= SafeLog.MaxFileBytes)); count++;
            Assert(File.ReadAllText(Path.Combine(logRoot, "unrelated.txt")) == "keep"); count++;
            Parallel.For(0, 200, i => SafeLog.Write(root, "ParallelEvent" + i));
            foreach (var line in File.ReadLines(path)) using (JsonDocument.Parse(line)) { }
            Assert(File.ReadLines(path).Count() == 201); count++;
            SafeLog.Write(root, "https://example.com/private-subscription");
            Assert(!File.ReadAllText(path).Contains("private-subscription") && File.ReadAllText(path).Contains("DiagnosticCodeRedacted")); count++;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                SafeLog.Write(root, "DeniedWriteMustNotThrow");
            count++;
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "not a directory");
            SafeLog.Write(blocked, "InvalidPathMustNotThrow"); count++;
            var decision = new WatchdogDecision("KeepCurrent", "CurrentChannelHealthy", 22086, 22087, 0, false, DateTimeOffset.UtcNow.ToString("o"));
            await SlotRuntimeManager.ReportSafelyAsync(_ => throw new IOException("simulated status write failure"), decision, root, CancellationToken.None);
            var subsequentReport = false;
            await SlotRuntimeManager.ReportSafelyAsync(_ => { subsequentReport = true; return Task.CompletedTask; }, decision, root, CancellationToken.None);
            Assert(subsequentReport && File.ReadAllText(path).Contains("WatchdogReportFailed")); count++;
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancellationPreserved = false;
            try { await SlotRuntimeManager.ReportSafelyAsync(_ => throw new OperationCanceledException(cancelled.Token), decision, root, cancelled.Token); }
            catch (OperationCanceledException) { cancellationPreserved = true; }
            Assert(cancellationPreserved); count++;
            return new { status = "Passed", scenarios = count, boundedLogs = true, reportFailureIsolated = true, productionNetworkChanged = false };
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("art-vpn-resource-test-", StringComparison.Ordinal))
                Directory.Delete(resolved, true);
        }
    }

    private static void Assert(bool passed)
    {
        if (!passed) throw new InvalidOperationException("ResourceBudgetInvariantFailed");
    }
}
