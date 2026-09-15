using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArtSport.ArtVpn.Service;

internal sealed record ProbeRoundSample(
    bool OpenAi,
    bool ChatGpt,
    bool Telegram,
    bool Integrity,
    bool RemoteConnect,
    double OpenAiMs,
    double ChatGptMs,
    double TelegramMs,
    double IntegrityMs,
    double ThroughputMbps,
    string EgressIpHash,
    string ObservedCountry,
    string FailureClass,
    bool HardDegradation,
    string GoogleStatus = "NotChecked",
    ProbeEndpointEvidence[]? Endpoints = null);

internal delegate Task<ProbeRoundSample> ProbeRoundTransport(
    int proxyPort,
    int integrityBytes,
    CancellationToken cancellationToken);

internal static class NodeQualificationRunner
{
    public static async Task<NodeProbeEvidence[]> RunAsync(
        IReadOnlyList<ProviderNodeView> nodes,
        int rounds,
        int integrityBytes,
        int concurrency,
        ProbeRoundTransport transport,
        CancellationToken cancellationToken)
    {
        if (nodes.Count < 1 || rounds is < 1 or > 8 || integrityBytes is < 262_144 or > 4_194_304 ||
            concurrency is < 1 or > 16)
            throw new InvalidDataException("QualificationRunBoundsRejected");
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = nodes.Select(async node =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var samples = new List<ProbeRoundSample>(rounds);
                for (var round = 0; round < rounds; round++)
                    samples.Add(await transport(node.ProxyPort, integrityBytes, cancellationToken).ConfigureAwait(false));
                return Summarize(node, rounds, samples);
            }
            finally { gate.Release(); }
        });
        return (await Task.WhenAll(tasks).ConfigureAwait(false))
            .OrderBy(result => !result.Clean).ThenBy(result => result.ScoreMs).ThenBy(result => result.Tag, StringComparer.Ordinal)
            .ToArray();
    }

    internal static NodeProbeEvidence Summarize(
        ProviderNodeView node,
        int rounds,
        IReadOnlyList<ProbeRoundSample> samples)
    {
        if (samples.Count != rounds) throw new InvalidDataException("QualificationSamplesRejected");
        // RemoteConnect is deliberately diagnostic-only.  The probe currently
        // targets mtalk.google.com, which is not an authoritative ChatGPT
        // Desktop Remote endpoint and therefore must not make an otherwise
        // healthy OpenAI route fail qualification.
        var tls = samples.Any(sample => sample.FailureClass is "TlsBadRecordMac" or "TlsIntegrityFailure");
        var hardFailures = samples.Count(sample => sample.HardDegradation &&
                                                   sample.FailureClass is not "RemoteHeuristicFailure");
        // A single transient timeout/reset in a six-round qualification must not
        // discard an otherwise stable node. TLS integrity failures remain a hard
        // quarantine signal and are never tolerated.
        var allowedTransientFailures = rounds >= 6 ? 1 : 0;
        var requiredAvailabilityPct = rounds >= 6 ? 83.0 : 100.0;
        var ipHashes = samples.Where(sample => sample.ChatGpt && sample.EgressIpHash.Length > 0)
            .Select(sample => sample.EgressIpHash).Distinct(StringComparer.Ordinal).ToArray();
        var locations = samples.Where(sample => sample.ChatGpt && sample.ObservedCountry.Length == 2)
            .Select(sample => sample.ObservedCountry).Distinct(StringComparer.Ordinal).ToArray();
        var egressStable = ipHashes.Length == 1 && locations.Length == 1;
        var throughputs = samples.Where(sample => sample.Integrity).Select(sample => sample.ThroughputMbps).Order().ToArray();
        var latencies = samples.SelectMany(sample => new[] { sample.OpenAiMs, sample.ChatGptMs, sample.TelegramMs })
            .Where(value => value > 0).Order().ToArray();
        var p50 = Percentile(latencies, 0.50);
        var p95 = Percentile(latencies, 0.95);
        var jitter = latencies.Length > 1
            ? Math.Sqrt(latencies.Select(value => Math.Pow(value - latencies.Average(), 2)).Average())
            : 0;
        var throughputFloor = throughputs.Length > 0 ? throughputs[0] : 0;
        var score = p95 + jitter + (throughputFloor > 0 ? 1_000 / throughputFloor : 100_000);
        double Pct(Func<ProbeRoundSample, bool> predicate) => Math.Round(samples.Count(predicate) * 100d / rounds, 3);
        var failure = samples.Select(sample => sample.FailureClass).FirstOrDefault(value => value != "Passed") ?? "Passed";
        var clean = !tls && hardFailures <= allowedTransientFailures &&
                    Pct(sample => sample.OpenAi) >= requiredAvailabilityPct &&
                    Pct(sample => sample.ChatGpt) >= requiredAvailabilityPct &&
                    Pct(sample => sample.Telegram) >= requiredAvailabilityPct &&
                    Pct(sample => sample.Integrity) >= requiredAvailabilityPct &&
                    egressStable && throughputFloor >= 1.0;
        return new NodeProbeEvidence(
            node.Tag, node.ProbeIdentity, node.Country, node.ServerKey, node.DestinationPort,
            clean, tls, rounds, samples.Count,
            Pct(sample => sample.OpenAi), Pct(sample => sample.ChatGpt), Pct(sample => sample.Telegram),
            Pct(sample => sample.Integrity), Pct(sample => sample.RemoteConnect),
            egressStable, Math.Max(0, ipHashes.Length - 1),
            Math.Round(p50, 3), Math.Round(p95, 3), Math.Round(jitter, 3), Math.Round(throughputFloor, 3),
            Math.Round(score, 3), locations.Length == 1 ? locations[0] : "", failure,
            samples.All(sample => sample.GoogleStatus == "NotChecked") ? -1 : Pct(sample => sample.GoogleStatus == "Available"));
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0) return 999_999;
        if (ordered.Count == 1) return ordered[0];
        var position = (ordered.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? ordered[lower] : ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }
}

