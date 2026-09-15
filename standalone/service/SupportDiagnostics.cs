using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Service;

internal sealed record SanitizedSupportReport(
    int Schema,
    string Kind,
    string ReportId,
    string ProductVersion,
    string AnonymousInstallId,
    string Health,
    string Mode,
    string Country,
    string Quality,
    string Latency,
    string Stability,
    string LastSwitchAtUtc,
    string LastSwitchReason,
    string SubscriptionAtUtc,
    string Bypass,
    string Update,
    string[] EventCodes,
    string CreatedAtUtc,
    bool ContainsProviderSecret,
    bool ContainsTrafficContent,
    bool ContainsPersonalData);

internal sealed record SupportEndpointContract(
    int Schema,
    string Kind,
    bool Enabled,
    string EndpointUri,
    string EndpointHost);

internal sealed record SupportDeliveryReceipt(
    int Schema,
    string Kind,
    string ReportId,
    string Status,
    string CaseId,
    string DetailCode,
    string AtUtc,
    bool UserConfirmed,
    bool ContainsProviderSecret);

internal delegate Task<byte[]> SupportUpload(Uri endpoint, byte[] payload, CancellationToken cancellationToken);

internal static partial class SupportDiagnostics
{
    private const int MaxReportBytes = 256 * 1024;
    private const int MaxEventCodes = 200;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCode();

    [GeneratedRegex("^ARTVPN-[A-Z0-9]{8,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CaseIdPattern();

    public static SanitizedSupportReport Create(RuntimeOptions options, InstallState install)
    {
        var status = ReadStatus(options.SanitizedStatusPath, install.Version);
        var report = new SanitizedSupportReport(
            1,
            "art-vpn-sanitized-support-report",
            "R-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)),
            install.Version,
            AnonymousInstallId(install.InstallId),
            status.Health,
            status.Mode,
            status.Country,
            status.Quality,
            status.Latency,
            status.Stability,
            status.LastSwitchAtUtc,
            status.LastSwitchReason,
            status.SubscriptionAtUtc,
            status.Bypass,
            status.Update,
            ReadEventCodes(options),
            DateTimeOffset.UtcNow.ToString("o"),
            false,
            false,
            false);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonSettings.Output);
        try
        {
            if (bytes.Length > MaxReportBytes || ContainsSensitiveMaterial(bytes))
                throw new InvalidDataException("SupportReportSanitizationRejected");
            AtomicFile.ReplaceJson(options.LatestSupportReportPath, report);
            return report;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static async Task<SupportDeliveryReceipt> SubmitLatestAsync(
        RuntimeOptions options,
        bool userConfirmed,
        SupportUpload? upload,
        CancellationToken cancellationToken)
    {
        if (!userConfirmed) throw new InvalidOperationException("SupportConsentRequired");
        var reportBytes = await File.ReadAllBytesAsync(options.LatestSupportReportPath, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (reportBytes.Length is 0 or > MaxReportBytes || ContainsSensitiveMaterial(reportBytes))
                throw new InvalidDataException("SupportReportSanitizationRejected");
            var report = JsonSerializer.Deserialize<SanitizedSupportReport>(reportBytes, JsonSettings.Strict)
                ?? throw new InvalidDataException("SupportReportEmpty");
            ValidateReport(report);
            var endpoint = ReadEndpoint(options.SupportEndpointPath);
            var responseBytes = await (upload ?? HttpSupportUpload.SendAsync)(endpoint, reportBytes, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                using var response = JsonDocument.Parse(responseBytes, JsonSettings.Document);
                var root = response.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.EnumerateObject().Select(item => item.Name)
                        .SequenceEqual(["accepted", "caseId"], StringComparer.Ordinal) ||
                    root.GetProperty("accepted").ValueKind != JsonValueKind.True)
                    throw new InvalidDataException("SupportResponseRejected");
                var caseId = root.GetProperty("caseId").GetString() ?? "";
                if (!CaseIdPattern().IsMatch(caseId)) throw new InvalidDataException("SupportCaseIdRejected");
                var receipt = new SupportDeliveryReceipt(1, "art-vpn-support-delivery-receipt", report.ReportId,
                    "Sent", caseId, "SupportReportAccepted", DateTimeOffset.UtcNow.ToString("o"), true, false);
                AtomicFile.ReplaceJson(options.SupportDeliveryReceiptPath, receipt);
                return receipt;
            }
            finally { CryptographicOperations.ZeroMemory(responseBytes); }
        }
        catch (Exception ex)
        {
            var reportId = TryReadReportId(reportBytes);
            var receipt = new SupportDeliveryReceipt(1, "art-vpn-support-delivery-receipt", reportId,
                "NotSent", "", ErrorCodes.From(ex), DateTimeOffset.UtcNow.ToString("o"), true, false);
            AtomicFile.ReplaceJson(options.SupportDeliveryReceiptPath, receipt);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(reportBytes); }
    }

    private static SanitizedServiceStatus ReadStatus(string path, string version)
    {
        if (!File.Exists(path)) return SanitizedServiceStatus.Initial(version);
        return JsonSerializer.Deserialize<SanitizedServiceStatus>(File.ReadAllText(path, Encoding.UTF8), JsonSettings.Strict)
            ?? SanitizedServiceStatus.Initial(version);
    }

    private static string[] ReadEventCodes(RuntimeOptions options)
    {
        var path = Path.Combine(options.DataRoot, "logs", "service-events.jsonl");
        if (!File.Exists(path)) return [];
        var result = new List<string>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8).TakeLast(MaxEventCodes))
        {
            try
            {
                using var document = JsonDocument.Parse(line, JsonSettings.Document);
                if (!document.RootElement.TryGetProperty("code", out var value) || value.ValueKind != JsonValueKind.String)
                    continue;
                var code = value.GetString() ?? "";
                if (SafeCode().IsMatch(code)) result.Add(code);
            }
            catch (JsonException) { result.Add("UnreadableEventDiscarded"); }
        }
        return result.ToArray();
    }

