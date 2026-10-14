using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>IWatchdogJob: ingest Security events, correlate pending events, finalize attribution (section 11).
/// Scaffold stub – replaced by W-rules.</summary>
/// <remarks>Inside <c>AdMng.Watchdog.Core.*</c> the simple name <c>Attribution</c> binds to this namespace, not to
/// <c>AdMng.Watchdog.Contracts.Events.Attribution</c> – qualify the record or use an alias.</remarks>
public sealed class AttributionEngine : IWatchdogJob
{
    public string Name => throw new NotImplementedException();

    public TimeSpan DefaultInterval => throw new NotImplementedException();

    public IReadOnlyList<WatchdogSignal> WakeSignals => throw new NotImplementedException();

    public Task<JobResult> RunOnceAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
}
