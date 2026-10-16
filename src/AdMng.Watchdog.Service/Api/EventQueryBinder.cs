using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using Microsoft.Extensions.Primitives;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// The query string of <c>GET /api/v1/events</c> → <see cref="EventQuery"/> with the keys of
/// <see cref="EventQuery.ToQueryString"/> (<c>after</c>, <c>before</c>, <c>limit</c>, <c>kind</c>*, <c>category</c>*,
/// <c>objectKind</c>*, <c>minSeverity</c>, <c>text</c>, <c>subtree</c>, <c>tag</c>*, <c>attributionStatus</c>*,
/// <c>from</c>, <c>to</c>, <c>ruleId</c>, <c>objectGuid</c>, <c>actorSid</c>, <c>unacknowledgedOnly</c>, <c>wait</c>).
/// Invalid values are validation issues (the value itself is never echoed).
/// </summary>
public static class EventQueryBinder
{
    public static (EventQuery? Query, IReadOnlyList<ConfigIssue> Issues) Bind(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var issues = new List<ConfigIssue>();
        var result = new EventQuery
        {
            After = Long(query, "after", issues),
            Before = Long(query, "before", issues),
            Limit = (int?)Long(query, "limit", issues) ?? 100,
            Kinds = Enums<WatchEventKind>(query, "kind", issues),
            Categories = Enums<EventCategory>(query, "category", issues),
            ObjectKinds = Enums<WatchObjectKind>(query, "objectKind", issues),
            MinSeverity = Enums<Severity>(query, "minSeverity", issues).Cast<Severity?>().FirstOrDefault(),
            Text = Text(query, "text"),
            Subtree = Text(query, "subtree"),
            Tags = [.. query["tag"].Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim())],
            AttributionStatuses = Enums<AttributionStatus>(query, "attributionStatus", issues),
            From = Time(query, "from", issues),
            To = Time(query, "to", issues),
            RuleId = Text(query, "ruleId"),
            ObjectGuid = Guid(query, "objectGuid", issues),
            ActorSid = Text(query, "actorSid"),
            UnacknowledgedOnly = Bool(query, "unacknowledgedOnly", issues),
            WaitSeconds = (int?)Long(query, "wait", issues) ?? 0,
        };

        if (result.Limit is < 1 or > Hosting.ApiLimits.MaxPageSize)
        {
            issues.Add(Issue("limit", "range", "limit must be between 1 and 500."));
        }

        if (result.WaitSeconds is < 0 or > Hosting.ApiLimits.MaxWaitSeconds)
        {
            issues.Add(Issue("wait", "range", "wait must be between 0 and 30 seconds."));
        }
        else if (result.WaitSeconds > 0 && result.After is null)
        {
            issues.Add(Issue("wait", "waitWithoutAfter", "wait is allowed only together with after."));
        }

        return issues.Count == 0 ? (result, issues) : (null, issues);
    }

    internal static ConfigIssue Issue(string path, string code, string message) =>
        new() { Path = path, Code = code, Severity = IssueSeverity.Error, Message = message };

    private static string? Text(IQueryCollection query, string key)
    {
        var value = query[key].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static long? Long(IQueryCollection query, string key, List<ConfigIssue> issues)
    {
        if (Single(query, key) is not { } text)
        {
            return null;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        issues.Add(Issue(key, "invalidNumber", key + " must be an integer."));
        return null;
    }

    private static bool Bool(IQueryCollection query, string key, List<ConfigIssue> issues)
    {
        if (Single(query, key) is not { } text)
        {
            return false;
        }

        if (bool.TryParse(text, out var value))
        {
            return value;
        }

        issues.Add(Issue(key, "invalidBoolean", key + " must be true or false."));
        return false;
    }

    private static DateTimeOffset? Time(IQueryCollection query, string key, List<ConfigIssue> issues)
    {
        if (Single(query, key) is not { } text)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            return value;
        }

        issues.Add(Issue(key, "invalidTime", key + " must be an ISO 8601 time."));
        return null;
    }

    private static Guid? Guid(IQueryCollection query, string key, List<ConfigIssue> issues)
    {
        if (Single(query, key) is not { } text)
        {
            return null;
        }

        if (System.Guid.TryParse(text, out var value))
        {
            return value;
        }

        issues.Add(Issue(key, "invalidGuid", key + " must be a GUID."));
        return null;
    }

    private static List<TEnum> Enums<TEnum>(IQueryCollection query, string key, List<ConfigIssue> issues)
        where TEnum : struct, Enum
    {
        var result = new List<TEnum>();
        foreach (var text in query[key])
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (Enum.TryParse<TEnum>(text.Trim(), ignoreCase: true, out var value) && Enum.IsDefined(value) && !char.IsDigit(text.Trim()[0]))
            {
                result.Add(value);
            }
            else
            {
                issues.Add(Issue(key, "invalidValue", key + " has an unknown value."));
            }
        }

        return result;
    }

    private static string? Single(IQueryCollection query, string key)
    {
        StringValues values = query[key];
        var text = values.Count > 0 ? values[^1] : null;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
