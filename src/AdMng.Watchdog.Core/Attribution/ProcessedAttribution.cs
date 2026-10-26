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

    /// <summary>The event with the update applied (re-evaluation and follow-up planning input): own severity
    /// <c>Update.Severity</c>, evaluated severity <c>Update.EvaluatedSeverity</c> (<see cref="EventSeverities.WithAttribution"/>).</summary>
    public WatchEvent Updated => EventSeverities.WithAttribution(Event, Update, StringComparer.OrdinalIgnoreCase);
}
