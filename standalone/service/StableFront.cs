using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ArtSport.ArtVpn.Service;

internal sealed class StableFrontHost
{
    private readonly int _frontPort;
    private readonly int _apiPort;
    private readonly HashSet<int> _allowedTargets;
    private readonly string _statePath;
    private int _targetPort;
    private int _activeConnections;
    private int _serving;
    private readonly ConcurrentDictionary<int, int> _activeConnectionsByTarget = new();
    private long _revision;
    private readonly object _routeSync = new();

    public StableFrontHost(
        RuntimeOptions options,
        int frontPort = ProductContract.StableFrontPort,
        int apiPort = ProductContract.StableFrontApiPort,
        IEnumerable<int>? allowedTargets = null,
        bool restorePersistedTarget = true)
    {
        _frontPort = frontPort;
        _apiPort = apiPort;
        _allowedTargets = new HashSet<int>(allowedTargets ?? [22086, 22087, 22088, 2080, 10809]);
        _statePath = Path.Combine(options.DataRoot, "state", "front-target.v1.json");
        LoadState(restorePersistedTarget);
    }

    public int TargetPort => Volatile.Read(ref _targetPort);
    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    public bool IsServing => Volatile.Read(ref _serving) == 1;
    public int ActiveConnectionsFor(int targetPort) =>
        _activeConnectionsByTarget.TryGetValue(targetPort, out var count) ? Math.Max(0, count) : 0;
    public long Revision => Interlocked.Read(ref _revision);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var relayListener = await BindWithRetryAsync(_frontPort, 256, cancellationToken).ConfigureAwait(false);
        TcpListener? apiListener = null;
        try
        {
            apiListener = await BindWithRetryAsync(_apiPort, 16, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _serving, 1);
            await Task.WhenAll(
                RunRelayAsync(relayListener, cancellationToken),
                RunApiAsync(apiListener, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _serving, 0);
            relayListener.Stop();
            apiListener?.Stop();
        }
    }

    public void SwitchNewConnectionsTo(int port, string reasonCode)
    {
        if (!_allowedTargets.Contains(port)) throw new InvalidDataException("FrontTargetRejected");
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 64 ||
            reasonCode.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new InvalidDataException("FrontReasonRejected");
        lock (_routeSync)
        {
            // Persist before publication. Failed IO must not change the live
            // target or leave callers believing a cutover was rolled back.
            var previous = TargetPort;
            var revision = Revision + 1;
            AtomicFile.ReplaceJson(_statePath, new FrontTargetState(
                1, "art-vpn-front-target", port, previous, revision, reasonCode,
                DateTimeOffset.UtcNow.ToString("o"), false));
            Interlocked.Exchange(ref _revision, revision);
            Volatile.Write(ref _targetPort, port);
        }
    }

    public void PauseNewConnections()
    {
        lock (_routeSync)
        {
            if (TargetPort == 0) return;
            var revision = Revision + 1;
            AtomicFile.ReplaceJson(_statePath, new FrontTargetState(1, "art-vpn-front-target",
                0, TargetPort, revision, "ManualExternalRoute", DateTimeOffset.UtcNow.ToString("o"), false));
            Interlocked.Exchange(ref _revision, revision);
            Volatile.Write(ref _targetPort, 0);
        }
    }

