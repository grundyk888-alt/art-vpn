using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ArtSport.ArtVpn.Service;

internal sealed record QualifiedGenerationCandidate(
    string GenerationId,
    string Slot,
    int ProxyPort,
    int ApiPort,
    string ConfigPath,
    string PrimaryTag,
    string PrimaryCountry,
    QualifiedPool Pool,
    string CoreSha256,
    string RulesManifestSha256,
    string QualifiedAtUtc);

internal static class CoreQualificationEngine
{
    public static async Task<QualifiedGenerationCandidate> QualifyAsync(
        RuntimeOptions options,
        StagedProviderGeneration staged,
        ProbeRoundTransport? transport,
        CancellationToken cancellationToken,
        bool quickConnect = false)
    {
        var assets = await BypassRuleRefresh.ForCandidateAsync(options, cancellationToken,
            deferRefresh: quickConnect).ConfigureAwait(false);
        var material = StagedGenerationReader.Open(options, staged);
        var runtimeProbeConfigPath = PreparePrivateProbeRuntimeConfig(material);
        await CoreProcessLease.CheckConfigAsync(assets.CorePath, runtimeProbeConfigPath, cancellationToken)
            .ConfigureAwait(false);
        await using var probe = await CoreProcessLease.StartAsync(
            assets.CorePath,
            runtimeProbeConfigPath,
            material.Manifest.Select(node => node.ProxyPort).ToArray(),
            cancellationToken).ConfigureAwait(false);
        var run = transport ?? LiveProbeTransport.RunAsync;
        var active = ActiveGenerationStore.TryRead(options);
        var live = active is null ? null : await LiveSelectorStatusReader.TryReadAsync(options, cancellationToken).ConfigureAwait(false);
        var incumbent = live?.CurrentTag ?? active?.PrimaryTag ?? string.Empty;
        var previousPool = active is null ? null : new[] { incumbent };
        NodeProbeEvidence[] prefilter, deep;
        ProviderNodeView[] testedNodes;
        if (quickConnect)
        {
            (testedNodes, deep, prefilter) = await FirstConnectionQualification.FirstPassAsync(
                material.Manifest, incumbent,
                (nodes, token) => NodeQualificationRunner.RunAsync(nodes, 1, 262_144, 8, run, token),
                (nodes, token) => NodeQualificationRunner.RunAsync(nodes, 6, 262_144, 4, run, token),
                cancellationToken, bootstrapOnly: true).ConfigureAwait(false);
        }
        else
        {
            prefilter = await NodeQualificationRunner.RunAsync(
                material.Manifest, 1, 262_144, 8, run, cancellationToken).ConfigureAwait(false);
            var shortlist = QualificationPolicy.Shortlist(material.Manifest, prefilter, previousPool, maximum: 24);
            (testedNodes, deep) = await FirstConnectionQualification.RunAsync(
                shortlist, incumbent, false,
                (nodes, token) => NodeQualificationRunner.RunAsync(nodes, 6, 262_144, 4, run, token),
                cancellationToken).ConfigureAwait(false);
        }
        AtomicFile.WriteJson(Path.Combine(material.GenerationRoot, "prefilter-summary.v1.json"),
            PrefilterSummary.Create(staged.GenerationId, prefilter));
        AtomicFile.WriteJson(Path.Combine(material.GenerationRoot, "deep-summary.v1.json"),
            PrefilterSummary.Create(staged.GenerationId, deep, "deep"));
        var pool = QualificationPolicy.Select(testedNodes, deep, incumbent,
            requiredRounds: quickConnect ? 1 : 6, maximum: 12);

        var slot = active?.Slot == "A" ? "B" : "A";
        var proxyPort = slot == "A" ? 22086 : 22087;
        var apiPort = slot == "A" ? 22096 : 22097;
        var slotRoot = Path.Combine(material.GenerationRoot, "runtime", slot.ToLowerInvariant());
        Directory.CreateDirectory(slotRoot);
        var configPath = Path.Combine(slotRoot, "config.json");
        var privateProbe = JsonNode.Parse(File.ReadAllText(material.ProbeConfigPath, Encoding.UTF8),
                               documentOptions: JsonSettings.Document) as JsonObject
                           ?? throw new InvalidDataException("ProbeConfigRejected");
        var runtime = RuntimeConfigBuilder.Build(privateProbe, pool, assets.Rules, proxyPort, apiPort,
            Path.Combine(slotRoot, "cache.db"), Path.Combine(slotRoot, "runtime.log"));
        var bytes = Encoding.UTF8.GetBytes(runtime.ToJsonString(JsonSettings.Output));
        try { AtomicFile.WriteBytes(configPath, bytes); }
        finally { Array.Clear(bytes); }
        await CoreProcessLease.CheckConfigAsync(assets.CorePath, configPath, cancellationToken).ConfigureAwait(false);

        var receipt = new QualificationReceipt(
            1,
            "art-vpn-generation-qualification",
            staged.GenerationId,
            "Passed",
            slot,
            pool.PrimaryTag,
            pool.PoolTags,
            deep,
            assets.CoreSha256,
            assets.RulesManifestSha256,
            DateTimeOffset.UtcNow.ToString("o"),
            false,
            false);
        AtomicFile.WriteJson(Path.Combine(material.GenerationRoot, "qualification.v1.json"), receipt);
        var primary = deep.Single(item => item.Tag == pool.PrimaryTag);
        return new QualifiedGenerationCandidate(
            staged.GenerationId,
            slot,
            proxyPort,
            apiPort,
            configPath,
            pool.PrimaryTag,
            primary.Country,
            pool,
            assets.CoreSha256,
            assets.RulesManifestSha256,
            receipt.QualifiedAtUtc);
    }

