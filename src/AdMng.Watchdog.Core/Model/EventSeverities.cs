using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Model;

/// <summary>
/// The two severities of a stored event (sections 12.1, 12.2): the event's own severity (detection, tag bumps,
/// attribution) is the input of every rule match – the rules job, the re-evaluation after attribution, the dispatch
/// job's re-match and the rule preview all use it –, the evaluated severity (<see cref="WatchEvent.Severity"/>, own
/// severity raised by the overrides of the matched rules) is what the feed, the digest and the counts show.
/// </summary>
public static class EventSeverities
{
    /// <summary>The event's own severity: <see cref="WatchEvent.OwnSeverity"/>, or <see cref="WatchEvent.Severity"/> for an
    /// event that was not stored yet.</summary>
    public static Severity Own(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        return watchEvent.OwnSeverity ?? watchEvent.Severity;
    }

    /// <summary>The event with an attribution update applied as the stores apply it (<c>IEventStore.UpdateAttributionAsync</c>):
    /// attribution, added tags (distinct), lockout sources, the own severity <c>update.Severity</c> and the evaluated one
    /// <c>update.EvaluatedSeverity</c> – or the new own severity when no evaluation came with it.</summary>
    /// <param name="watchEvent">The stored event.</param>
    /// <param name="update">The update.</param>
    /// <param name="tagComparer">How tags are compared when the added ones are merged (the stores: ordinal).</param>
    public static WatchEvent WithAttribution(WatchEvent watchEvent, AttributionUpdate update, StringComparer? tagComparer = null)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(update);
        return watchEvent with
        {
            Attribution = update.Attribution,
            Tags = [.. (watchEvent.Tags ?? []).Concat(update.AddedTags ?? []).Distinct(tagComparer ?? StringComparer.Ordinal)],
            OwnSeverity = update.Severity ?? Own(watchEvent),
            Severity = update.EvaluatedSeverity ?? update.Severity ?? watchEvent.Severity,
            Lockout = update.Lockout ?? watchEvent.Lockout,
        };
    }
}
