using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArtSport.ArtVpn.Common;
using ArtSport.ArtVpn.Service;

namespace ArtSport.ArtVpn.Ui;

internal static class UpdatePackageTransferTests
{
    internal static async Task<object> RunAsync()
    {
        var checks = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "ARTVpn-UpdateQA-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow;
        using var key = RSA.Create(2048);
        var bytes = Archive("ART-VPN-1.0.37/ART-VPN-Setup.exe");
        var manifest = new SignedUpdateManifest(1, "art-vpn-signed-update", "ART VPN", "stable", "1.0.37", "1.0.0", 17763,
            "https://artfitness.ru/vpn/ART-VPN-1.0.37.zip", Convert.ToHexString(SHA256.HashData(bytes)), new string('B', 64),
            "art-vpn-update-fixture", now.ToString("o"), 100, "1.0.36", "");
        string Sign(SignedUpdateManifest value) => JsonSerializer.Serialize(value with
        {
            Signature = Convert.ToBase64String(key.SignData(SignedUpdatePolicy.CanonicalBytes(value), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        }, UpdateJson.Output);
        var channel = JsonSerializer.Serialize(new { kind = "art-vpn-update-channel", enabled = true, manifestHost = "artfitness.ru",
            packageHost = "artfitness.ru", signerId = manifest.SignerId, channel = "stable", publicKeyPem = key.ExportSubjectPublicKeyInfoPem() });
        var install = "{\"version\":\"1.0.36\",\"installId\":\"0123456789abcdef0123456789abcdef\"}";
        string Receipt(string signed, string at, string version = "1.0.37") => JsonSerializer.Serialize(new
        { status = "Available", checkedAtUtc = at, currentVersion = "1.0.36", availableVersion = version, packageUri = manifest.PackageUri, signedManifest = signed });
        void Reject(string name, Action action)
        {
            try { action(); } catch (Exception e) when (e is InvalidDataException or CryptographicException) { checks.Add(name); return; }
            throw new InvalidOperationException("UnsafeUpdateAccepted:" + name);
        }
        try
        {
            var receipt = Receipt(Sign(manifest), now.ToString("o"));
            var verified = UpdatePackageTransfer.Verify(receipt, channel, install, now);
            checks.Add("PinnedSignatureAccepted");
            Reject("TamperedSignature", () => UpdatePackageTransfer.Verify(receipt.Replace(new string('B', 64), new string('C', 64)), channel, install, now));
            Reject("StaleReceipt", () => UpdatePackageTransfer.Verify(receipt, channel, install, now.AddHours(1)));
            Reject("FutureReceipt", () => UpdatePackageTransfer.Verify(receipt, channel, install, now.AddMinutes(-10)));
            Reject("CurrentInstallChanged", () => UpdatePackageTransfer.Verify(receipt, channel, install.Replace("1.0.36", "1.0.37"), now));
            Reject("OlderBuild", () => UpdatePackageTransfer.Verify(Receipt(Sign(manifest with { Version = "1.0.35" }), now.ToString("o"), "1.0.35"), channel, install, now));
            Reject("SameBuild", () => UpdatePackageTransfer.Verify(Receipt(Sign(manifest with { Version = "1.0.36" }), now.ToString("o"), "1.0.36"), channel, install, now));
            Reject("WrongChannel", () => UpdatePackageTransfer.Verify(Receipt(Sign(manifest with { Channel = "preview" }), now.ToString("o")), channel, install, now));
            Reject("DeferredRollout", () => UpdatePackageTransfer.Verify(Receipt(Sign(manifest with { RolloutPercent = 0 }), now.ToString("o")), channel, install, now));
            Reject("WrongPublisher", () => UpdatePackageTransfer.Verify(receipt, channel.Replace(manifest.SignerId, "different-publisher"), install, now));
            foreach (var uri in new[] { "http://artfitness.ru/vpn/ART-VPN-1.0.37.zip", "https://artfitness.ru.evil.invalid/vpn/ART-VPN-1.0.37.zip",
                "https://user:pass@artfitness.ru/vpn/ART-VPN-1.0.37.zip", "https://artfitness.ru:444/vpn/ART-VPN-1.0.37.zip",
                "https://artfitness.ru/vpn/ART-VPN-1.0.37.zip?redirect=x", "https://artfitness.ru/vpn/setup.exe" })
                Reject("UnsafeUri", () => UpdatePackageTransfer.ValidateUri(new Uri(uri)));
            async Task Transfer(string name, byte[] data, VerifiedUpdate update, HttpStatusCode status, bool accepted, bool cancel = false, long? length = null)
            {
                var directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                using var client = new HttpClient(new Handler(data, status, length));
                using var cts = new CancellationTokenSource(); if (cancel) cts.Cancel();
                try
                {
                    var exe = await UpdatePackageTransfer.DownloadAsync(client, update, directory, null, cts.Token);
                    if (!accepted || !File.ReadAllBytes(exe).SequenceEqual(new byte[] { 77, 90, 1, 2 })) throw new InvalidOperationException("UnexpectedTransferAcceptance:" + name);
                }
                catch (Exception error) when (!accepted && error is IOException or InvalidDataException or OperationCanceledException)
                {
                    if (Directory.GetFiles(directory).Length != 0) throw new InvalidOperationException("RejectedDownloadNotCleaned");
                }
                checks.Add(name);
            }
            await Transfer("ValidPackage", bytes, verified, HttpStatusCode.OK, true);
            await Transfer("CorruptBytes", bytes.Concat(new byte[] { 0 }).ToArray(), verified, HttpStatusCode.OK, false);
            await Transfer("TruncatedArchive", bytes[..^8], verified, HttpStatusCode.OK, false);
            await Transfer("HtmlLogin", Encoding.UTF8.GetBytes("<html>login</html>"), verified, HttpStatusCode.OK, false);
            await Transfer("RedirectNotFollowed", bytes, verified, HttpStatusCode.Found, false);
            await Transfer("NotFound", bytes, verified, HttpStatusCode.NotFound, false);
            await Transfer("OversizedHeader", bytes, verified, HttpStatusCode.OK, false, length: UpdatePackageTransfer.MaxBytes + 1);
            await Transfer("WrongContentLength", bytes, verified, HttpStatusCode.OK, false, length: bytes.Length + 1);
            await Transfer("Cancellation", bytes, verified, HttpStatusCode.OK, false, cancel: true);
            foreach (var name in new[] { "../../ART-VPN-Setup.exe", "ART-VPN-1.0.36/ART-VPN-Setup.exe" })
            {
                var badArchive = Archive(name);
                await Transfer("ArchivePathRejected", badArchive, verified with { PackageSha256 = Convert.ToHexString(SHA256.HashData(badArchive)) }, HttpStatusCode.OK, false);
            }
            return new { status = "Passed", scenarios = checks.Count, checks, systemChanged = false, installerExecuted = false, secretDisplayed = false };
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] Archive(string exeName)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var exe = archive.CreateEntry(exeName).Open()) exe.Write(new byte[] { 77, 90, 1, 2 });
            archive.CreateEntry("ART-VPN-1.0.37/READ-ME-RU.txt");
            archive.CreateEntry("ART-VPN-1.0.37/SHA256SUMS.txt");
        }
        return stream.ToArray();
    }

    private sealed class Handler(byte[] bytes, HttpStatusCode status, long? length) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
            if (length.HasValue) response.Content.Headers.ContentLength = length.Value;
            return Task.FromResult(response);
        }
    }
}
