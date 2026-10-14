using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>IWatchdogJob: one poll cycle (section 10). Scaffold stub – replaced by W-detect.</summary>
public sealed class ChangeDetectionEngine : IWatchdogJob
{
    public string Name => throw new NotImplementedException();

    public TimeSpan DefaultInterval => throw new NotImplementedException();

    public IReadOnlyList<WatchdogSignal> WakeSignals => throw new NotImplementedException();

    public Task<JobResult> RunOnceAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
}
