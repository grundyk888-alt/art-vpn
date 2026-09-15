using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

internal sealed record ClientPayloadFile(string RelativePath, long Length, string Sha256);
internal enum ClientPackageKind { PortableZip, Installer }

internal sealed record ClientCatalogEntry(
    string Product,
    string Version,
    Uri OfficialAsset,
    string AssetName,
    long AssetLength,
    string AssetSha256,
    ClientPayloadFile[] ExactFiles,
    ClientPackageKind PackageKind = ClientPackageKind.PortableZip);

internal static class ClientCatalog
{
    public static readonly ClientCatalogEntry Happ421 = new(
        "Happ", AcceptedClientRelease.HappVersion, new Uri(AcceptedClientRelease.HappUrl),
        AcceptedClientRelease.HappAssetName, AcceptedClientRelease.HappBytes, AcceptedClientRelease.HappSha256,
        [new(AcceptedClientRelease.HappAssetName, AcceptedClientRelease.HappBytes, AcceptedClientRelease.HappSha256)],
        ClientPackageKind.Installer);

    public static readonly ClientCatalogEntry Throne124 = new(
        "Throne",
        "1.2.4",
        new Uri("https://github.com/throneproj/Throne/releases/download/1.2.4/Throne-1.2.4-windows64.zip"),
        "Throne-1.2.4-windows64.zip",
        44_374_170,
        "89D32F3A557849641B466D3C2F0AC69DFC02EC8309A129289290E8FCCA4B7B32",
        [
            new("Throne/libcronet.dll", 9_528_832, "257F966119FFCA91D7A2CE110A4B668B865D88BF2ED5339FD06A5644B0D02823"),
            new("Throne/Throne.exe", 34_030_592, "6F34BB5A1E290A64A01D64DA1ABC0049607E688C79A0F5595CCBAE30D6E8BB5E"),
            new("Throne/ThroneCore.exe", 67_233_280, "457545E94AF4F36E19EE0492592CF777270CD99E20FC0EEF7F6D3110E4F5CE28"),
            new("Throne/updater.exe", 631_296, "416933F908DF2CE8638F7122754C6C744403FB9189F2640CDEB13F89072EA010")
        ]);
}

