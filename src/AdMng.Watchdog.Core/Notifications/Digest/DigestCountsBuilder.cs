using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>The counters of a digest over every event of the period.</summary>
public static class DigestCountsBuilder
{
    /// <summary>Counts <paramref name="events"/>.</summary>
    public static DigestCounts Count(IReadOnlyList<WatchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var passwordChanges = events.Where(e => e.Kind == WatchEventKind.PasswordChanged).ToList();
        var resets = passwordChanges.Count(e => HasTag(e, EventTags.PasswordReset));
        var own = passwordChanges.Count(e => !HasTag(e, EventTags.PasswordReset) && HasTag(e, EventTags.SelfChange));
        return new DigestCounts
        {
            Total = events.Count,
            Critical = events.Count(e => e.Severity == Severity.Critical),
            High = events.Count(e => e.Severity == Severity.High),
            NewUsers = Of(events, WatchEventKind.UserCreated),
            NewComputers = Of(events, WatchEventKind.ComputerCreated),
            Deleted = Of(events, WatchEventKind.ObjectDeleted),
            Disabled = Of(events, WatchEventKind.AccountDisabled),
            Enabled = Of(events, WatchEventKind.AccountEnabled),
            Lockouts = Of(events, WatchEventKind.AccountLockedOut),
            PasswordResets = resets,
            OwnPasswordChanges = own,
            OtherPasswordChanges = passwordChanges.Count - resets - own,
            PasswordAgeResets = Of(events, WatchEventKind.PasswordAgeReset),
            PrivilegedChanges = Of(events, WatchEventKind.PrivilegeGranted) + Of(events, WatchEventKind.PrivilegeRevoked),
            DomainChanges = events.Count(e => DigestSections.For(e.Kind) == DigestSectionKind.DomainPolicy),
            Attention = events.Count(e => e.Severity >= Severity.High),
            Unacknowledged = events.Count(e => e.Severity >= Severity.High && e.AcknowledgedAt is null),
        };
    }

    private static int Of(IReadOnlyList<WatchEvent> events, WatchEventKind kind) => events.Count(e => e.Kind == kind);

    private static bool HasTag(WatchEvent e, string tag) => e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
}