internal static class LiveProbeTransport
{
    private const string OpenAiUrl = "https://api.openai.com/v1/models";
    private const string ChatGptUrl = "https://chatgpt.com/cdn-cgi/trace";
    private const string TelegramUrl = "https://api.telegram.org/";

    public static async Task<ProbeRoundSample> RunAsync(
        int proxyPort,
        int integrityBytes,
        CancellationToken cancellationToken)
    {
        var proxy = new WebProxy($"http://127.0.0.1:{proxyPort}");
        // Run alongside core checks; Google does not contribute to HardDegradation.
        var googleTask = GoogleReachability.ProbeAsync(proxy, cancellationToken);
        // CONNECT is diagnostic-only and transfers no benchmark payload. Run
        // it alongside the measured requests instead of adding up to eight
        // idle seconds after every round of a first-connection qualification.
        var remoteTask = ProbeHttpConnectAsync(proxyPort, "mtalk.google.com", 5228, cancellationToken);
        FetchResult openAi = new(0, [], 0, "NotRun"), chatGpt = new(0, [], 0, "NotRun"),
            telegram = new(0, [], 0, "NotRun"), integrity = new(0, [], 0, "NotRun");
        var remote = new RemoteConnectResult(false, "NotRun");
        try
        {
            // Small watchdog samples are liveness checks, not throughput
            // qualification. Serial endpoint timeouts across three routes can
            // exceed the 75s freshness gate and reset every failure sequence.
            // Parallelize only this small path; full qualification keeps its
            // original sequential bandwidth measurement.
            await RunProbeScheduleAsync(integrityBytes <= 65_536,
                async () => { openAi = await FetchAsync(proxy, OpenAiUrl, 65_536, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); },
                async () => { chatGpt = await FetchAsync(proxy, ChatGptUrl, 8_192, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); },
                async () => { telegram = await FetchAsync(proxy, TelegramUrl, 65_536, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); },
                async () => { integrity = await FetchAsync(proxy, $"https://speed.cloudflare.com/__down?bytes={integrityBytes}", integrityBytes,
                    TimeSpan.FromSeconds(integrityBytes >= 4_194_304 ? 25 : 12), cancellationToken).ConfigureAwait(false); }
            ).ConfigureAwait(false);
            remote = await remoteTask.ConfigureAwait(false);
            var openAiOk = ValidateOpenAi(openAi);
            var trace = ParseTrace(chatGpt);
            var telegramOk = telegram.Status is 200 or 302 or 404 && telegram.FailureClass == "Passed";
            var integrityOk = integrity.Status == 200 && integrity.Body.Length == integrityBytes && integrity.FailureClass == "Passed";
            var throughput = integrityOk
                ? integrityBytes * 8d / Math.Max(0.001, integrity.ElapsedMs / 1_000d) / 1_000_000d
                : 0;
            var coreFailures = new[] { openAi.FailureClass, chatGpt.FailureClass, telegram.FailureClass,
                integrity.FailureClass };
            var failure = coreFailures.FirstOrDefault(value => value != "Passed") ?? "Passed";
            return new ProbeRoundSample(
                openAiOk,
                trace is not null,
                telegramOk,
                integrityOk,
                remote.Passed,
                openAi.ElapsedMs,
                chatGpt.ElapsedMs,
                telegram.ElapsedMs,
                integrity.ElapsedMs,
                throughput,
                trace?.IpHash ?? "",
                trace?.Country ?? "",
                failure,
                coreFailures.Any(IsHardFailure),
                await googleTask.ConfigureAwait(false),
                [
                    Evidence("OpenAI", openAi, openAiOk),
                    Evidence("ChatGPT", chatGpt, trace is not null),
                    Evidence("Telegram", telegram, telegramOk),
                    Evidence("Integrity", integrity, integrityOk)
                ]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(openAi.Body);
            CryptographicOperations.ZeroMemory(chatGpt.Body);
            CryptographicOperations.ZeroMemory(telegram.Body);
            CryptographicOperations.ZeroMemory(integrity.Body);
        }
    }

    internal static async Task RunProbeScheduleAsync(bool healthOnly, params Func<Task>[] probes)
    {
        if (healthOnly) await Task.WhenAll(probes.Select(probe => probe())).ConfigureAwait(false);
        else foreach (var probe in probes) await probe().ConfigureAwait(false);
    }

    private static ProbeEndpointEvidence Evidence(string name, FetchResult result, bool passed) =>
        new(name, passed, result.Status, Math.Round(result.ElapsedMs, 1),
            passed ? "Passed" : result.FailureClass != "Passed" ? result.FailureClass : "ResponseRejected");

    private static async Task<FetchResult> FetchAsync(
        IWebProxy proxy,
        string url,
        int maximumBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = proxy,
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = false,
            // Windows revocation endpoints are frequently unreachable through
            // third-party proxy routes. Normal chain, hostname and signature
            // validation remain enabled; revocation availability is not used
            // as a false node-health signal.
            CheckCertificateRevocationList = false
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ART-VPN-Qualification/3");
        client.DefaultRequestHeaders.ConnectionClose = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            var body = await ReadLimitedAsync(response.Content, maximumBytes, linked.Token).ConfigureAwait(false);
            return new FetchResult((int)response.StatusCode, body, watch.Elapsed.TotalMilliseconds, "Passed");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or AuthenticationException)
        {
            return new FetchResult(0, [], watch.Elapsed.TotalMilliseconds, Classify(ex));
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maximum)
            throw new IOException("ProbeBodyTooLarge");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > maximum) throw new IOException("ProbeBodyTooLarge");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static bool ValidateOpenAi(FetchResult result)
    {
        if (result.Status != 401 || result.FailureClass != "Passed" || result.Body.Length > 65_536) return false;
        try
        {
            using var document = JsonDocument.Parse(result.Body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static TraceIdentity? ParseTrace(FetchResult result)
    {
        if (result.Status != 200 || result.FailureClass != "Passed" || result.Body.Length > 8_192) return null;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(result.Body); }
        catch (DecoderFallbackException) { return null; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var split = line.IndexOf('=');
            if (split < 1 || !values.TryAdd(line[..split], line[(split + 1)..])) return null;
        }
        if (values.GetValueOrDefault("h") != "chatgpt.com" ||
            !IPAddress.TryParse(values.GetValueOrDefault("ip"), out _) ||
            values.GetValueOrDefault("loc")?.Length != 2 || values.GetValueOrDefault("colo")?.Length is < 3 or > 4 ||
            values.GetValueOrDefault("tls") is not ("TLSv1.2" or "TLSv1.3")) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(values["ip"]))).ToLowerInvariant()[..16];
        return new TraceIdentity(hash, values["loc"].ToUpperInvariant());
    }

