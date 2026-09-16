namespace ArtSport.ArtVpn.Service;

internal static class FirstConnectionQualification
{
    internal static async Task<(ProviderNodeView[] Nodes, NodeProbeEvidence[] Deep, NodeProbeEvidence[] Prefilter)> FirstPassAsync(
        IReadOnlyList<ProviderNodeView> manifest, string incumbent,
        Func<IReadOnlyList<ProviderNodeView>, CancellationToken, Task<NodeProbeEvidence[]>> prefilter,
        Func<IReadOnlyList<ProviderNodeView>, CancellationToken, Task<NodeProbeEvidence[]>> deep,
        CancellationToken token, bool bootstrapOnly = false)
    {
        token.ThrowIfCancellationRequested();
        var observations = new List<NodeProbeEvidence>();
        var queue = new Queue<ProviderNodeView>(manifest.OrderByDescending(n => n.Tag == incumbent));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var deepGate = new SemaphoreSlim(4, 4);
        var pending = new List<Task<FirstCandidate>>();
        try
        {
            // A slow neighbour must not delay an already qualified route.
            // Up to eight nodes are in flight, only four in deep qualification.
            // Installer bootstrap reuses its successful one-round prefilter.
            // Ordinary qualification still uses the six-round deep policy.
            while (queue.Count > 0 || pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                while (queue.Count > 0 && pending.Count < 8)
                {
                    var task = ProbeCandidateAsync(queue.Dequeue(), incumbent, prefilter, deep, deepGate, stop.Token, bootstrapOnly);
                    pending.Add(task);
                    // Do not unnecessarily start the whole list when a cached
                    // or test transport has already produced a final result.
                    if (task.IsCompleted) break;
                }
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                observations.AddRange(result.Prefilter);
                if (result.Accepted)
                    return ([result.Node], result.Deep, observations.ToArray());
            }
            throw new InvalidDataException("QualifiedPoolEmpty");
        }
        finally
        {
            // No orphan probes or background core processes after a winner,
            // cancellation, or failed import. Full refinement is scheduled only
            // after the controller has actually committed the first connection.
            stop.Cancel();
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private sealed record FirstCandidate(ProviderNodeView Node, NodeProbeEvidence[] Prefilter,
        NodeProbeEvidence[] Deep, bool Accepted);

    private static async Task<FirstCandidate> ProbeCandidateAsync(ProviderNodeView node, string incumbent,
        Func<IReadOnlyList<ProviderNodeView>, CancellationToken, Task<NodeProbeEvidence[]>> prefilter,
        Func<IReadOnlyList<ProviderNodeView>, CancellationToken, Task<NodeProbeEvidence[]>> deep,
        SemaphoreSlim deepGate, CancellationToken token, bool bootstrapOnly)
    {
        var nodes = new[] { node };
        var filtered = await prefilter(nodes, token).ConfigureAwait(false);
        try { _ = QualificationPolicy.Shortlist(nodes, filtered, new[] { incumbent }, maximum: 1); }
        catch (InvalidDataException ex) when (ex.Message == "PrefilterEmpty")
        { return new(node, filtered, [], false); }
        if (bootstrapOnly)
        {
            // Do not fabricate six-round evidence. One complete successful
            // HTTPS/integrity round qualifies only an initial route; activation
            // independently verifies the actual runtime twice before commit.
            try
            {
                _ = QualificationPolicy.Select(nodes, filtered, incumbent, requiredRounds: 1, maximum: 1);
                return new(node, filtered, filtered, true);
            }
            catch (InvalidDataException ex) when (ex.Message == "QualifiedPoolEmpty")
            { return new(node, filtered, [], false); }
        }
        await deepGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var evidence = await deep(nodes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            try
            {
                _ = QualificationPolicy.Select(nodes, evidence, incumbent, maximum: 1);
                return new(node, filtered, evidence, true);
            }
            catch (InvalidDataException ex) when (ex.Message == "QualifiedPoolEmpty")
            { return new(node, filtered, evidence, false); }
        }
        finally { deepGate.Release(); }
    }

    // Keep admission criteria identical to background qualification. Only the
    // amount of work before the first connection changes, not its trust level.
    internal static async Task<(ProviderNodeView[] Nodes, NodeProbeEvidence[] Evidence)> RunAsync(
        IReadOnlyList<ProviderNodeView> shortlist, string incumbent, bool quickConnect,
        Func<IReadOnlyList<ProviderNodeView>, CancellationToken, Task<NodeProbeEvidence[]>> probe,
        CancellationToken token)
    {
        if (!quickConnect) return (shortlist.ToArray(), await probe(shortlist, token).ConfigureAwait(false));
        var tested = new List<ProviderNodeView>();
        var evidence = new List<NodeProbeEvidence>();
        foreach (var batch in shortlist.Chunk(4))
        {
            token.ThrowIfCancellationRequested();
            tested.AddRange(batch);
            evidence.AddRange(await probe(batch, token).ConfigureAwait(false));
            try
            {
                _ = QualificationPolicy.Select(tested, evidence, incumbent, maximum: 12);
                return (tested.ToArray(), evidence.ToArray());
            }
            catch (InvalidDataException ex) when (ex.Message == "QualifiedPoolEmpty") { }
        }
        // Preserve the exact no-channel failure; never advertise an unchecked
        // candidate or fall back to another provider as a successful import.
        _ = QualificationPolicy.Select(tested, evidence, incumbent, maximum: 12);
        return (tested.ToArray(), evidence.ToArray());
    }
}
