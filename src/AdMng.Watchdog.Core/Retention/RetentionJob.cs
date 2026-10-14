using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Retention;

/// <summary>IWatchdogJob: daily purge and backup through IStoreMaintenance (section 16.3). Scaffold stub – replaced by
/// W-notify.</summary>
public sealed class RetentionJob : IWatchdogJob
{
    public string Name => throw new NotImplementedException();

    public TimeSpan DefaultInterval => throw new NotImplementedException();

    public IReadOnlyList<WatchdogSignal> WakeSignals => throw new NotImplementedException();

    public Task<JobResult> RunOnceAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
}