    private static string PreparePrivateProbeRuntimeConfig(StagedGenerationMaterial material)
    {
        var source = JsonNode.Parse(File.ReadAllText(material.ProbeConfigPath, Encoding.UTF8),
                         documentOptions: JsonSettings.Document) as JsonObject
                     ?? throw new InvalidDataException("ProbeConfigRejected");
        var logPath = Path.Combine(material.GenerationRoot, "probe-runtime.log");
        source["log"] = new JsonObject
        {
            ["level"] = "warn",
            ["output"] = logPath,
            ["timestamp"] = true
        };
        var runtimeConfigPath = Path.Combine(material.GenerationRoot, "probe-runtime-config.json");
        var bytes = Encoding.UTF8.GetBytes(source.ToJsonString(JsonSettings.Output));
        try { AtomicFile.WriteBytes(runtimeConfigPath, bytes); }
        finally { Array.Clear(bytes); }
        return runtimeConfigPath;
    }
}

internal sealed record PrefilterSummary(
    int Schema,
    string Kind,
    string GenerationId,
    int Tested,
    int Clean,
    int OpenAiPassed,
    int ChatGptPassed,
    int TelegramPassed,
    int IntegrityPassed,
    int RemoteConnectPassed,
    int StableEgress,
    Dictionary<string, int> FailureClasses,
    string AtUtc,
    bool ContainsProviderSecret)
{
    public static PrefilterSummary Create(string generationId, IReadOnlyList<NodeProbeEvidence> evidence,
        string phase = "prefilter") => new(
        1,
        phase == "deep" ? "art-vpn-deep-summary" : "art-vpn-prefilter-summary",
        generationId,
        evidence.Count,
        evidence.Count(item => item.Clean),
        evidence.Count(item => item.OpenAiPct == 100),
        evidence.Count(item => item.ChatGptPct == 100),
        evidence.Count(item => item.TelegramPct == 100),
        evidence.Count(item => item.IntegrityPct == 100),
        evidence.Count(item => item.RemoteConnectPct == 100),
        evidence.Count(item => item.EgressStable),
        evidence.GroupBy(item => item.FailureClass, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
        DateTimeOffset.UtcNow.ToString("o"),
        false);
}

internal sealed record StagedGenerationMaterial(
    string GenerationRoot,
    string ProbeConfigPath,
    ProviderNodeView[] Manifest);

internal static class StagedGenerationReader
{
    public static StagedGenerationMaterial Open(RuntimeOptions options, StagedProviderGeneration staged)
    {
        if (!Regex.IsMatch(staged.GenerationId, "^[0-9]{8}T[0-9]{6}Z-[a-f0-9]{12}$", RegexOptions.CultureInvariant) ||
            staged.Schema != 1 || staged.Kind != "art-vpn-staged-provider-generation" || staged.Status != "Quarantined" ||
            staged.Promoted || staged.ContainsProviderSecret)
            throw new InvalidDataException("StagedGenerationIdentityRejected");
        var root = Path.GetFullPath(options.PrivateGenerationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var generationRoot = Path.GetFullPath(Path.Combine(options.PrivateGenerationRoot, staged.GenerationId));
        if (!generationRoot.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(generationRoot))
            throw new InvalidDataException("StagedGenerationPathRejected");
        var receipt = JsonSerializer.Deserialize<StagedProviderGeneration>(
                          File.ReadAllText(Path.Combine(generationRoot, "generation.v1.json"), Encoding.UTF8), JsonSettings.Strict)
                      ?? throw new InvalidDataException("StagedGenerationReceiptMissing");
        if (receipt.Schema != staged.Schema || receipt.Kind != staged.Kind || receipt.GenerationId != staged.GenerationId ||
            receipt.Status != staged.Status || receipt.Adapter != staged.Adapter || receipt.SourceLines != staged.SourceLines ||
            receipt.SupportedNodes != staged.SupportedNodes ||
            !receipt.Countries.SequenceEqual(staged.Countries, StringComparer.Ordinal) ||
            receipt.ManifestSha256 != staged.ManifestSha256 || receipt.StagedAtUtc != staged.StagedAtUtc ||
            receipt.Promoted != staged.Promoted || receipt.ContainsProviderSecret != staged.ContainsProviderSecret)
            throw new InvalidDataException("StagedGenerationReceiptChanged");
        var manifestPath = Path.Combine(generationRoot, "manifest.v1.json");
        var manifest = JsonSerializer.Deserialize<ProviderNodeView[]>(File.ReadAllText(manifestPath, Encoding.UTF8), JsonSettings.Strict)
                       ?? throw new InvalidDataException("StagedManifestMissing");
        if (manifest.Length != staged.SupportedNodes || manifest.Length < 3 ||
            manifest.Select(node => node.Tag).Distinct(StringComparer.Ordinal).Count() != manifest.Length ||
            manifest.Any(node => node.ProxyPort is < 24000 or > 24999 || node.ProbeIdentity.Length != 32))
            throw new InvalidDataException("StagedManifestRejected");
        var probeConfig = Path.Combine(generationRoot, "probe-config.json");
        if (!File.Exists(probeConfig)) throw new InvalidDataException("StagedProbeConfigMissing");
        return new StagedGenerationMaterial(generationRoot, probeConfig, manifest);
    }
}

internal sealed class CoreProcessLease : ICoreLease
{
    private readonly Process _process;
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly ChildProcessJob _job;
    private bool _disposed;

    private CoreProcessLease(Process process, Task stdout, Task stderr, ChildProcessJob job)
    {
        _process = process;
        _stdout = stdout;
        _stderr = stderr;
        _job = job;
    }

    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;

    public static async Task CheckConfigAsync(string corePath, string configPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLaunchPaths(corePath, configPath);
        await CheckWithRetryAsync(async token =>
        {
            using var process = NewProcess(corePath, "check", configPath);
            await CheckProcessAsync(process, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        }, token => Task.Delay(1_000, token), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CheckWithRetryAsync(Func<CancellationToken, Task> check,
        Func<CancellationToken, Task> delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await check(token).ConfigureAwait(false); }
        catch (InvalidDataException ex) when (ex.Message == "CoreCheckTimedOut" && !token.IsCancellationRequested)
        {
            // A cold launch/temporary contention is not an invalid subscription.
            // The first owned check process has already been reaped; retry once.
            await delay(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await check(token).ConfigureAwait(false);
        }
    }

    internal static async Task CheckProcessAsync(Process process, TimeSpan maximumDuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("CoreCheckStartRejected");
        try
        {
            using var job = ChildProcessJob.CreateAndAssign(process);
            var stdout = DrainAsync(process.StandardOutput, cancellationToken);
            var stderr = DrainAsync(process.StandardError, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(maximumDuration);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal service shutdown is not a failed or timed-out config.
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new InvalidDataException("CoreCheckTimedOut");
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidDataException("CoreConfigRejected");
        }
        catch
        {
            // Also covers assignment failure, before ownership by a job exists.
            TryKill(process);
            throw;
        }
    }

    public static async Task<CoreProcessLease> StartAsync(
        string corePath,
        string configPath,
        IReadOnlyCollection<int> expectedPorts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLaunchPaths(corePath, configPath);
        if (expectedPorts.Count < 1 || expectedPorts.Any(port => port is < 1024 or > 65535) ||
            expectedPorts.Distinct().Count() != expectedPorts.Count)
            throw new InvalidDataException("CoreExpectedPortsRejected");
        var process = NewProcess(corePath, "run", configPath);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("CoreStartRejected");
        }
        ChildProcessJob job;
        try { job = ChildProcessJob.CreateAndAssign(process); }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }
        var lease = new CoreProcessLease(process,
            DrainAsync(process.StandardOutput, cancellationToken), DrainAsync(process.StandardError, cancellationToken), job);
        try
        {
            await WaitPortsAsync(process, expectedPorts, cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        TryKill(_process);
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        try { await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _job.Dispose();
        _process.Dispose();
    }

    private static Process NewProcess(string corePath, string verb, string configPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = corePath,
            WorkingDirectory = Path.GetDirectoryName(corePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(verb);
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(configPath);
        return new Process { StartInfo = info, EnableRaisingEvents = true };
    }

    private static async Task WaitPortsAsync(Process process, IReadOnlyCollection<int> ports, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited) throw new InvalidDataException("CoreExitedBeforeReady");
            var all = true;
            foreach (var port in ports)
            {
                using var client = new TcpClient(AddressFamily.InterNetwork);
                try { await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false); }
                catch (SocketException) { all = false; break; }
            }
            if (all) return;
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
        throw new TimeoutException("CorePortsUnavailable");
    }

    private static void ValidateLaunchPaths(string corePath, string configPath)
    {
        if (!Path.IsPathFullyQualified(corePath) || !Path.IsPathFullyQualified(configPath) ||
            !File.Exists(corePath) || !File.Exists(configPath) ||
            !string.Equals(Path.GetExtension(corePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(configPath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CoreLaunchPathRejected");
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
                Array.Clear(buffer);
        }
        catch { }
        finally { Array.Clear(buffer); }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}

internal sealed class ChildProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private ChildProcessJob(SafeFileHandle handle) => _handle = handle;

    public static ChildProcessJob CreateAndAssign(Process process)
    {
        var raw = CreateJobObject(IntPtr.Zero, null);
        if (raw == IntPtr.Zero) throw new InvalidOperationException("CoreJobCreateRejected");
        var handle = new SafeFileHandle(raw, ownsHandle: true);
        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            if (!SetInformationJobObject(handle, 9, ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                throw new InvalidOperationException("CoreJobPolicyRejected");
            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new InvalidOperationException("CoreJobAssignmentRejected");
            return new ChildProcessJob(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

internal static class CoreProcessJobTests
{
    public static async Task<CoreProcessJobTestReceipt> RunAsync()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("TestExecutableUnavailable");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--job-test-child");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("TestChildStartRejected");
        await Task.Delay(150).ConfigureAwait(false);
        if (child.HasExited) throw new InvalidOperationException("TestChildExitedEarly");
        using (var job = ChildProcessJob.CreateAndAssign(child)) { }
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch
        {
            try { child.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("JobCloseDidNotTerminateChild");
        }
        return new CoreProcessJobTestReceipt("Passed", 4, true, true, false);
    }
}

internal sealed record CoreProcessJobTestReceipt(
    string Status,
    int Scenarios,
    bool AssignedToKillOnCloseJob,
    bool ChildTerminatedOnJobClose,
    bool SecretDisplayed);

internal static class ActiveGenerationStore
{
    public static string PathFor(RuntimeOptions options) => Path.Combine(options.DataRoot, "state", "active-generation.v1.json");

    public static ActiveGenerationState? TryRead(RuntimeOptions options)
    {
        var path = PathFor(options);
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<ActiveGenerationState>(File.ReadAllText(path, Encoding.UTF8), JsonSettings.Strict);
            if (state is null || state.Schema != 1 || state.Kind != "art-vpn-active-generation" ||
                state.Slot is not ("A" or "B") || state.ProxyPort != (state.Slot == "A" ? 22086 : 22087) ||
                state.ApiPort != (state.Slot == "A" ? 22096 : 22097) || state.ContainsProviderSecret)
                return null;
            return state;
        }
        catch { return null; }
    }
}

internal sealed record ActiveGenerationState(
    int Schema,
    string Kind,
    string GenerationId,
    string Slot,
    int ProxyPort,
    int ApiPort,
    string ConfigPath,
    string PrimaryTag,
    string PrimaryCountry,
    string ActivatedAtUtc,
    long FrontRevision,
    bool ContainsProviderSecret);

internal sealed record QualificationReceipt(
    int Schema,
    string Kind,
    string GenerationId,
    string Status,
    string Slot,
    string PrimaryTag,
    string[] PoolTags,
    NodeProbeEvidence[] Evidence,
    string CoreSha256,
    string RulesManifestSha256,
    string QualifiedAtUtc,
    bool ActiveChannelChanged,
    bool ContainsProviderSecret);

internal static class CoreRuntimeContractTests
{
    public static async Task<CoreRuntimeContractTestReceipt> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-core-contract-" + Guid.NewGuid().ToString("N"));
        var options = RuntimeOptions.Test(root, "ARTSPORT.ARTVpn.CoreContract." + Guid.NewGuid().ToString("N"));
        var provider = Encoding.UTF8.GetBytes(string.Join('\n', new[]
        {
            Node("00000000-1111-2222-3333-444444444461", "198.51.100.31", 443, "Нидерланды"),
            Node("00000000-1111-2222-3333-444444444462", "198.51.100.32", 8443, "Германия"),
            Node("00000000-1111-2222-3333-444444444463", "198.51.100.33", 9443, "Эстония")
        }));
        try
        {
            var staged = await ProviderRefreshEngine.StageAsync(options,
                Encoding.UTF8.GetBytes("https://example.invalid/provider"),
                (_, _) => Task.FromResult(Encoding.ASCII.GetBytes(Convert.ToBase64String(provider))),
                false,
                CancellationToken.None).ConfigureAwait(false);
            var material = StagedGenerationReader.Open(options, staged);
            Assert(material.Manifest.Length == 3 && File.Exists(material.ProbeConfigPath));
            var changed = staged with { GenerationId = "../escape" };
            try
            {
                _ = StagedGenerationReader.Open(options, changed);
                throw new InvalidOperationException("GenerationTraversalAccepted");
            }
            catch (InvalidDataException) { }
            Assert(ActiveGenerationStore.TryRead(options) is null);
            return new CoreRuntimeContractTestReceipt("Passed", 8, true, true, true, false);
        }
        finally
        {
            Array.Clear(provider);
            var resolved = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("art-vpn-core-contract-", StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static string Node(string uuid, string host, int port, string country) =>
        $"vless://{uuid}@{host}:{port}?security=reality&type=tcp&sni=example.com&pbk=abcdefghijklmnopqrstuvwxyzABCDEFGH123456789#{country}";

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("CoreRuntimeContractInvariantFailed");
    }
}

internal sealed record CoreRuntimeContractTestReceipt(
    string Status,
    int Scenarios,
    bool StagedMaterialBound,
    bool TraversalRejected,
    bool ActiveGenerationUntouched,
    bool SecretDisplayed);
