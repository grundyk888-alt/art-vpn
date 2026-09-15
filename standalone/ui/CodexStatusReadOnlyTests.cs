using System.Text;
using ArtSport.Vpn.Shared;

namespace ArtSport.ArtVpn.Ui;

internal static class CodexStatusReadOnlyTests
{
    internal static int Verify()
    {
        // Only synthetic files in this test's unique directory. Never inspect
        // or migrate the developer's real profile during a build/QA run.
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-codex-observe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".env");
        var checks = 0;
        void Assert(bool value) { if (!value) throw new InvalidOperationException("CodexStatusReadOnlyFailed"); checks++; }
        try
        {
            Assert(CodexProxyCompatibility.InspectFile(path, "http://127.0.0.1:22080").Code == "NotConfigured");
            Assert(Directory.GetFiles(root).Length == 0);
            File.WriteAllText(path, "FIXTURE=not-a-real-secret\nHTTPS_PROXY=http://127.0.0.1:2080\n", new UTF8Encoding(false));
            var original = File.ReadAllBytes(path);
            var written = File.GetLastWriteTimeUtc(path);
            for (var i = 0; i < 5; i++)
                Assert(CodexProxyCompatibility.InspectFile(path, "http://127.0.0.1:22080").Code == "RouteUpdateSuggested");
            Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(original));
            Assert(File.GetLastWriteTimeUtc(path) == written && Directory.GetFiles(root).Length == 1);

            var migrated = CodexProxyCompatibility.PrepareFile(path, "http://127.0.0.1:22080");
            Assert(migrated.Changed && migrated.Code == "Migrated");
            Assert(File.ReadAllBytes(Directory.GetFiles(root, ".env.art-vpn-backup-*").Single()).AsSpan().SequenceEqual(original));
            var afterMigration = Directory.GetFiles(root).Length;
            var prepared = File.ReadAllBytes(path);
            Assert(CodexProxyCompatibility.InspectFile(path, "http://127.0.0.1:22080").Code == "Ready");
            Assert(CodexProxyCompatibility.InspectFile(path, "http://127.0.0.1:2080").Code == "RouteUpdateSuggested");
            Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(prepared) && Directory.GetFiles(root).Length == afterMigration);

            File.WriteAllText(path, "HTTPS_PROXY=http://corporate.invalid:3128\n", new UTF8Encoding(false));
            original = File.ReadAllBytes(path);
            Assert(CodexProxyCompatibility.InspectFile(path, "http://127.0.0.1:22080").Code == "CustomOverride");
            Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(original));
            File.WriteAllBytes(path, [0xff, 0xfe, 0xff]);
            Assert(CodexProxyCompatibility.InspectFile(path, "http://127.0.0.1:22080").Code == "Encoding");
            Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 0xff, 0xfe, 0xff }));
            File.WriteAllText(path, new string('x', 131073));
            Assert(CodexProxyCompatibility.InspectFile(path, "http://127.0.0.1:22080").Code == "UnsafeFile");
            return checks;
        }
        finally
        {
            // This directory was created above exclusively for synthetic QA.
            foreach (var file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root, recursive: false);
        }
    }
}
