using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>IWatchdogJob: evaluates new events against the rules and plans notifications (section 13.1). Scaffold stub
/// – replaced by W-rules.</summary>
public sealed class RuleEvaluationJob : IWatchdogJob
{
    public string Name => throw new NotImplementedException();

    public TimeSpan DefaultInterval => throw new NotImplementedException();

    public IReadOnlyList<WatchdogSignal> WakeSignals => throw new NotImplementedException();

    public Task<JobResult> RunOnceAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
}