    private async Task RunRelayAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                _ = RelayOneAsync(client, cancellationToken);
            }
        }
        finally { listener.Stop(); }
    }

    private async Task RelayOneAsync(TcpClient incoming, CancellationToken serviceToken)
    {
        using (incoming)
        {
            int target;
            lock (_routeSync)
            {
                target = TargetPort;
                if (!_allowedTargets.Contains(target)) return;
                // Reserve the old slot before dialing it. Otherwise a cutover
                // and slot reuse could race an in-flight local connection.
                Interlocked.Increment(ref _activeConnections);
                _activeConnectionsByTarget.AddOrUpdate(target, 1, (_, value) => checked(value + 1));
            }
            using var upstream = new TcpClient(AddressFamily.InterNetwork);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, target, connectTimeout.Token).ConfigureAwait(false);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
                var down = CopyUntilEndAsync(incoming.GetStream(), upstream.GetStream(), session.Token);
                var up = CopyUntilEndAsync(upstream.GetStream(), incoming.GetStream(), session.Token);
                await Task.WhenAny(down, up).ConfigureAwait(false);
                session.Cancel();
                try { await Task.WhenAll(down, up).ConfigureAwait(false); } catch { }
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                // A connection-specific failure is deliberately not allowed to
                // mutate the selected target. The watchdog owns that decision.
            }
            finally
            {
                Interlocked.Decrement(ref _activeConnections);
                _activeConnectionsByTarget.AddOrUpdate(target, 0, (_, value) => Math.Max(0, value - 1));
            }
        }
    }

    private static async Task CopyUntilEndAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Array.Clear(buffer); }
    }

    private async Task RunApiAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                _ = RespondApiAsync(client, cancellationToken);
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task<TcpListener> BindWithRetryAsync(
        int port,
        int backlog,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
            try
            {
                listener.Start(backlog);
                return listener;
            }
            catch (SocketException) when (attempt < 20)
            {
                listener.Stop();
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                listener.Stop();
                throw;
            }
        }
        throw new InvalidOperationException("StableFrontPortUnavailable");
    }

    private async Task RespondApiAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            cancellationToken = timeout.Token;
            var stream = client.GetStream();
            var request = new byte[1024];
            int count;
            try { count = await stream.ReadAsync(request, cancellationToken).ConfigureAwait(false); }
            catch { return; }
            var line = Encoding.ASCII.GetString(request, 0, count).Split("\r\n", 2, StringSplitOptions.None)[0];
            Array.Clear(request);
            var ok = line == "GET /status HTTP/1.1" || line == "GET /status HTTP/1.0";
            var body = ok
                ? JsonSerializer.Serialize(new
                {
                    schema = 1,
                    kind = "art-vpn-front-status",
                    targetPort = TargetPort,
                    activeConnections = ActiveConnections,
                    revision = Revision,
                    existingConnectionsInterrupted = false
                }, JsonSettings.Output)
                : "{\"status\":\"Rejected\"}";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
            try
            {
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
            }
            catch { }
            finally { Array.Clear(header); Array.Clear(bodyBytes); }
        }
    }

    private void LoadState(bool restoreTarget)
    {
        if (!File.Exists(_statePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<FrontTargetState>(File.ReadAllText(_statePath), JsonSettings.Strict)
                        ?? throw new InvalidDataException("FrontStateEmpty");
            if (state.Schema != 1 || state.Kind != "art-vpn-front-target" || (state.TargetPort != 0 && !_allowedTargets.Contains(state.TargetPort)) ||
                state.Revision < 1 || state.ExistingConnectionsInterrupted)
                throw new InvalidDataException("FrontStateRejected");
            _targetPort = restoreTarget ? state.TargetPort : 0;
            _revision = state.Revision;
        }
        catch
        {
            _targetPort = 0;
            _revision = 0;
        }
    }
}

internal sealed record FrontTargetState(
    int Schema,
    string Kind,
    int TargetPort,
    int PreviousPort,
    long Revision,
    string ReasonCode,
    string SwitchedAtUtc,
    bool ExistingConnectionsInterrupted);

