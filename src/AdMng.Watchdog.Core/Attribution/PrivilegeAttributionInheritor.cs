using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// PrivilegeGranted / PrivilegeRevoked inherit the attribution of the change that caused them (section 11.2): the
/// GroupMemberAdded/Removed, PrimaryGroupChanged or ObjectRestored (tag <c>restored</c>) event of the same change set;
/// otherwise the membership change on <b>any edge</b> of the nesting path (privileged root ← … ← principal, in whatever
/// order <see cref="WatchEvent.Via"/> lists the groups) or a PrimaryGroupChanged of the principal, detected within
/// <see cref="CauseWindow"/> before the privilege event – the newest one. Nesting IT-Ops into Domain Admins is the cause of
/// Bob's privilege, not the day-old addition of Bob to IT-Ops; a change long before the privilege event is never
/// taken (better unresolved than the wrong actor).
/// </summary>
public sealed class PrivilegeAttributionInheritor(IEventStore events)
{
    /// <summary>How long before the privilege event its cause may have been detected (the privilege evaluation runs in
    /// the detection cycle of the membership change, or a few cycles later for nested groups read on demand).</summary>
    public static readonly TimeSpan CauseWindow = TimeSpan.FromMinutes(15);

    /// <summary>Clock tolerance after the privilege event.</summary>
    private static readonly TimeSpan After = TimeSpan.FromMinutes(1);

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

        if (privilegeEvent.Object.ObjectGuid is not { } principalGuid)
        {
            return null;
        }

        // The groups of the path and the members whose addition/removal can complete or break it.
        var via = (privilegeEvent.Via ?? []).Select(g => g.ObjectGuid).OfType<Guid>().ToList();
        var groups = new HashSet<Guid>(via);
        if (privilegeEvent.Member?.ObjectGuid is { } root)
        {
            groups.Add(root);
        }

        var members = new HashSet<Guid>(via) { principalGuid };
        var from = privilegeEvent.DetectedAt - CauseWindow;
        var to = privilegeEvent.DetectedAt + After;
        var candidates = new List<WatchEvent>();
        foreach (var group in groups)
        {
            var page = await QueryAsync(group, memberKind, from, to, cancellationToken).ConfigureAwait(false);
            candidates.AddRange(page.Where(e => e.Member?.ObjectGuid is { } member && member != group && members.Contains(member)));
        }

        candidates.AddRange(await QueryAsync(principalGuid, WatchEventKind.PrimaryGroupChanged, from, to, cancellationToken).ConfigureAwait(false));
        return candidates
            .Where(e => e.EventId != privilegeEvent.EventId)
            .OrderByDescending(e => e.DetectedAt)
            .ThenByDescending(e => e.Id)
            .FirstOrDefault();
    }

    /// <summary>The privilege event's attribution copied from its final cause.</summary>
    public static Contracts.Events.Attribution Inherit(Contracts.Events.Attribution cause, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return cause with { ResolvedAt = cause.ResolvedAt ?? now };
    }

    private async Task<IReadOnlyList<WatchEvent>> QueryAsync(Guid objectGuid, WatchEventKind kind, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken) =>
        (await events.QueryAsync(new EventQuery { ObjectGuid = objectGuid, Kinds = [kind], From = from, To = to, Limit = 100 }, cancellationToken)
            .ConfigureAwait(false)).Items;
}
