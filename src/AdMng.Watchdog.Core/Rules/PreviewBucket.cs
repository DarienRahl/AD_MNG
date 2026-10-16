using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>One simulated aggregation bucket of the rule preview (<see cref="PreviewTargetQueue"/>).</summary>
internal sealed class PreviewBucket
{
    /// <summary>Earliest due time of its items (moved by the throttle).</summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>Highest severity of its items (Critical bypasses the throttle).</summary>
    public Severity Severity { get; set; }
}
