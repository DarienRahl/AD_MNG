using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// One alert per event and target (section 13.2 #1): a planned notification of an event is covered by an earlier alert of
/// that event to the same target that the recipients get (not Dead or Cancelled), that showed the event – the event
/// itself, not the maximum of an aggregated alert – with at least the planned severity, and that carries the final state:
/// the event's actor was known when it was rendered, or the attribution follow-up of the alert brings it
/// (<see cref="FollowUpPlanner.GetsFollowUp"/>). A higher severity, or an actor that would stay unknown to the
/// recipients, is new information. The planner leaves covered targets out of a re-evaluation (11.4 #9); the dispatch job
/// – the only writer of alerts, one bucket at a time – checks again right before it completes a row, so a row planned
/// while the earlier alert was being completed never becomes a second alert.
/// </summary>
public static class AlertCoverage
{
    /// <summary>True when <paramref name="eventId"/> already has an alert for <paramref name="targetKey"/> that covers a
    /// notification of <paramref name="severity"/>.</summary>
    public static async Task<bool> IsCoveredAsync(IOutboxStore outbox, long eventId, string targetKey, Severity severity,
        WatchdogConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(configuration);
        var alerts = (await outbox.GetMessagesForEventAsync(eventId, cancellationToken).ConfigureAwait(false))
            .Where(m => m.Kind == DeliveryKind.Alert && string.Equals(m.TargetKey, targetKey, StringComparison.Ordinal))
            .ToList();
        if (alerts.Count == 0)
        {
            return false;
        }

        var lost = await LostAsync(outbox, eventId, cancellationToken).ConfigureAwait(false);
        return alerts.Any(m => !lost.Contains(m.Id) && Covers(m, eventId, severity, configuration));
    }

    /// <summary>The target keys of <paramref name="eventId"/> whose alerts cover a notification of the given severity
    /// (one read for several targets).</summary>
    public static async Task<Func<string, Severity, bool>> CoverageAsync(IOutboxStore outbox, long eventId, WatchdogConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(configuration);
        var alerts = (await outbox.GetMessagesForEventAsync(eventId, cancellationToken).ConfigureAwait(false))
            .Where(m => m.Kind == DeliveryKind.Alert)
            .ToList();
        if (alerts.Count == 0)
        {
            return (_, _) => false;
        }

        var lost = await LostAsync(outbox, eventId, cancellationToken).ConfigureAwait(false);
        var kept = alerts.Where(m => !lost.Contains(m.Id)).ToList();
        return (targetKey, severity) => kept.Any(m =>
            string.Equals(m.TargetKey, targetKey, StringComparison.Ordinal) && Covers(m, eventId, severity, configuration));
    }

    /// <summary>True when <paramref name="alert"/> (not lost) covers a notification of <paramref name="severity"/> about
    /// <paramref name="eventId"/>: the event was shown with at least that severity and with its actor, or the follow-up
    /// brings the actor. Alerts without per-event states (schema 2 and older) count with their message values.</summary>
    public static bool Covers(OutboxMessage alert, long eventId, Severity severity, WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(configuration);
        if (alert.Kind != DeliveryKind.Alert || !alert.EventIds.Contains(eventId))
        {
            return false;
        }

        var state = alert.EventStates.FirstOrDefault(s => s.EventId == eventId);
        var shown = state?.Severity ?? alert.Severity;
        var pending = state?.PendingAttribution ?? alert.SentWithPendingAttribution;
        return shown >= severity && (!pending || FollowUpPlanner.GetsFollowUp(alert, configuration));
    }

    private static async Task<HashSet<long>> LostAsync(IOutboxStore outbox, long eventId, CancellationToken cancellationToken) =>
        (await outbox.GetDeliveriesForEventAsync(eventId, cancellationToken).ConfigureAwait(false))
            .Where(d => d.Status is OutboxStatus.Dead or OutboxStatus.Cancelled)
            .Select(d => d.OutboxId)
            .ToHashSet();
}
