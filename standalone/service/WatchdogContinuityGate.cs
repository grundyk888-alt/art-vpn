namespace ArtSport.ArtVpn.Service;

internal sealed class WatchdogContinuityGate
{
    private int _failureCount;
    private DateTimeOffset _firstFailure;
    private DateTimeOffset _lastFailure;
    private int _reservePasses;
    private int _thronePasses;
    private int _reserveBrowserPasses;
    private int _throneBrowserPasses;

    public int FailureCount => _failureCount;
    public int ReservePasses => _reservePasses;
    public int ThronePasses => _thronePasses;
    public bool PreferReserve => _reservePasses >= 4 &&
        (_reserveBrowserPasses >= 4 || _thronePasses < 4 || _throneBrowserPasses < 4);

    public bool ObserveActive(bool healthy, DateTimeOffset atUtc)
    {
        if (healthy)
        {
            _failureCount = 0;
            return false;
        }
        // Only the first/last timestamps and count are needed. A provider
        // outage lasting weeks must not grow an in-memory history forever.
        if (_failureCount > 0 && (atUtc < _lastFailure || atUtc - _lastFailure > TimeSpan.FromSeconds(75)))
            _failureCount = 0;
        if (_failureCount == 0) _firstFailure = atUtc;
        _lastFailure = atUtc;
        if (_failureCount < int.MaxValue) _failureCount++;
        return _failureCount >= 3 && _lastFailure - _firstFailure >= TimeSpan.FromSeconds(30);
    }

    public int ObserveReserve(bool healthy, bool browserAvailable = false)
    {
        _reserveBrowserPasses = healthy && browserAvailable ? Math.Min(8, _reserveBrowserPasses + 1) : 0;
        return _reservePasses = healthy ? Math.Min(8, _reservePasses + 1) : 0;
    }

    public int ObserveThrone(bool healthy, bool browserAvailable = false)
    {
        _throneBrowserPasses = healthy && browserAvailable ? Math.Min(8, _throneBrowserPasses + 1) : 0;
        return _thronePasses = healthy ? Math.Min(8, _thronePasses + 1) : 0;
    }

    public void Reset()
    {
        _failureCount = 0;
        _reservePasses = 0;
        _thronePasses = 0;
        _reserveBrowserPasses = 0;
        _throneBrowserPasses = 0;
    }
}

internal static class WatchdogContinuityGateTests
{
    public static WatchdogContinuityGateTestReceipt Run()
    {
        var origin = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);
        var gate = new WatchdogContinuityGate();
        Assert(!gate.ObserveActive(false, origin));
        Assert(!gate.ObserveActive(true, origin.AddSeconds(30)) && gate.FailureCount == 0);
        Assert(!gate.ObserveActive(false, origin.AddSeconds(60)));
        Assert(!gate.ObserveActive(false, origin.AddSeconds(90)));
        Assert(gate.ObserveActive(false, origin.AddSeconds(120)) && gate.FailureCount == 3);
        Assert(gate.ObserveReserve(true) == 1);
        Assert(gate.ObserveReserve(false) == 0);
        Assert(gate.ObserveReserve(true) == 1 && gate.ObserveReserve(true) == 2 &&
               gate.ObserveReserve(true) == 3 && gate.ObserveReserve(true) == 4);
        Assert(gate.ObserveThrone(true) == 1 && gate.ObserveThrone(true) == 2 &&
               gate.ObserveThrone(true) == 3 && gate.ObserveThrone(true) == 4);
        gate.Reset();
        Assert(gate.FailureCount == 0 && gate.ReservePasses == 0 && gate.ThronePasses == 0);
        Assert(!gate.ObserveActive(false, origin));
        Assert(!gate.ObserveActive(false, origin.AddMinutes(3)) && gate.FailureCount == 1);
        gate.Reset();
        for (var i=0; i<4; i++) { gate.ObserveReserve(true, false); gate.ObserveThrone(true, true); }
        Assert(!gate.PreferReserve); // only when core failure is confirmed: full-service Throne beats partial ART reserve
        gate.ObserveThrone(false, true);
        Assert(gate.PreferReserve); // never sacrifice working OpenAI for Google
        for (var i=0; i<4; i++) { gate.ObserveReserve(true, true); gate.ObserveThrone(true, true); }
        Assert(gate.PreferReserve); // equivalent full-service candidates keep the internal reserve preference
        gate.Reset();
        Assert(!gate.PreferReserve && gate.FailureCount == 0);
        for (var i = 0; i < 100_000; i++) gate.ObserveActive(false, origin.AddSeconds(i * 30L));
        Assert(gate.FailureCount == 100_000);
        Assert(!gate.ObserveActive(false, origin) && gate.FailureCount == 1); // clock rollback starts fresh
        Assert(!gate.ObserveActive(true, origin.AddSeconds(1)) && gate.FailureCount == 0);
        return new WatchdogContinuityGateTestReceipt("Passed", 19, true, true, true, true, false);
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("WatchdogContinuityGateInvariantFailed");
    }
}

internal sealed record WatchdogContinuityGateTestReceipt(
    string Status,
    int Scenarios,
    bool HealthySampleResetsFailures,
    bool ThreeConsecutiveFailuresRequired,
    bool FourReservePassesRequired,
    bool StaleFailureSequenceRejected,
    bool SecretDisplayed);
