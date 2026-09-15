namespace ArtSport.ArtVpn.Service;

internal static class FirstConnectionQualification
{
    internal static async Task<(ProviderNodeView[] Nodes, NodeProbeEvidence[] Deep, NodeProbeEvidence[] Prefilter)> FirstPassAsync(
        IReadOnlyList<ProviderNodeView> manifest, string incumbent,
        Func<IReadOnlyList<ProviderNodeView>, CancellationToken, Task<NodeProbeEvidence[]>> prefilter,
        Func<IReadOnlyList<ProviderNodeView>, CancellationToken, Task<NodeProbeEvidence[]>> deep,
        CancellationToken token)
    {
        var observations = new List<NodeProbeEvidence>();
        // Do not wait for timeouts on the whole subscription before trying a
        // good first route. The full country survey still runs in background.
        foreach (var batch in manifest.OrderByDescending(n => n.Tag == incumbent).Chunk(8))
        {
            token.ThrowIfCancellationRequested();
            var filtered = await prefilter(batch, token).ConfigureAwait(false);
            observations.AddRange(filtered);
            try
            {
                var shortlist = QualificationPolicy.Shortlist(batch, filtered, new[] { incumbent }, maximum: 8);
                var qualified = await RunAsync(shortlist, incumbent, true, deep, token).ConfigureAwait(false);
                return (qualified.Nodes, qualified.Evidence, observations.ToArray());
            }
            catch (InvalidDataException ex) when (ex.Message is "PrefilterEmpty" or "QualifiedPoolEmpty") { }
        }
        throw new InvalidDataException("QualifiedPoolEmpty");
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
