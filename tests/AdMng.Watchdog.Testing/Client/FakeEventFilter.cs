using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Testing.Client;

/// <summary>The filter semantics of <c>GET /api/v1/events</c> (<see cref="EventQuery"/>) over in-memory events.</summary>
public static class FakeEventFilter
{
    /// <summary>True when <paramref name="watchEvent"/> passes every condition of <paramref name="query"/> (paging ignored).</summary>
    public static bool Matches(WatchEvent watchEvent, EventQuery query)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(query);
        var attribution = watchEvent.Attribution ?? EventAttribution.Pending;
        return (query.Kinds.Count == 0 || query.Kinds.Contains(watchEvent.Kind))
            && (query.Categories.Count == 0 || query.Categories.Contains(EventKindCatalog.Get(watchEvent.Kind).Category))
            && (query.ObjectKinds.Count == 0 || query.ObjectKinds.Contains(watchEvent.Object.Kind))
            && (query.MinSeverity is not { } min || watchEvent.Severity >= min)
            && MatchesText(watchEvent, query.Text)
            && MatchesSubtree(watchEvent, query.Subtree)
            && query.Tags.All(t => watchEvent.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            && (query.AttributionStatuses.Count == 0 || query.AttributionStatuses.Contains(attribution.Status))
            && (query.From is not { } from || watchEvent.DetectedAt >= from)
            && (query.To is not { } to || watchEvent.DetectedAt <= to)
            && (query.RuleId is not { } rule || watchEvent.MatchedRules.Contains(rule, StringComparer.Ordinal))
            && (query.ObjectGuid is not { } guid || watchEvent.Object.ObjectGuid == guid || watchEvent.Member?.ObjectGuid == guid)
            && (query.ActorSid is not { } sid || string.Equals(attribution.Actor?.Sid, sid, StringComparison.OrdinalIgnoreCase))
            && (!query.UnacknowledgedOnly || watchEvent.AcknowledgedAt is null);
    }

    private static bool MatchesText(WatchEvent watchEvent, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var attribution = watchEvent.Attribution ?? EventAttribution.Pending;
        string?[] candidates =
        [
            watchEvent.Object.Name, watchEvent.Object.DisplayName, watchEvent.Object.SamAccountName, watchEvent.Object.Dn,
            watchEvent.PreviousDn, attribution.Actor?.Account, attribution.Actor?.DisplayName, attribution.Workstation, attribution.IpAddress,
        ];
        return candidates.Any(c => c?.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool MatchesSubtree(WatchEvent watchEvent, string? subtree)
    {
        if (string.IsNullOrWhiteSpace(subtree))
        {
            return true;
        }

        if (!DistinguishedName.TryParse(subtree, out var root))
        {
            return false;
        }

        return IsAtOrBelow(watchEvent.Object.Dn, root) || IsAtOrBelow(watchEvent.PreviousDn, root);
    }

    private static bool IsAtOrBelow(string? dn, DistinguishedName root) =>
        DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot && (parsed.Equals(root) || parsed.IsDescendantOf(root));
}
