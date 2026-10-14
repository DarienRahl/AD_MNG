using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>IWatchdogJob running the scanners (expiry, lockout, bursts, self-check) on their intervals. Scaffold stub –
/// replaced by W-detect.</summary>
public sealed class ScheduledScanJob : IWatchdogJob
{
    public string Name => throw new NotImplementedException();

    public TimeSpan DefaultInterval => throw new NotImplementedException();

    public IReadOnlyList<WatchdogSignal> WakeSignals => throw new NotImplementedException();

    public Task<JobResult> RunOnceAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
}