internal static class ClientAcquisitionEngine
{
    public static async Task<ClientAcquisitionReceipt> AcquireAsync(
        RuntimeOptions options,
        ClientCatalogEntry entry,
        ClientAssetFetch fetch,
        bool enforcePrivateAcl,
        CancellationToken cancellationToken)
    {
        ValidateCatalog(entry);
        var quarantine = Path.Combine(options.PrivateRoot, "clients", "quarantine");
        ClientDirectorySecurity.EnsureNoLinks(quarantine);
        Directory.CreateDirectory(quarantine);
        if (enforcePrivateAcl) PrivateDirectorySecurity.Apply(options.PrivateRoot);
        if (enforcePrivateAcl) ClientDirectorySecurity.EnsurePublicRoot(options.DataRoot, entry.Product, entry.Version);
        var finalRoot = Path.Combine(quarantine, entry.Product, entry.Version);
        ClientDirectorySecurity.EnsureNoLinks(finalRoot);
        var finalArchive = Path.Combine(finalRoot, entry.AssetName);
        var finalReceipt = Path.Combine(finalRoot, "acquisition.v1.json");
        if (Directory.Exists(finalRoot))
        {
            if (!File.Exists(finalArchive) || !File.Exists(finalReceipt))
                throw new InvalidDataException("ExistingClientQuarantineRejected");
            VerifyArchive(finalArchive, entry);
            _ = ReadReceipt(finalReceipt, entry);
            var reused = InstallVerifiedPortable(options, finalArchive, entry);
            AtomicFile.ReplaceJson(finalReceipt, reused);
            AtomicFile.ReplaceJson(Path.Combine(options.DataRoot, "state", "clients", entry.Product + ".v1.json"), reused);
            return reused;
        }

        var temporary = Path.Combine(quarantine, ".acquire-" + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(temporary);
        var temporaryArchive = Path.Combine(temporary, entry.AssetName);
        try
        {
            await fetch(entry, temporaryArchive, cancellationToken).ConfigureAwait(false);
            VerifyArchive(temporaryArchive, entry);
            var receipt = new ClientAcquisitionReceipt(
                1,
                "art-vpn-client-acquisition",
                entry.Product,
                entry.Version,
                "DownloadedVerifiedQuarantined",
                entry.AssetName,
                entry.AssetLength,
                entry.AssetSha256,
                entry.ExactFiles.Length,
                DateTimeOffset.UtcNow.ToString("o"),
                false,
                false,
                false);
            AtomicFile.WriteJson(Path.Combine(temporary, "acquisition.v1.json"), receipt);
            Directory.CreateDirectory(Path.GetDirectoryName(finalRoot)!);
            Directory.Move(temporary, finalRoot);
            var installed = InstallVerifiedPortable(options, finalArchive, entry);
            AtomicFile.ReplaceJson(finalReceipt, installed);
            AtomicFile.ReplaceJson(Path.Combine(options.DataRoot, "state", "clients", entry.Product + ".v1.json"), installed);
            return installed;
        }
        catch
        {
            DeleteOwnedTemporary(quarantine, temporary);
            throw;
        }
    }

    internal static void VerifyArchive(string path, ClientCatalogEntry entry)
    {
        ClientDirectorySecurity.EnsureNoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != entry.AssetLength)
            throw new InvalidDataException("ClientAssetLengthRejected");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var digest = Convert.ToHexString(SHA256.HashData(stream));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(entry.AssetSha256)))
                throw new InvalidDataException("ClientAssetHashRejected");
        }

        // An installer is retained as one verified file, never unpacked or run by
        // the privileged service. Launch is an explicit action in the owner's UI.
        if (entry.PackageKind == ClientPackageKind.Installer) return;

        var expected = entry.ExactFiles.ToDictionary(file => Normalize(file.RelativePath), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(path);
        foreach (var item in archive.Entries)
        {
            if (string.IsNullOrEmpty(item.Name)) continue;
            var normalized = Normalize(item.FullName);
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains(':') ||
                normalized.Split('/').Any(part => part is "" or "." or "..") || !expected.TryGetValue(normalized, out var file) ||
                !seen.Add(normalized) || item.Length != file.Length)
                throw new InvalidDataException("ClientArchiveShapeRejected");
            using var entryStream = item.Open();
            var entryHash = Convert.ToHexString(SHA256.HashData(entryStream));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(entryHash), Encoding.ASCII.GetBytes(file.Sha256)))
                throw new InvalidDataException("ClientPayloadHashRejected");
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("ClientArchiveCoverageRejected");
    }

    private static ClientAcquisitionReceipt InstallVerifiedPortable(
        RuntimeOptions options,
        string archivePath,
        ClientCatalogEntry entry)
    {
        var clientsRoot = Path.GetFullPath(Path.Combine(options.DataRoot, "clients"));
        var installRoot = Path.GetFullPath(Path.Combine(clientsRoot, entry.Product, entry.Version));
        ClientDirectorySecurity.EnsureNoLinks(installRoot);
        if (!installRoot.StartsWith(clientsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ClientInstallPathRejected");

        if (Directory.Exists(installRoot))
        {
            VerifyInstalledPortable(installRoot, entry);
            return ReadyReceipt(entry);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(installRoot)!);
        var staging = installRoot + ".stage-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            if (entry.PackageKind == ClientPackageKind.Installer)
            {
                File.Copy(archivePath, Path.Combine(staging, entry.AssetName), overwrite: false);
                VerifyInstalledPortable(staging, entry);
                Directory.Move(staging, installRoot);
                return ReadyReceipt(entry);
            }
            var expected = entry.ExactFiles.ToDictionary(file => Normalize(file.RelativePath), StringComparer.OrdinalIgnoreCase);
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var item in archive.Entries)
            {
                if (string.IsNullOrEmpty(item.Name)) continue;
                var normalized = Normalize(item.FullName);
                if (!expected.TryGetValue(normalized, out var file))
                    throw new InvalidDataException("ClientArchiveShapeRejected");
                var leaf = Path.GetFileName(normalized);
                if (leaf != normalized.Split('/').Last() || string.IsNullOrWhiteSpace(leaf))
                    throw new InvalidDataException("ClientInstallPathRejected");
                var destination = Path.Combine(staging, leaf);
                using var input = item.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.WriteThrough);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
                if (new FileInfo(destination).Length != file.Length)
                    throw new InvalidDataException("ClientPayloadLengthRejected");
            }
            VerifyInstalledPortable(staging, entry);
            Directory.Move(staging, installRoot);
            return ReadyReceipt(entry);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static void VerifyInstalledPortable(string root, ClientCatalogEntry entry)
    {
        ClientDirectorySecurity.EnsureNoLinks(root);
        var expected = entry.ExactFiles.ToDictionary(file => Path.GetFileName(Normalize(file.RelativePath)),
            StringComparer.OrdinalIgnoreCase);
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (actual.Length != expected.Count) throw new InvalidDataException("ExistingClientInstallRejected");
        foreach (var path in actual)
        {
            ClientDirectorySecurity.EnsureNoLinks(path);
            if (!expected.TryGetValue(Path.GetFileName(path), out var file) || new FileInfo(path).Length != file.Length)
                throw new InvalidDataException("ExistingClientInstallRejected");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var digest = Convert.ToHexString(SHA256.HashData(stream));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(file.Sha256)))
                throw new InvalidDataException("ExistingClientInstallRejected");
        }
    }

    private static ClientAcquisitionReceipt ReadyReceipt(ClientCatalogEntry entry) => new(
        1, "art-vpn-client-acquisition", entry.Product, entry.Version,
        entry.PackageKind == ClientPackageKind.Installer ? "DownloadedVerifiedInstaller" : "DownloadedVerifiedReady",
        entry.AssetName, entry.AssetLength, entry.AssetSha256, entry.ExactFiles.Length,
        DateTimeOffset.UtcNow.ToString("o"), false, false, false);

    private static ClientAcquisitionReceipt ReadReceipt(string path, ClientCatalogEntry entry)
    {
        var receipt = System.Text.Json.JsonSerializer.Deserialize<ClientAcquisitionReceipt>(
                          File.ReadAllText(path, Encoding.UTF8), JsonSettings.Strict)
                      ?? throw new InvalidDataException("ClientReceiptMissing");
        if (receipt.Schema != 1 || receipt.Kind != "art-vpn-client-acquisition" ||
            receipt.Product != entry.Product || receipt.Version != entry.Version ||
            receipt.Status is not ("DownloadedVerifiedQuarantined" or "DownloadedVerifiedReady" or "DownloadedVerifiedInstaller") || receipt.AssetName != entry.AssetName ||
            receipt.AssetLength != entry.AssetLength || receipt.AssetSha256 != entry.AssetSha256 ||
            receipt.PayloadFiles != entry.ExactFiles.Length || receipt.Activated || receipt.Launched ||
            receipt.ContainsProviderSecret)
            throw new InvalidDataException("ClientReceiptRejected");
        return receipt;
    }

    private static void ValidateCatalog(ClientCatalogEntry entry)
    {
        static bool Segment(string value) => value.Length is >= 1 and <= 32 &&
            char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
        if (!Segment(entry.Product) || !Segment(entry.Version) ||
            !Enum.IsDefined(entry.PackageKind) || entry.AssetName != Path.GetFileName(entry.AssetName) ||
            !entry.AssetName.EndsWith(entry.PackageKind == ClientPackageKind.Installer ? ".exe" : ".zip", StringComparison.OrdinalIgnoreCase) ||
            entry.OfficialAsset.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(entry.OfficialAsset.UserInfo) ||
            !string.IsNullOrEmpty(entry.OfficialAsset.Fragment) || entry.AssetLength is < 1 or > 200_000_000 ||
            entry.AssetSha256.Length != 64 || entry.ExactFiles.Length is < 1 or > 64 ||
            entry.ExactFiles.Select(file => Normalize(file.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.ExactFiles.Length)
            throw new InvalidDataException("ClientCatalogRejected");
        if (entry.PackageKind == ClientPackageKind.Installer &&
            (entry.ExactFiles.Length != 1 || entry.ExactFiles[0].RelativePath != entry.AssetName ||
             entry.ExactFiles[0].Length != entry.AssetLength || entry.ExactFiles[0].Sha256 != entry.AssetSha256))
            throw new InvalidDataException("ClientInstallerCatalogRejected");
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static void DeleteOwnedTemporary(string quarantineRoot, string candidate)
    {
        var root = Path.GetFullPath(quarantineRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(candidate);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(resolved),
                "^\\.acquire-[a-f0-9]{32}\\.tmp$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidOperationException("ClientTemporaryPathRejected");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}

internal static class ClientAssetFetcher
{
    public static async Task DownloadAsync(
        ClientCatalogEntry entry,
        string destinationPath,
        CancellationToken cancellationToken,
        Uri? proxy = null)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            CheckCertificateRevocationList = true,
            // LocalSystem does not inherit the interactive owner's WinINET
            // proxy. The caller passes the observed route, never changes it.
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy(proxy)
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ART-VPN/0.1 client-acquisition");
        var current = ValidateUri(entry.OfficialAsset);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirect == 5 || response.Headers.Location is null)
                    throw new InvalidDataException("ClientRedirectRejected");
                current = ValidateUri(response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location));
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new IOException("ClientHttpRejected");
            if (response.Content.Headers.ContentLength is long length && length != entry.AssetLength)
                throw new InvalidDataException("ClientAssetLengthRejected");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var buffer = new byte[64 * 1024];
            long written = 0;
            try
            {
                while (true)
                {
                    var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    written += count;
                    if (written > entry.AssetLength) throw new InvalidDataException("ClientAssetLengthRejected");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                }
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                if (written != entry.AssetLength) throw new InvalidDataException("ClientAssetLengthRejected");
                return;
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        throw new InvalidDataException("ClientRedirectRejected");
    }

    private static Uri ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.DnsSafeHost) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            (!uri.IsDefaultPort && uri.Port != 443))
            throw new InvalidDataException("ClientAssetUriRejected");
        return uri;
    }
}

