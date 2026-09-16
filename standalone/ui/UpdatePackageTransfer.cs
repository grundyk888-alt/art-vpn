using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ArtSport.ArtVpn.Service;

namespace ArtSport.ArtVpn.Ui;

// No network-provided code runs until a pinned publisher key AND the complete
// package hash have been verified. TLS alone is not a release signature.
internal static class UpdatePackageTransfer
{
    internal const long MaxBytes = 500_000_000;
    internal const string Site = "artfitness.ru";

    internal static VerifiedUpdate Verify(string receiptJson, string channelJson, string installJson, DateTimeOffset now)
    {
        using var receipt = JsonDocument.Parse(receiptJson);
        using var channel = JsonDocument.Parse(channelJson);
        using var install = JsonDocument.Parse(installJson);
        var r = receipt.RootElement; var c = channel.RootElement; var i = install.RootElement;
        if (r.GetProperty("status").GetString() != "Available" ||
            !DateTimeOffset.TryParse(r.GetProperty("checkedAtUtc").GetString(), out var checkedAt) ||
            now - checkedAt > TimeSpan.FromMinutes(30) || checkedAt > now.AddMinutes(5) ||
            c.GetProperty("kind").GetString() != "art-vpn-update-channel" || !c.GetProperty("enabled").GetBoolean() ||
            c.GetProperty("manifestHost").GetString() != Site || c.GetProperty("packageHost").GetString() != Site)
            throw new InvalidDataException("UpdateReceiptRejected");
        var signedJson = r.GetProperty("signedManifest").GetString() ?? "";
        if (signedJson.Length is 0 or > 65_536) throw new InvalidDataException("UpdateManifestSizeRejected");
        using var key = RSA.Create();
        key.ImportFromPem(c.GetProperty("publicKeyPem").GetString()!);
        var update = SignedUpdatePolicy.Verify(signedJson, key, c.GetProperty("signerId").GetString()!,
            i.GetProperty("installId").GetString()!, Environment.OSVersion.Version.Build, now);
        using var signed = JsonDocument.Parse(signedJson);
        // Automated release delivery currently supports stable numeric builds only.
        if (signed.RootElement.GetProperty("channel").GetString() != c.GetProperty("channel").GetString() ||
            !update.EligibleForInstall || !Version.TryParse(update.Version, out var next) ||
            !Version.TryParse(i.GetProperty("version").GetString(), out var current) || next <= current ||
            i.GetProperty("version").GetString() != r.GetProperty("currentVersion").GetString() ||
            update.Version != r.GetProperty("availableVersion").GetString() ||
            update.PackageUri.AbsoluteUri != r.GetProperty("packageUri").GetString())
            throw new InvalidDataException("UpdateVersionRejected");
        ValidateUri(update.PackageUri);
        return update;
    }

    internal static void ValidateUri(Uri uri)
    {
        if (uri.Scheme != "https" || uri.DnsSafeHost != Site || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/vpn/ART-VPN-[0-9]+\.[0-9]+\.[0-9]+(?:-win-x64)?\.zip$"))
            throw new InvalidDataException("UpdatePackageUriRejected");
    }

    internal static async Task<string> DownloadAsync(HttpClient client, VerifiedUpdate update, string directory,
        IProgress<string>? progress, CancellationToken token)
    {
        ValidateUri(update.PackageUri);
        var zipPath = Path.Combine(directory, "package.zip");
        var exePath = Path.Combine(directory, "ART-VPN-Setup.exe");
        try
        {
            using var response = await client.GetAsync(update.PackageUri, HttpCompletionOption.ResponseHeadersRead, token);
            // A Bitrix error/login/redirect must never become an executable.
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > MaxBytes)
                throw new IOException("UpdateDownloadRejected");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var zip = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                128 * 1024, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024]; long total = 0; long lastMb = -1;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                total = checked(total + count);
                if (total > MaxBytes) throw new InvalidDataException("UpdatePackageSizeRejected");
                hash.AppendData(buffer, 0, count);
                await zip.WriteAsync(buffer.AsMemory(0, count), token);
                if (total / 1_048_576 != lastMb)
                {
                    lastMb = total / 1_048_576;
                    var expected = response.Content.Headers.ContentLength;
                    progress?.Report(expected > 0 ? $"Загрузка: {total * 100 / expected}% · {lastMb} МБ" : $"Загрузка: {lastMb} МБ");
                }
            }
            if (total == 0 || response.Content.Headers.ContentLength is long bytes && bytes != total ||
                Convert.ToHexString(hash.GetHashAndReset()) != update.PackageSha256)
                throw new InvalidDataException("UpdatePackageHashRejected");
            progress?.Report("Проверка установщика…");
            zip.Position = 0;
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            var prefix = "ART-VPN-" + update.Version + "/";
            var expectedNames = new[] { prefix + "ART-VPN-Setup.exe", prefix + "READ-ME-RU.txt", prefix + "SHA256SUMS.txt" };
            if (archive.Entries.Count != 3 || !archive.Entries.Select(e => e.FullName).Order().SequenceEqual(expectedNames.Order()))
                throw new InvalidDataException("UpdateArchiveShapeRejected");
            var entry = archive.GetEntry(expectedNames[0])!;
            if (entry.Length is < 2 or > MaxBytes) throw new InvalidDataException("UpdateInstallerSizeRejected");
            using var source = entry.Open();
            await using (var output = new FileStream(exePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(output, token);
            return exePath;
        }
        catch { TryDelete(exePath); TryDelete(zipPath); throw; }
    }

    internal static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
