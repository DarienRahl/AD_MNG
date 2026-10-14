using System.Collections.Frozen;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Testing.Stores;

/// <summary>The <see cref="EventQuery"/> filters of <c>GET /api/v1/events</c> as the SQLite store applies them (paging
/// is the caller's): kinds, categories, object kinds, minimum severity, text (case-insensitive contains on the search
/// text "name|sAMAccountName|DN|actor account|workstation|IP"), subtree (DN or previous DN at or below), all tags,
/// attribution statuses, <c>[From, To)</c>, rule id, object GUID, actor SID and unacknowledged.</summary>
internal sealed class EventQueryFilter
{
    private static readonly FrozenDictionary<WatchEventKind, EventCategory> Categories =
        EventKindCatalog.All.ToFrozenDictionary(k => k.Kind, k => k.Category);

    private readonly EventQuery _query;
    private readonly DistinguishedName? _subtree;
    private readonly bool _subtreeInvalid;
    private readonly string? _text;

    public EventQueryFilter(EventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _query = query;
        _text = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(query.Subtree))
        {
            _subtreeInvalid = !DistinguishedName.TryParse(query.Subtree, out var subtree) || subtree.IsRoot;
            _subtree = _subtreeInvalid ? null : subtree;
        }
    }

    /// <summary>The search text column of an event (lower-case, '|'-separated).</summary>
    public static string SearchText(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        var parts = new[]
        {
            watchEvent.Object.Name, watchEvent.Object.SamAccountName, watchEvent.Object.Dn,
            watchEvent.Attribution.Actor?.Account, watchEvent.Attribution.Workstation, watchEvent.Attribution.IpAddress,
        };
        return string.Join('|', parts.Select(p => p ?? string.Empty)).ToLowerInvariant();
    }

    public bool Matches(WatchEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return MatchesKind(e)
            && (_query.MinSeverity is not { } min || e.Severity >= min)
            && (_text is null || SearchText(e).Contains(_text, StringComparison.Ordinal))
            && MatchesSubtree(e)
            && _query.Tags.All(t => e.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            && (_query.AttributionStatuses.Count == 0 || _query.AttributionStatuses.Contains(e.Attribution.Status))
            && (_query.From is not { } from || e.DetectedAt >= from)
            && (_query.To is not { } to || e.DetectedAt < to)
            && (_query.RuleId is not { } ruleId || e.MatchedRules.Contains(ruleId, StringComparer.OrdinalIgnoreCase))
            && (_query.ObjectGuid is not { } guid || e.Object.ObjectGuid == guid)
            && (_query.ActorSid is not { } sid || string.Equals(e.Attribution.Actor?.Sid, sid, StringComparison.OrdinalIgnoreCase))
            && (!_query.UnacknowledgedOnly || e.AcknowledgedAt is null);
    }

    private bool MatchesKind(WatchEvent e) =>
        (_query.Kinds.Count == 0 || _query.Kinds.Contains(e.Kind))
        && (_query.Categories.Count == 0 || (Categories.TryGetValue(e.Kind, out var category) && _query.Categories.Contains(category)))
        && (_query.ObjectKinds.Count == 0 || _query.ObjectKinds.Contains(e.Object.Kind));

    private bool MatchesSubtree(WatchEvent e)
    {
        if (_subtreeInvalid)
        {
            return false;
        }

        return _subtree is null || AtOrBelow(e.Object.Dn, _subtree) || AtOrBelow(e.PreviousDn, _subtree);
    }

    private static bool AtOrBelow(string? dn, DistinguishedName ancestor) =>
        DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot && (parsed.Equals(ancestor) || parsed.IsDescendantOf(ancestor));
}
