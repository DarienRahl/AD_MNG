using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// The SQL of <c>GET /api/v1/events</c> (spec 17.2): every <see cref="EventQuery"/> filter as an indexed column condition
/// (kinds and categories as kind lists, text as a substring of the lower-case search text, tags and rule ids against the
/// "|a|b|" columns, subtree as a normalized DN suffix confirmed by <c>wd_at_or_below</c>, half-open <c>[From, To)</c>),
/// plus the cursor paging (<c>After</c> ascending, otherwise descending with <c>Before</c>), limit 1..500.
/// </summary>
internal sealed class EventQuerySql
{
    public const int MaxLimit = 500;

    private readonly List<string> _conditions = [];
    private readonly List<(string Name, object? Value)> _parameters = [];

    /// <param name="query">The filters and the cursor.</param>
    /// <param name="textMatchesSources">False for Minimal viewers: <c>Text</c> does not match workstation and IP, which
    /// they cannot see (otherwise hit/miss would reveal them).</param>
    public EventQuerySql(EventQuery query, bool textMatchesSources = true)
    {
        ArgumentNullException.ThrowIfNull(query);
        Limit = Math.Clamp(query.Limit, 1, MaxLimit);
        Ascending = query.After is not null;
        if (query.After is { } after)
        {
            Add("id > $after", ("$after", after));
        }
        else if (query.Before is { } before)
        {
            Add("id < $before", ("$before", before));
        }

        AddKinds(query);
        if (query.ObjectKinds.Count > 0)
        {
            _conditions.Add($"object_kind IN ({SqliteValues.InList("ok", [.. query.ObjectKinds.Select(k => (object)(int)k)], _parameters)})");
        }

        if (query.MinSeverity is { } minSeverity)
        {
            Add("severity >= $minSeverity", ("$minSeverity", (int)minSeverity));
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            Add(
                textMatchesSources ? "instr(search_text, $text) > 0" : "instr(wd_search_visible(search_text), $text) > 0",
                ("$text", query.Text.Trim().ToLowerInvariant()));
        }

        AddSubtree(query.Subtree);
        for (var i = 0; i < query.Tags.Count; i++)
        {
            Add($"instr(tags, $tag{i}) > 0", ($"$tag{i}", SqliteValues.PipeNeedle(query.Tags[i])));
        }

        if (query.AttributionStatuses.Count > 0)
        {
            _conditions.Add($"actor_status IN ({SqliteValues.InList("as", [.. query.AttributionStatuses.Select(s => (object)(int)s)], _parameters)})");
        }

        if (query.From is { } from)
        {
            Add("detected_at >= $from", ("$from", SqliteValues.ToUnixMs(from)));
        }

        if (query.To is { } to)
        {
            Add("detected_at < $to", ("$to", SqliteValues.ToUnixMs(to)));
        }

        if (!string.IsNullOrWhiteSpace(query.RuleId))
        {
            Add("instr(coalesce(matched_rules, ''), $rule) > 0", ("$rule", SqliteValues.PipeNeedle(query.RuleId)));
        }

        if (query.ObjectGuid is { } objectGuid)
        {
            Add("object_guid = $objectGuid", ("$objectGuid", SqliteValues.ToBlob(objectGuid)));
        }

        if (!string.IsNullOrWhiteSpace(query.ActorSid))
        {
            Add("actor_sid = $actorSid", ("$actorSid", SqliteValues.SidOrNull(query.ActorSid)));
        }

        if (query.UnacknowledgedOnly)
        {
            _conditions.Add("acked_at IS NULL");
        }
    }

    public int Limit { get; }

    public bool Ascending { get; }

    /// <summary>The page query: <see cref="Limit"/> + 1 rows (the extra row only sets HasMore).</summary>
    public string Sql =>
        $"SELECT {EventRows.Columns} FROM events{Where} ORDER BY id {(Ascending ? "ASC" : "DESC")} LIMIT {Limit + 1};";

    public IReadOnlyList<(string Name, object? Value)> Parameters => _parameters;

    private string Where => _conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", _conditions);

    private void Add(string condition, (string Name, object? Value) parameter)
    {
        _conditions.Add(condition);
        _parameters.Add(parameter);
    }

    private void AddKinds(EventQuery query)
    {
        if (query.Kinds.Count > 0)
        {
            _conditions.Add($"kind IN ({SqliteValues.InList("k", [.. query.Kinds.Distinct().Select(k => (object)(int)k)], _parameters)})");
        }

        if (query.Categories.Count > 0)
        {
            var kinds = EventKindCatalog.All.Where(k => query.Categories.Contains(k.Category)).Select(k => (object)(int)k.Kind).ToList();
            _conditions.Add(kinds.Count == 0 ? "0" : $"kind IN ({SqliteValues.InList("c", kinds, _parameters)})");
        }
    }

    private void AddSubtree(string? subtree)
    {
        if (string.IsNullOrWhiteSpace(subtree))
        {
            return;
        }

        if (!SqliteValues.IsAtOrBelow(subtree, subtree))
        {
            _conditions.Add("0");   // not a DN: nothing matches
            return;
        }

        var norm = SqliteValues.NormalizeDn(subtree);
        _parameters.Add(("$subtree", norm));
        _parameters.Add(("$subtreeSuffix", "," + norm));
        _conditions.Add(
            "((object_dn_norm = $subtree OR substr(object_dn_norm, -length($subtreeSuffix)) = $subtreeSuffix) AND wd_at_or_below(object_dn_norm, $subtree)"
            + " OR (previous_dn_norm = $subtree OR substr(previous_dn_norm, -length($subtreeSuffix)) = $subtreeSuffix) AND wd_at_or_below(previous_dn_norm, $subtree))");
    }
}
