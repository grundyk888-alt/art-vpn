using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Setup;

// Installation/upgrade must verify executable bytes before using them. Removal must
// not require those bytes to be healthy: missing/quarantined files are a normal
// reason to uninstall. Keep the original manifest binding and exact owned paths;
// never execute or follow anything from the damaged payload.
internal static class RemovalPayloadVerifier
{
    public static void Verify(string root, string version, string manifestSha256)
    {
        VerifyTree(root);
        var manifestPath = Path.Combine(root, "manifest.v1.json");
        var info = new FileInfo(manifestPath);
        if (!info.Exists || info.Length is < 32 or > 1_000_000)
            throw new InvalidDataException("UninstallManifestMissing");
        var bytes = File.ReadAllBytes(manifestPath);
        try
        {
            if (!Regex.IsMatch(manifestSha256, "^[A-F0-9]{64}$") ||
                Convert.ToHexString(SHA256.HashData(bytes)) != manifestSha256)
                throw new InvalidDataException("UninstallManifestBindingRejected");
            var manifest = JsonSerializer.Deserialize<PackageManifest>(bytes, JsonOptions.Strict)
                ?? throw new InvalidDataException("UninstallManifestRejected");
            if (manifest.Schema != 1 || manifest.Kind != "art-vpn-consumer-package" || manifest.Product != "ART VPN" ||
                manifest.Version != version || manifest.ContainsProviderSecret || manifest.MinimumWindowsBuild < 17763 ||
                !Regex.IsMatch(version, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-beta\\.[0-9]+)?$") ||
                manifest.Files is not { Length: >= 8 and <= 64 } ||
                manifest.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length)
                throw new InvalidDataException("UninstallManifestRejected");
            foreach (var file in manifest.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Path) || file.Path.Contains("..", StringComparison.Ordinal) ||
                    Path.IsPathFullyQualified(file.Path) || file.Path.Contains(':') ||
                    file.Path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                    file.Bytes is < 1 or > 500_000_000 || !Regex.IsMatch(file.Sha256, "^[A-F0-9]{64}$") ||
                    file.Role is not { Length: >= 1 and <= 32 } || !IsChild(root, Path.Combine(root, file.Path)))
                    throw new InvalidDataException("UninstallManifestEntryRejected");
            }
        }
        finally { Array.Clear(bytes); }
    }

    public static void VerifyTree(string root)
    {
        var resolved = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        // Check ancestors too. A safe-looking product child inside a junction is
        // still outside the fixed product boundary.
        for (var parent = new DirectoryInfo(resolved); parent is not null; parent = parent.Parent)
            if (parent.Exists) RejectReparsePoint(parent.Attributes);
        if (!Directory.Exists(resolved)) return;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(resolved));
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (!IsChild(resolved, entry.FullName)) throw new InvalidDataException("UninstallPathRejected");
                RejectReparsePoint(entry.Attributes); // Inspect before descending, never follow links.
                if ((entry.Attributes & FileAttributes.Directory) != 0) pending.Push(new DirectoryInfo(entry.FullName));
            }
        }
    }

    private static bool IsChild(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    private static void RejectReparsePoint(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("UninstallReparsePointRejected");
    }

    public static object Test()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-removal-test-" + Guid.NewGuid().ToString("N"));
        var count = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("RemovalScopeTestFailed"); count++; }
        void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { count++; return; }
            throw new InvalidOperationException("RemovalScopeRejectedTestFailed");
        }
        try
        {
            Directory.CreateDirectory(root);
            var payload = new byte[] { 1, 2, 3 };
            var files = Enumerable.Range(0, 8).Select(i => new PackageFile($"part{i}.bin", 3,
                Convert.ToHexString(SHA256.HashData(payload)), "runtime")).ToArray();
            foreach (var file in files) File.WriteAllBytes(Path.Combine(root, file.Path), payload);
            var manifest = new PackageManifest(1, "art-vpn-consumer-package", "ART VPN", "1.0.2", 17763, files, false);
            var manifestPath = Path.Combine(root, "manifest.v1.json");
            string WriteManifest(PackageManifest value)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions.Output);
                File.WriteAllBytes(manifestPath, bytes);
                return Convert.ToHexString(SHA256.HashData(bytes));
            }
            var hash = WriteManifest(manifest);
            Verify(root, manifest.Version, hash); count++;
            _ = PackageVerifier.Verify(root); count++;
            File.WriteAllBytes(Path.Combine(root, files[0].Path), [9]);
            Reject(() => PackageVerifier.Verify(root));
            Verify(root, manifest.Version, hash); count++;
            File.Delete(Path.Combine(root, files[1].Path));
            Verify(root, manifest.Version, hash); count++;
            Reject(() => Verify(root, "1.0.1", hash));
            Reject(() => Verify(root, manifest.Version, new string('0', 64)));
            var escaped = manifest with { Files = [files[0] with { Path = "../outside.bin" }, .. files.Skip(1)] };
            var escapedHash = WriteManifest(escaped);
            Reject(() => Verify(root, manifest.Version, escapedHash));
            Check(!IsChild(root, root + "-neighbour\\file"));
            Reject(() => RejectReparsePoint(FileAttributes.Directory | FileAttributes.ReparsePoint));
            Reject(() => RejectReparsePoint(FileAttributes.ReparsePoint));
            File.Delete(manifestPath);
            Reject(() => Verify(root, manifest.Version, hash));
            return new { status = "Passed", scenarios = count, changedAndMissingPayloadRemovable = true,
                upgradeIntegrityStillRequired = true, manifestBindingRequired = true, reparseRejected = true, systemChanged = false };
        }
        finally
        {
            VerifyTree(root);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
