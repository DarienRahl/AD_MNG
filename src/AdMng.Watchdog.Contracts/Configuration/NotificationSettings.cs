namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>Immediate notifications: holds, aggregation, throttling (section 13).</summary>
public sealed record NotificationSettings
{
    public string SubjectPrefix { get; init; } = "[AD Watchdog]";
    public TimeSpan AttributionGrace { get; init; } = TimeSpan.FromSeconds(90);    // hold < High waiting for the actor
    public TimeSpan AggregationWindow { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan HighSeverityFlushDelay { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxItemsPerMessage { get; init; } = 50;
    public int MaxMessagesPerRulePerHour { get; init; } = 10;                      // Critical exempt
    public bool FollowUpWhenAttributed { get; init; } = true;                      // High/Critical sent with actor pending
    public int MaxValueLength { get; init; } = 200;
}