    private static Uri ReadEndpoint(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("SupportEndpointNotConfigured");
        var json = File.ReadAllText(path, Encoding.UTF8);
        using var document = JsonDocument.Parse(json, JsonSettings.Document);
        if (!document.RootElement.EnumerateObject().Select(item => item.Name)
            .SequenceEqual(["schema", "kind", "enabled", "endpointUri", "endpointHost"], StringComparer.Ordinal))
            throw new InvalidDataException("SupportEndpointContractRejected");
        var contract = JsonSerializer.Deserialize<SupportEndpointContract>(json, JsonSettings.Strict)
            ?? throw new InvalidDataException("SupportEndpointContractRejected");
        if (contract.Schema != 1 || contract.Kind != "art-vpn-support-endpoint" || !contract.Enabled ||
            !Uri.TryCreate(contract.EndpointUri, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.IsDefaultPort is false ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath is "/" or "" ||
            !string.Equals(endpoint.DnsSafeHost, contract.EndpointHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SupportEndpointRejected");
        return endpoint;
    }

    private static void ValidateReport(SanitizedSupportReport report)
    {
        if (report.Schema != 1 || report.Kind != "art-vpn-sanitized-support-report" ||
            !report.ReportId.StartsWith("R-", StringComparison.Ordinal) ||
            report.ContainsProviderSecret || report.ContainsTrafficContent || report.ContainsPersonalData ||
            report.EventCodes.Length > MaxEventCodes || report.EventCodes.Any(code => !SafeCode().IsMatch(code)))
            throw new InvalidDataException("SupportReportContractRejected");
    }

    private static string AnonymousInstallId(string installId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("art-vpn-support-v1:" + installId)))[..16];

    private static string TryReadReportId(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, JsonSettings.Document);
            return document.RootElement.GetProperty("reportId").GetString() ?? "Unknown";
        }
        catch { return "Unknown"; }
    }

