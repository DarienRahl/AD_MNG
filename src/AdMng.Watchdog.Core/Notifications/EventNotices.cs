using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// What the recipients of each target were told about one event (sections 13.2 #1 and #7): the event's alerts and
/// follow-ups that reach them – not Dead or Cancelled, still waiting in the outbox counts –, the severity they saw the
/// event with (per event, <see cref="OutboxMessage.EventStates"/>; an aggregated alert carries the maximum of its events),
/// the alert a follow-up replies to, and whether the attribution follow-up of an alert is still to come. One read of the
/// outbox serves every target of a re-evaluation; <see cref="AlertCoverage"/> decides repeats with it.
/// </summary>
public sealed class EventNotices
{
    private readonly long _eventId;
    private readonly IReadOnlyList<OutboxMessage> _kept;

    private EventNotices(long eventId, IReadOnlyList<OutboxMessage> kept)
    {
        _eventId = eventId;
        _kept = kept;
    }

    /// <summary>The alerts and follow-ups of <paramref name="eventId"/> that reach their recipients.</summary>
    public static async Task<EventNotices> LoadAsync(IOutboxStore outbox, long eventId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        var messages = (await outbox.GetMessagesForEventAsync(eventId, cancellationToken).ConfigureAwait(false))
            .Where(m => m.Kind is DeliveryKind.Alert or DeliveryKind.FollowUp && m.EventIds.Contains(eventId))
            .ToList();
        if (messages.Count == 0)
        {
            return new EventNotices(eventId, []);
        }

        var lost = (await outbox.GetDeliveriesForEventAsync(eventId, cancellationToken).ConfigureAwait(false))
            .Where(d => d.Status is OutboxStatus.Dead or OutboxStatus.Cancelled)
            .Select(d => d.OutboxId)
            .ToHashSet();
        return new EventNotices(eventId, [.. messages.Where(m => !lost.Contains(m.Id))]);
    }

    /// <summary>The severity <paramref name="message"/> showed event <paramref name="eventId"/> with: its per-event state,
    /// else (messages stored before the per-event states) the message severity.</summary>
    public static Severity ShownSeverity(OutboxMessage message, long eventId)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.EventStates.FirstOrDefault(s => s.EventId == eventId)?.Severity ?? message.Severity;
    }

    /// <summary>The highest severity the target's alerts and follow-ups showed the event with; null when none reached it.</summary>
    public Severity? Shown(string targetKey)
    {
        var shown = ForTarget(targetKey).Select(m => (Severity?)ShownSeverity(m, _eventId)).ToList();
        return shown.Count == 0 ? null : shown.Max();
    }

    /// <summary>The latest alert of the event to the target – the message a follow-up replies to –, or null.</summary>
    public OutboxMessage? LatestAlert(string targetKey) =>
        ForTarget(targetKey).Where(m => m.Kind == DeliveryKind.Alert).MaxBy(m => m.Id);

    /// <summary>True when the target already got a notification of <paramref name="severity"/> about the event: an alert
    /// that covers it (<see cref="AlertCoverage.Covers"/>), or a follow-up that showed the event with at least that
    /// severity (a follow-up carries the final attribution).</summary>
    public bool Covers(string targetKey, Severity severity, WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ForTarget(targetKey).Any(m => m.Kind == DeliveryKind.Alert
            ? AlertCoverage.Covers(m, _eventId, severity, configuration)
            : ShownSeverity(m, _eventId) >= severity);
    }

    /// <summary>True when the attribution follow-up of an alert to the target is still to come: the event's actor was
    /// pending in an alert that gets a follow-up (<see cref="FollowUpPlanner.GetsFollowUp"/>) and no follow-up of the
    /// event reached the target yet.</summary>
    public bool FollowUpComing(string targetKey, WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var messages = ForTarget(targetKey).ToList();
        return messages.All(m => m.Kind != DeliveryKind.FollowUp)
            && messages.Any(m => m.Kind == DeliveryKind.Alert && PendingIn(m) && FollowUpPlanner.GetsFollowUp(m, configuration));
    }

    private bool PendingIn(OutboxMessage alert) =>
        alert.EventStates.FirstOrDefault(s => s.EventId == _eventId)?.PendingAttribution ?? alert.SentWithPendingAttribution;

    private IEnumerable<OutboxMessage> ForTarget(string targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        return _kept.Where(m => string.Equals(m.TargetKey, targetKey, StringComparison.Ordinal));
    }
}
