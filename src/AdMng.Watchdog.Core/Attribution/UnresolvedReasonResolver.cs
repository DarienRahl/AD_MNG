using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// The reason code of an attribution that ends unresolved (section 11.7), checked in this order: DC_UNREACHABLE (a
/// searched DC's log could not be read since the window began), LOG_OVERWRITTEN (records of a searched DC were
/// overwritten unread since the window began), AUDIT_POLICY_MISSING (no event of the kind's account-management family
/// from any searched DC in 7 days), SACL_MISSING (the kind is only coverable by 5136/5137/5139/5141 and none was seen
/// from any searched DC in 7 days), otherwise NO_MATCH. (SOURCE_DISABLED and AMBIGUOUS are decided by the engine.)
/// </summary>
public sealed class UnresolvedReasonResolver(ISecurityEventStore store, DcLogHealthTracker tracker, TimeProvider timeProvider)
{
    /// <summary>How far back "seen from that DC" looks.</summary>
    public static readonly TimeSpan AuditHorizon = TimeSpan.FromDays(7);

    private static readonly int[] SaclCodes =
        [SecurityEventCatalog.ObjectModified, SecurityEventCatalog.ObjectCreated, SecurityEventCatalog.ObjectMoved, SecurityEventCatalog.ObjectDeleted];

    /// <summary>The reason for <paramref name="watchEvent"/> searched with <paramref name="plan"/>.</summary>
    public async Task<string> ResolveAsync(WatchEvent watchEvent, AttributionSearchPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.DomainControllers.Any(dc => tracker.WasUnreachableSince(dc, plan.From)))
        {
            return AttributionReasons.DcUnreachable;
        }

        if (plan.DomainControllers.Any(dc => tracker.WasOverwrittenSince(dc, plan.From)))
        {
            return AttributionReasons.LogOverwritten;
        }

        var accountCodes = SecurityEventCatalog.AccountCodes(watchEvent.Kind, watchEvent.Object.Kind);
        if (accountCodes.Count > 0)
        {
            return await NoneSeenAsync(plan, accountCodes, cancellationToken).ConfigureAwait(false)
                ? AttributionReasons.AuditPolicyMissing
                : AttributionReasons.NoMatch;
        }

        return SecurityEventCatalog.DirectoryCodes(watchEvent.Kind).Count > 0 && await NoneSeenAsync(plan, SaclCodes, cancellationToken).ConfigureAwait(false)
            ? AttributionReasons.SaclMissing
            : AttributionReasons.NoMatch;
    }

    private async Task<bool> NoneSeenAsync(AttributionSearchPlan plan, IReadOnlyList<int> codes, CancellationToken cancellationToken)
    {
        if (plan.DomainControllers.Count == 0)
        {
            return false;
        }

        var since = timeProvider.GetUtcNow() - AuditHorizon;
        foreach (var dc in plan.DomainControllers)
        {
            var computer = tracker.ComputerName(dc);
            foreach (var code in codes)
            {
                if (await store.GetLastSeenAsync(computer, code, cancellationToken).ConfigureAwait(false) is { } seen && seen >= since)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