    private static bool ContainsSensitiveMaterial(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Contains("auth.quattro", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("vless://", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("vmess://", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("trojan://", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("provider.dpapi", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("subscriptionUrl", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(text, @"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b", RegexOptions.CultureInvariant) ||
               Regex.IsMatch(text, @"\b(?:[0-9A-F]{2}:){5}[0-9A-F]{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(text, @"C:\\Users\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

internal static class HttpSupportUpload
{
    public static async Task<byte[]> SendAsync(Uri endpoint, byte[] payload, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("SupportUploadRejected");
        if (response.Content.Headers.ContentLength is > 16_384) throw new InvalidDataException("SupportResponseSizeRejected");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 16_384) throw new InvalidDataException("SupportResponseSizeRejected");
        return bytes;
    }
}

internal static class SupportDiagnosticsTests
{
    public static async Task<SupportDiagnosticsTestReceipt> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-support-test-" + Guid.NewGuid().ToString("N"));
        var options = RuntimeOptions.Test(root, "ARTSPORT.ARTVpn.SupportTest." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.InstallStatePath)!);
            var install = new InstallState(1, "art-vpn-consumer-install", "Configured", "TEST-PC",
                "S-1-5-21-111-222-333", "S-1-5-21-111-222-333-1001", Guid.NewGuid().ToString("N"),
                "0.1.0-beta.1", new string('A', 64), DateTimeOffset.UtcNow.ToString("o"), false, false);
            AtomicFile.ReplaceJson(options.SanitizedStatusPath, SanitizedServiceStatus.Initial(install.Version) with
            {
                Health = "Healthy", Country = "Нидерланды", Latency = "84 мс", LastSwitchReason = "Stable"
            });
            SafeLog.Write(options.DataRoot, "BadRecordMacQuarantined");
            var report = SupportDiagnostics.Create(options, install);
            var reportText = File.ReadAllText(options.LatestSupportReportPath, Encoding.UTF8);
            if (report.ContainsProviderSecret || report.ContainsPersonalData || report.ContainsTrafficContent ||
                reportText.Contains("TEST-PC", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SupportReportSecretLeak");

            AtomicFile.ReplaceJson(options.SupportEndpointPath, new SupportEndpointContract(
                1, "art-vpn-support-endpoint", true, "https://support.example.invalid/api/art-vpn/report", "support.example.invalid"));
            var uploads = 0;
            SupportUpload fake = (endpoint, payload, token) =>
            {
                uploads++;
                if (endpoint.Host != "support.example.invalid" || payload.Length == 0)
                    throw new InvalidOperationException("SupportUploadContractLost");
                return Task.FromResult(Encoding.UTF8.GetBytes("{\"accepted\":true,\"caseId\":\"ARTVPN-TEST12345\"}"));
            };
            var consentRejected = false;
            try { await SupportDiagnostics.SubmitLatestAsync(options, false, fake, CancellationToken.None); }
            catch (InvalidOperationException ex) when (ex.Message == "SupportConsentRequired") { consentRejected = true; }
            if (!consentRejected || uploads != 0) throw new InvalidOperationException("SupportConsentBypassed");
            var receipt = await SupportDiagnostics.SubmitLatestAsync(options, true, fake, CancellationToken.None);
            if (uploads != 1 || receipt.Status != "Sent" || receipt.CaseId != "ARTVPN-TEST12345")
                throw new InvalidOperationException("SupportDeliveryReceiptRejected");
            return new SupportDiagnosticsTestReceipt("Passed", 9, true, true, true, false, false);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

internal sealed record SupportDiagnosticsTestReceipt(
    string Status,
    int Scenarios,
    bool ConsentRequired,
    bool Sanitized,
    bool DeliveryReceipt,
    bool BackgroundUpload,
    bool SecretDisplayed);
