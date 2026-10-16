using System.Globalization;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Contracts.Api;

/// <summary>Filter and paging of <c>GET /api/v1/events</c>.</summary>
public sealed record EventQuery
{
    public long? After { get; init; }          // ascending ids > After (live polling)
    public long? Before { get; init; }         // descending ids < Before (paging back); ignored when After is set
    public int Limit { get; init; } = 100;     // 1..500
    public IReadOnlyList<WatchEventKind> Kinds { get; init; } = [];
    public IReadOnlyList<EventCategory> Categories { get; init; } = [];
    public IReadOnlyList<WatchObjectKind> ObjectKinds { get; init; } = [];
    public Severity? MinSeverity { get; init; }
    /// <summary>Case-insensitive contains on name, sAMAccountName, DN, actor, workstation and IP address.</summary>
    public string? Text { get; init; }
    /// <summary>Object DN (or PreviousDn) equal to or below this DN (helpdesk per branch).</summary>
    public string? Subtree { get; init; }
    /// <summary>Event must carry ALL of these tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<AttributionStatus> AttributionStatuses { get; init; } = [];
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? RuleId { get; init; }
    public Guid? ObjectGuid { get; init; }
    public string? ActorSid { get; init; }
    public bool UnacknowledgedOnly { get; init; }
    public int WaitSeconds { get; init; }      // 0..30, only with After: long-poll until a newer event exists

    /// <summary>"after=1&amp;kind=UserCreated&amp;kind=…" – repeated keys for lists, ISO 8601 for times.</summary>
    /// <remarks>
    /// Keys (camelCase; only values that differ from the defaults are written, every value is URI-escaped):
    /// <c>after</c>, <c>before</c>, <c>limit</c> (omitted when 100), <c>kind</c>*, <c>category</c>*, <c>objectKind</c>*
    /// and <c>minSeverity</c> (enum names), <c>text</c>, <c>subtree</c>, <c>tag</c>*, <c>attributionStatus</c>*,
    /// <c>from</c> and <c>to</c> (round-trip "O" format), <c>ruleId</c>, <c>objectGuid</c> ("D"), <c>actorSid</c>,
    /// <c>unacknowledgedOnly=true</c>, <c>wait</c> (seconds, omitted when 0). * = repeated per list item.
    /// </remarks>
    public string ToQueryString()
    {
        var query = new QueryStringWriter();
        query.Add("after", After);
        query.Add("before", Before);
        if (Limit != 100)
        {
            query.Add("limit", Limit);
        }

        query.AddAll("kind", Kinds);
        query.AddAll("category", Categories);
        query.AddAll("objectKind", ObjectKinds);
        if (MinSeverity is { } minSeverity)
        {
            query.Add("minSeverity", minSeverity.ToString());
        }

        query.Add("text", Text);
        query.Add("subtree", Subtree);
        query.AddAll("tag", Tags);
        query.AddAll("attributionStatus", AttributionStatuses);
        query.Add("from", From);
        query.Add("to", To);
        query.Add("ruleId", RuleId);
        query.Add("objectGuid", ObjectGuid?.ToString("D", CultureInfo.InvariantCulture));
        query.Add("actorSid", ActorSid);
        if (UnacknowledgedOnly)
        {
            query.Add("unacknowledgedOnly", "true");
        }

        if (WaitSeconds != 0)
        {
            query.Add("wait", WaitSeconds);
        }

        return query.ToString();
    }
}
