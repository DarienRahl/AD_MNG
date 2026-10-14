using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>One processed pending event (<see cref="PendingAttributionProcessor"/>).</summary>
/// <param name="Event">The event as it was pending.</param>
/// <param name="Update">What to store (<c>NextAttemptAt</c> null = final).</param>
/// <param name="Outcome">The correlation outcome when one ran (operation grouping, 11.4 #7).</param>
/// <param name="Plan">The search plan when one was made.</param>
public sealed record ProcessedAttribution(WatchEvent Event, AttributionUpdate Update, CorrelationOutcome? Outcome = null, AttributionSearchPlan? Plan = null)
{
    /// <summary>True when the attribution is final.</summary>
    public bool IsFinal => Update.NextAttemptAt is null;

    /// <summary>The event with the update applied (follow-up planning input).</summary>
    public WatchEvent Updated => Event with
    {
        Attribution = Update.Attribution,
        Tags = [.. (Event.Tags ?? []).Concat(Update.AddedTags).Distinct(StringComparer.OrdinalIgnoreCase)],
        Severity = Update.Severity ?? Event.Severity,
        Lockout = Update.Lockout ?? Event.Lockout,
    };
}
