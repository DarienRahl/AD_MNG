using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// What a follow-up tells about one event compared with the alert it replies to (sections 13.2 #6–#7, 14): the severity
/// that alert showed the event with and whether the event's actor was still pending there. The follow-up renders the
/// event with its final severity and says when it changed ("Średni → Wysoki").
/// </summary>
/// <param name="EventId">The event (store id).</param>
/// <param name="PreviousSeverity">The severity the alert showed the event with; null when unknown (the alert was purged,
/// or an aggregated alert stored before the per-event states).</param>
/// <param name="ActorWasPending">The alert showed the event with its actor still pending.</param>
public sealed record FollowUpChange(long EventId, Severity? PreviousSeverity, bool ActorWasPending)
{
    /// <summary>The change of <paramref name="watchEvent"/> in <paramref name="changes"/>, or null.</summary>
    public static FollowUpChange? Of(WatchEvent watchEvent, IReadOnlyList<FollowUpChange>? changes)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        return changes?.FirstOrDefault(c => c.EventId == watchEvent.Id);
    }

    /// <summary>The severity <paramref name="watchEvent"/> was shown with before, when it differs from its final severity
    /// (<see cref="WatchEvent.Severity"/> as rendered), else null.</summary>
    public static Severity? ChangedFrom(WatchEvent watchEvent, IReadOnlyList<FollowUpChange>? changes) =>
        Of(watchEvent, changes)?.PreviousSeverity is { } previous && previous != watchEvent.Severity ? previous : null;

    /// <summary>"Uzupełnienie: wykonawca ustalony" – or "…: priorytet podniesiony" when the severity of an event was raised
    /// and no actor was pending, "…: wykonawca ustalony, priorytet podniesiony" when both. Without changes (the original
    /// is unknown) the actor follow-up.</summary>
    /// <param name="events">The events of the follow-up (severities as rendered).</param>
    /// <param name="changes">Their changes.</param>
    /// <param name="context">Language.</param>
    /// <param name="key">The resource of the actor follow-up (<c>Email.FollowUpBanner</c>, <c>Card.FollowUpTitle</c>);
    /// the other two are it + <c>Severity</c> / <c>ActorAndSeverity</c>.</param>
    internal static string Title(IReadOnlyList<WatchEvent> events, IReadOnlyList<FollowUpChange>? changes, NotificationContext context, string key)
    {
        var raised = events.Any(e => ChangedFrom(e, changes) is { } previous && previous < e.Severity);
        var actor = events.Any(e => Of(e, changes)?.ActorWasPending != false);
        return context.Text.Get(key + (!raised ? string.Empty : actor ? "ActorAndSeverity" : "Severity"));
    }

    /// <summary>"Średni → Wysoki" when the severity of <paramref name="watchEvent"/> changed, else null.</summary>
    internal static string? SeverityChange(WatchEvent watchEvent, IReadOnlyList<FollowUpChange>? changes, NotificationContext context) =>
        ChangedFrom(watchEvent, changes) is { } previous
            ? context.Text.Format("Value.SeverityChange", context.Text.SeverityName(previous), context.Text.SeverityName(watchEvent.Severity))
            : null;

    /// <summary>The final severity of <paramref name="watchEvent"/>, with its change: "Wysoki" or "Średni → Wysoki".</summary>
    internal static string SeverityText(WatchEvent watchEvent, IReadOnlyList<FollowUpChange>? changes, NotificationContext context) =>
        SeverityChange(watchEvent, changes, context) ?? context.Text.SeverityName(watchEvent.Severity);
}