    private static async Task<RemoteConnectResult> ProbeHttpConnectAsync(
        int proxyPort,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token).ConfigureAwait(false);
            var request = Encoding.ASCII.GetBytes($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\nProxy-Connection: close\r\n\r\n");
            try { await client.GetStream().WriteAsync(request, timeout.Token).ConfigureAwait(false); }
            finally { Array.Clear(request); }
            var response = new byte[512];
            try
            {
                var count = await client.GetStream().ReadAsync(response, timeout.Token).ConfigureAwait(false);
                var first = Encoding.ASCII.GetString(response, 0, count).Split("\r\n", 2, StringSplitOptions.None)[0];
                return new RemoteConnectResult(first.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) ||
                                               first.StartsWith("HTTP/1.0 200", StringComparison.Ordinal),
                    first.StartsWith("HTTP/", StringComparison.Ordinal) ? "Passed" : "RemoteConnectRejected");
            }
            finally { Array.Clear(response); }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            return new RemoteConnectResult(false, Classify(ex));
        }
    }

    private static string Classify(Exception exception)
    {
        var text = exception.ToString();
        if (text.Contains("bad record mac", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("decrypt", StringComparison.OrdinalIgnoreCase)) return "TlsBadRecordMac";
        if (exception is OperationCanceledException) return "Timeout";
        if (exception is AuthenticationException || text.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SSL", StringComparison.OrdinalIgnoreCase)) return "TlsIntegrityFailure";
        if (text.Contains("reset", StringComparison.OrdinalIgnoreCase)) return "ConnectionReset";
        if (text.Contains("partial", StringComparison.OrdinalIgnoreCase) || text.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase))
            return "PartialTransfer";
        return "TransportFailure";
    }

    private static bool IsHardFailure(string value) => value != "Passed";

    private sealed record FetchResult(int Status, byte[] Body, double ElapsedMs, string FailureClass);
    private sealed record TraceIdentity(string IpHash, string Country);
    private sealed record RemoteConnectResult(bool Passed, string FailureClass);
}

