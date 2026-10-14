using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// PrivilegeGranted / PrivilegeRevoked inherit the attribution of the change that caused them (section 11.2): the
/// GroupMemberAdded/Removed, PrimaryGroupChanged or ObjectRestored (tag <c>restored</c>) event of the same change set,
/// else the membership event on the path's first edge (the principal joined/left the first group of the nesting path,
/// or the privileged group itself when the membership is direct).
/// </summary>
public sealed class PrivilegeAttributionInheritor(IEventStore events)
{
    /// <summary>How far before the privilege event the first-edge membership event is searched.</summary>
    public static readonly TimeSpan FirstEdgeLookback = TimeSpan.FromDays(1);

    /// <summary>The causing event, or null when none is stored.</summary>
    public async Task<WatchEvent?> FindCauseAsync(WatchEvent privilegeEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(privilegeEvent);
        var granted = privilegeEvent.Kind == WatchEventKind.PrivilegeGranted;
        var memberKind = granted ? WatchEventKind.GroupMemberAdded : WatchEventKind.GroupMemberRemoved;
        if (privilegeEvent.ChangeSetId != Guid.Empty)
        {
            var changeSet = await events.GetChangeSetAsync(privilegeEvent.ChangeSetId, cancellationToken).ConfigureAwait(false);
            var restored = (privilegeEvent.Tags ?? []).Contains(EventTags.Restored, StringComparer.OrdinalIgnoreCase);
            var cause = changeSet
                .Where(e => e.EventId != privilegeEvent.EventId)
                .OrderBy(e => restored && e.Kind == WatchEventKind.ObjectRestored ? 0 : 1)
                .FirstOrDefault(e => e.Kind == memberKind || e.Kind == WatchEventKind.PrimaryGroupChanged || e.Kind == WatchEventKind.ObjectRestored);
            if (cause is not null)
            {
                return cause;
            }
        }

        var firstGroup = privilegeEvent.Via is { Count: > 0 } via ? via[0] : privilegeEvent.Member;
        if (firstGroup?.ObjectGuid is not { } groupGuid || privilegeEvent.Object.ObjectGuid is not { } principalGuid)
        {
            return null;
        }

        var page = await events.QueryAsync(new EventQuery
        {
            ObjectGuid = groupGuid,
            Kinds = [memberKind],
            From = privilegeEvent.DetectedAt - FirstEdgeLookback,
            To = privilegeEvent.DetectedAt + TimeSpan.FromMinutes(1),
            Limit = 100,
        }, cancellationToken).ConfigureAwait(false);
        return page.Items
            .Where(e => e.Member?.ObjectGuid == principalGuid)
            .OrderByDescending(e => e.DetectedAt)
            .FirstOrDefault();
    }

    /// <summary>The privilege event's attribution copied from its final cause.</summary>
    public static Contracts.Events.Attribution Inherit(Contracts.Events.Attribution cause, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return cause with { ResolvedAt = cause.ResolvedAt ?? now };
    }
}
