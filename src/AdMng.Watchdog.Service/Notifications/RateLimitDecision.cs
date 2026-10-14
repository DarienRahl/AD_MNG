namespace AdMng.Watchdog.Service.Notifications;

/// <summary>Outcome of <see cref="ChannelRateLimiters"/>: acquired, or denied with the limiter's retry hint.</summary>
public readonly record struct RateLimitDecision(bool Acquired, TimeSpan RetryAfter)
{
    public static RateLimitDecision Granted { get; } = new(true, TimeSpan.Zero);
}
