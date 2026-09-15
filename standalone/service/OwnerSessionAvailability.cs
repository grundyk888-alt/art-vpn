using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Service;

// Wait without mounting the profile, inventing proxy values, changing a lease,
// or terminating the service. Other registry errors remain genuine failures.
internal static class OwnerSessionAvailability
{
    internal static bool IsAvailable(ISystemProxyStore store)
    {
        try { SystemProxyLeaseCoordinator.ValidateSnapshot(store.Read()); return true; }
        catch (SystemProxyOwnerUnavailableException) { return false; }
    }

    internal static async Task WaitAsync(ISystemProxyStore store, Action waiting,
        CancellationToken cancellationToken, TimeSpan? retryDelay = null)
    {
        var announced = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsAvailable(store)) return;
            if (!announced) { waiting(); announced = true; }
            await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }
}