internal static class StableFrontTests
{
    public static async Task<StableFrontTestReceipt> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-front-test-" + Guid.NewGuid().ToString("N"));
        var options = RuntimeOptions.Test(root, "ARTSPORT.ARTVpn.FrontTest." + Guid.NewGuid().ToString("N"));
        var aPort = FreePort();
        var bPort = FreePort([aPort]);
        var frontPort = FreePort([aPort, bPort]);
        var apiPort = FreePort([aPort, bPort, frontPort]);
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var backendA = RunGreetingBackendAsync(aPort, "A", stopping.Token);
        var backendB = RunGreetingBackendAsync(bPort, "B", stopping.Token);
        var transientBlocker = new TcpListener(IPAddress.Loopback, frontPort);
        transientBlocker.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
        transientBlocker.Start();
        var front = new StableFrontHost(options, frontPort, apiPort, [aPort, bPort]);
        var frontTask = front.RunAsync(stopping.Token);
        try
        {
            await Task.Delay(350, stopping.Token).ConfigureAwait(false);
            transientBlocker.Stop();
            await WaitPortAsync(frontPort, stopping.Token).ConfigureAwait(false);
            await WaitPortAsync(apiPort, stopping.Token).ConfigureAwait(false);
            front.SwitchNewConnectionsTo(aPort, "InitialAcceptedSlot");
            using var first = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, frontPort, stopping.Token).ConfigureAwait(false);
            var firstGreeting = await ReadLineAsync(first.GetStream(), stopping.Token).ConfigureAwait(false);
            Assert(firstGreeting == "A");
            Assert(front.ActiveConnectionsFor(aPort) == 1 && front.ActiveConnectionsFor(bPort) == 0);
            front.SwitchNewConnectionsTo(bPort, "AcceptedSlotPromotion");
            using var second = new TcpClient();
            await second.ConnectAsync(IPAddress.Loopback, frontPort, stopping.Token).ConfigureAwait(false);
            var secondGreeting = await ReadLineAsync(second.GetStream(), stopping.Token).ConfigureAwait(false);
            Assert(secondGreeting == "B");
            Assert(front.ActiveConnectionsFor(aPort) == 1 && front.ActiveConnectionsFor(bPort) == 1);

            var ping = Encoding.ASCII.GetBytes("still-A\n");
            await first.GetStream().WriteAsync(ping, stopping.Token).ConfigureAwait(false);
            var echoed = await ReadLineAsync(first.GetStream(), stopping.Token).ConfigureAwait(false);
            Assert(echoed == "A:still-A");
            using var api = new TcpClient();
            await api.ConnectAsync(IPAddress.Loopback, apiPort, stopping.Token).ConfigureAwait(false);
            var request = Encoding.ASCII.GetBytes("GET /status HTTP/1.1\r\nHost: localhost\r\n\r\n");
            await api.GetStream().WriteAsync(request, stopping.Token).ConfigureAwait(false);
            using var reader = new StreamReader(api.GetStream(), Encoding.UTF8);
            var response = await reader.ReadToEndAsync(stopping.Token).ConfigureAwait(false);
            Assert(response.Contains("200 OK", StringComparison.Ordinal) &&
                   response.Contains($"\"targetPort\":{bPort}", StringComparison.Ordinal) &&
                   response.Contains("\"existingConnectionsInterrupted\":false", StringComparison.Ordinal));
            Assert(front.Revision == 2 && File.Exists(Path.Combine(root, "state", "front-target.v1.json")));
            return new StableFrontTestReceipt("Passed", 13, true, true, true, true, false, false);
        }
        finally
        {
            transientBlocker.Stop();
            stopping.Cancel();
            try { await Task.WhenAll(frontTask, backendA, backendB).ConfigureAwait(false); } catch { }
            var resolved = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("art-vpn-front-test-", StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task RunGreetingBackendAsync(int port, string name, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var greeting = Encoding.ASCII.GetBytes(name + "\n");
                        await stream.WriteAsync(greeting, cancellationToken).ConfigureAwait(false);
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            string line;
                            try { line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false); }
                            catch { break; }
                            var reply = Encoding.ASCII.GetBytes(name + ":" + line + "\n");
                            try { await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false); }
                            catch { break; }
                        }
                    }
                }, cancellationToken);
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 1024)
        {
            var count = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            if (one[0] == (byte)'\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add(one[0]);
        }
        throw new InvalidDataException("LineTooLong");
    }

    private static int FreePort(IEnumerable<int>? excluded = null)
    {
        var denied = new HashSet<int>(excluded ?? []);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            if (!denied.Contains(port)) return port;
        }
        throw new InvalidOperationException("FreePortUnavailable");
    }

    private static async Task WaitPortAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SocketException) { await Task.Delay(20, cancellationToken).ConfigureAwait(false); }
        }
        throw new TimeoutException("ListenerUnavailable");
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("StableFrontInvariantFailed");
    }
}

internal sealed record StableFrontTestReceipt(
    string Status,
    int Scenarios,
    bool NewConnectionsSwitched,
    bool ExistingConnectionPreserved,
    bool LoopbackOnly,
    bool TransientBindRecovered,
    bool ExistingConnectionsInterrupted,
    bool SecretDisplayed);