internal sealed record ClientAcquisitionReceipt(
    int Schema,
    string Kind,
    string Product,
    string Version,
    string Status,
    string AssetName,
    long AssetLength,
    string AssetSha256,
    int PayloadFiles,
    string AcquiredAtUtc,
    bool Activated,
    bool Launched,
    bool ContainsProviderSecret);

internal static class ClientDownloadRoutePolicy
{
    public static Uri? Select(SystemProxySnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.AutoConfigUrl)) throw new InvalidOperationException("ClientDownloadPacUnsupported");
        if (snapshot.ProxyEnable != 1) return null;
        var server = snapshot.ProxyServer?.Trim() ?? "";
        if (server.Contains('='))
        {
            var entries = server.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).ToArray();
            var https = entries.Where(item => item.Length == 2 && item[0].Trim().Equals("https", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (https.Length != 1) throw new InvalidOperationException("ClientDownloadRouteUnsupported");
            server = https[0][1].Trim();
        }
        if (!Uri.TryCreate("http://" + server, UriKind.Absolute, out var proxy) ||
            proxy.Host is not ("127.0.0.1" or "localhost" or "[::1]") || proxy.Port is < 1 or > 65535 ||
            proxy.UserInfo.Length != 0 || proxy.AbsolutePath != "/" || proxy.Query.Length != 0 || proxy.Fragment.Length != 0)
            throw new InvalidOperationException("ClientDownloadRouteUnsupported");
        return proxy;
    }

    internal static int VerifyContract()
    {
        static SystemProxySnapshot Snapshot(int enabled, string server, string pac = "") => new(enabled, server, "", pac);
        if (Select(Snapshot(0, "127.0.0.1:2080")) is not null ||
            Select(Snapshot(1, "127.0.0.1:22080"))?.Port != 22080 ||
            Select(Snapshot(1, "127.0.0.1:2080"))?.Port != 2080 ||
            Select(Snapshot(1, "http=127.0.0.1:7890;https=127.0.0.1:8889"))?.Port != 8889)
            throw new InvalidOperationException("ClientDownloadRouteTestFailed");
        foreach (var sample in new[] { Snapshot(1, "bad:secret@127.0.0.1:2080"), Snapshot(1, "other.invalid:8080"),
                     Snapshot(1, "127.0.0.1:2080/path"), Snapshot(1, "127.0.0.1:2080", "https://example.invalid/pac"),
                     Snapshot(1, "https=127.0.0.1:2080;https=127.0.0.1:22080") })
        {
            try { Select(sample); }
            catch (InvalidOperationException) { continue; }
            throw new InvalidOperationException("UnsafeClientDownloadRouteAccepted");
        }
        return 9;
    }
}

