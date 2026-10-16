namespace AdMng.Watchdog.Core.Abstractions;

public enum WatchdogSignal { PollRequested, EventsStored, AttributionUpdated, NotificationsQueued, OutboxEnqueued, ConfigChanged, DigestRequested }

/// <summary>In-process wake-ups between the jobs.</summary>
public interface IWatchdogSignals
{
    /// <summary>Sets the latch of every subscription that includes the signal.</summary>
    void Raise(WatchdogSignal signal);
    /// <summary>One subscription per consumer (JobHost subscribes once per job with its WakeSignals). Each subscription
    /// has its own latch, so overlapping consumers never steal each other's wake-ups.</summary>
    ISignalSubscription Subscribe(IReadOnlyList<WatchdogSignal> signals);
}

public interface ISignalSubscription : IDisposable
{
    /// <summary>Completes true when one of the subscribed signals was raised since the previous WaitAsync of this
    /// subscription returned (the latch is reset on return), false on timeout. The timeout runs on the injected
    /// TimeProvider (<c>Task.Delay(timeout, timeProvider, ct)</c>).</summary>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