internal static class NodeQualificationTests
{
    public static async Task<NodeQualificationTestReceipt> RunAsync()
    {
        var nodes = Enumerable.Range(0, 4).Select(index => new ProviderNodeView(
            "n-" + index, "node", index % 2 == 0 ? "Нидерланды" : "Германия", "tcp", "server-" + index,
            index % 2 == 0 ? 443 : 8443, 24000 + index, new string((char)('a' + index), 32))).ToArray();
        var calls = 0;
        Task<ProbeRoundSample> Good(int port, int bytes, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ProbeRoundSample(true, true, true, true, true,
                70 + port % 10, 80 + port % 10, 75, 500, 20, "ip-" + port, "NL", "Passed", false));
        }
        var results = await NodeQualificationRunner.RunAsync(nodes, 2, 262_144, 2, Good, CancellationToken.None)
            .ConfigureAwait(false);
        Assert(results.Length == 4 && calls == 8 && results.All(item => item.Clean && item.RemoteConnectPct == 100));
        var rotatingSamples = new[]
        {
            new ProbeRoundSample(true,true,true,true,true,70,80,75,500,20,"ip-a","NL","Passed",false),
            new ProbeRoundSample(true,true,true,true,true,70,80,75,500,20,"ip-b","NL","Passed",false)
        };
        Assert(!NodeQualificationRunner.Summarize(nodes[0], 2, rotatingSamples).Clean);
        var badMac = rotatingSamples.Select(sample => sample with
        {
            EgressIpHash = "ip-a", FailureClass = "TlsBadRecordMac", HardDegradation = true, Integrity = false
        }).ToArray();
        var rejected = NodeQualificationRunner.Summarize(nodes[0], 2, badMac);
        Assert(!rejected.Clean && rejected.TlsIntegrityFailure);
        var noRemote = rotatingSamples.Select(sample => sample with { EgressIpHash = "ip-a", RemoteConnect = false }).ToArray();
        var diagnosticOnly = NodeQualificationRunner.Summarize(nodes[0], 2, noRemote);
        Assert(diagnosticOnly.Clean && diagnosticOnly.RemoteConnectPct == 0);
        var mostlyStable = Enumerable.Range(0, 6).Select(_ => rotatingSamples[0] with { EgressIpHash = "ip-a" }).ToArray();
        mostlyStable[2] = mostlyStable[2] with
        {
            OpenAi = false, ChatGpt = false, Telegram = false, Integrity = false,
            FailureClass = "Timeout", HardDegradation = true, EgressIpHash = "", ObservedCountry = ""
        };
        Assert(NodeQualificationRunner.Summarize(nodes[0], 6, mostlyStable).Clean);
        mostlyStable[4] = mostlyStable[2];
        Assert(!NodeQualificationRunner.Summarize(nodes[0], 6, mostlyStable).Clean);
        foreach (var fast in new[] { false, true })
        {
            var entered = 0;
            var peak = 0;
            var completed = 0;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = LiveProbeTransport.RunProbeScheduleAsync(fast,
                Enumerable.Range(0, 5).Select(_ => (Func<Task>)(async () =>
                {
                    var active = Interlocked.Increment(ref entered);
                    peak = Math.Max(peak, active);
                    if (fast) await release.Task.ConfigureAwait(false);
                    await Task.Yield();
                    Interlocked.Decrement(ref entered);
                    Interlocked.Increment(ref completed);
                })).ToArray());
            // Starting the schedule runs each async callback to its first await.
            // No wall-clock timing assumption or external network is needed.
            if (fast) { Assert(entered == 5); release.TrySetResult(); }
            await pending.ConfigureAwait(false);
            Assert(completed == 5 && peak == (fast ? 5 : 1));
        }
        return new NodeQualificationTestReceipt("Passed", 15, true, true, true, true, false);
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("NodeQualificationInvariantFailed");
    }
}

internal sealed record NodeQualificationTestReceipt(
    string Status,
    int Scenarios,
    bool OpenAiFirst,
    bool RemoteConnectDiagnosticOnly,
    bool EgressRotationRejected,
    bool BadMacQuarantined,
    bool SecretDisplayed);