internal static class ClientAcquisitionTests
{
    public static async Task<ClientAcquisitionTestReceipt> RunAsync()
    {
        var routeChecks = ClientDownloadRoutePolicy.VerifyContract();
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-client-test-" + Guid.NewGuid().ToString("N"));
        var goodArchive = Path.Combine(Path.GetTempPath(), "art-vpn-client-fixture-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var content = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Fixture/client.exe"] = Encoding.ASCII.GetBytes("fixture-client"),
                ["Fixture/core.exe"] = Encoding.ASCII.GetBytes("fixture-core")
            };
            using (var archive = ZipFile.Open(goodArchive, ZipArchiveMode.Create))
                foreach (var item in content)
                {
                    var entry = archive.CreateEntry(item.Key, CompressionLevel.NoCompression);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(item.Value).ConfigureAwait(false);
                }
            var files = content.Select(item => new ClientPayloadFile(
                item.Key, item.Value.Length, Convert.ToHexString(SHA256.HashData(item.Value)))).ToArray();
            var bytes = await File.ReadAllBytesAsync(goodArchive).ConfigureAwait(false);
            var catalog = new ClientCatalogEntry(
                "FixtureClient", "1.0.0", new Uri("https://example.invalid/fixture.zip"), "fixture.zip",
                bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), files);
            var options = RuntimeOptions.Test(root, "ARTSPORT.ARTVpn.ClientTest." + Guid.NewGuid().ToString("N"));
            var receipt = await ClientAcquisitionEngine.AcquireAsync(options, catalog,
                (_, destination, _) => File.WriteAllBytesAsync(destination, bytes), false, CancellationToken.None)
                .ConfigureAwait(false);
            Assert(receipt.Status == "DownloadedVerifiedReady" && !receipt.Activated && !receipt.Launched);
            Assert(File.Exists(Path.Combine(options.DataRoot, "clients", catalog.Product, catalog.Version, "client.exe")));
            var second = await ClientAcquisitionEngine.AcquireAsync(options, catalog,
                (_, _, _) => throw new InvalidOperationException("ExistingVerifiedArchiveShouldBeReused"), false,
                CancellationToken.None).ConfigureAwait(false);
            Assert(second.AssetSha256 == receipt.AssetSha256);

            var badRoot = root + "-bad";
            var badOptions = RuntimeOptions.Test(badRoot, "ARTSPORT.ARTVpn.ClientBad." + Guid.NewGuid().ToString("N"));
            var corrupted = bytes.ToArray();
            corrupted[^1] ^= 0x55;
            try
            {
                _ = await ClientAcquisitionEngine.AcquireAsync(badOptions, catalog,
                    (_, destination, _) => File.WriteAllBytesAsync(destination, corrupted), false, CancellationToken.None)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("TamperedClientAccepted");
            }
            catch (InvalidDataException) { }
            Assert(!Directory.Exists(Path.Combine(badOptions.PrivateRoot, "clients", "quarantine", catalog.Product, catalog.Version)));

            // Raw installers use the same pinned-asset pipeline but must never
            // be executed by acquisition (especially in the LocalSystem service).
            var installerBytes = Encoding.ASCII.GetBytes("MZ-fixture-not-an-executable");
            var installerHash = Convert.ToHexString(SHA256.HashData(installerBytes));
            var installerCatalog = new ClientCatalogEntry("FixtureInstaller", "1.0.0",
                new Uri("https://example.invalid/setup.exe"), "setup.exe", installerBytes.Length, installerHash,
                [new("setup.exe", installerBytes.Length, installerHash)], ClientPackageKind.Installer);
            var installer = await ClientAcquisitionEngine.AcquireAsync(options, installerCatalog,
                (_, destination, _) => File.WriteAllBytesAsync(destination, installerBytes), false, CancellationToken.None);
            var installerPath = Path.Combine(options.DataRoot, "clients", installerCatalog.Product, installerCatalog.Version, "setup.exe");
            Assert(installer.Status == "DownloadedVerifiedInstaller" && !installer.Activated && !installer.Launched &&
                File.ReadAllBytes(installerPath).SequenceEqual(installerBytes));
            var reusedInstaller = await ClientAcquisitionEngine.AcquireAsync(options, installerCatalog,
                (_, _, _) => throw new InvalidOperationException("VerifiedInstallerShouldBeReused"), false, CancellationToken.None);
            Assert(reusedInstaller.AssetSha256 == installerHash && reusedInstaller.PayloadFiles == 1);
            var tamperedInstaller = installerBytes.ToArray(); tamperedInstaller[^1] ^= 0x55;
            File.WriteAllBytes(installerPath, tamperedInstaller);
            try
            {
                await ClientAcquisitionEngine.AcquireAsync(options, installerCatalog,
                    (_, _, _) => throw new InvalidOperationException("MustNotRedownloadOverChangedInstallation"), false, CancellationToken.None);
                throw new InvalidOperationException("ChangedInstallerAccepted");
            }
            catch (InvalidDataException) { }
            Assert(File.ReadAllBytes(installerPath).SequenceEqual(tamperedInstaller));
            try
            {
                await ClientAcquisitionEngine.AcquireAsync(options, installerCatalog with { Product = "../escape" },
                    (_, _, _) => throw new InvalidOperationException("UnsafeCatalogReachedNetwork"), false, CancellationToken.None);
                throw new InvalidOperationException("ClientPathTraversalAccepted");
            }
            catch (InvalidDataException) { }
            try
            {
                await ClientAcquisitionEngine.AcquireAsync(options, installerCatalog with { ExactFiles = files },
                    (_, _, _) => throw new InvalidOperationException("WrongManifestReachedNetwork"), false, CancellationToken.None);
                throw new InvalidOperationException("InconsistentInstallerManifestAccepted");
            }
            catch (InvalidDataException) { }
            var cancelled = installerCatalog with { Product = "CancelledInstaller" };
            try
            {
                await ClientAcquisitionEngine.AcquireAsync(options, cancelled, async (_, destination, _) =>
                {
                    await File.WriteAllBytesAsync(destination, installerBytes[..3]);
                    throw new OperationCanceledException();
                }, false, CancellationToken.None);
                throw new InvalidOperationException("CancelledInstallerAccepted");
            }
            catch (OperationCanceledException) { }
            Assert(!Directory.Exists(Path.Combine(options.DataRoot, "clients", cancelled.Product)) &&
                !Directory.EnumerateDirectories(Path.Combine(options.PrivateRoot, "clients", "quarantine"), ".acquire-*").Any());

            return new ClientAcquisitionTestReceipt("Passed", 15 + routeChecks, true, true, true, false, false);
        }
        finally
        {
            if (File.Exists(goodArchive)) File.Delete(goodArchive);
            foreach (var candidate in new[] { root, root + "-bad" })
            {
                var resolved = Path.GetFullPath(candidate);
                var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(resolved).StartsWith("art-vpn-client-test-", StringComparison.Ordinal) &&
                    Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
            }
        }
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("ClientAcquisitionInvariantFailed");
    }
}

internal sealed record ClientAcquisitionTestReceipt(
    string Status,
    int Scenarios,
    bool ExactArchiveVerified,
    bool ExactPayloadVerified,
    bool TamperRejected,
    bool Activated,
    bool SecretDisplayed);
