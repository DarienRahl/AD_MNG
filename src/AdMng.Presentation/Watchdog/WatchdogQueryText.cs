using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Event filter presets as text: <see cref="EventQuery.ToQueryString"/> without the paging and long-poll keys, and the
/// reverse (keys of Contracts/Api/QueryStringWriter). Unknown keys and invalid values are ignored, never thrown.
/// </summary>
public static class WatchdogQueryText
{
    /// <summary>The filter part of <paramref name="query"/> (no after/before/wait, default limit) as a query string.</summary>
    public static string ToPreset(EventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return (query with { After = null, Before = null, WaitSeconds = 0, Limit = 100 }).ToQueryString();
    }

    /// <summary>Parses a query string written by <see cref="EventQuery.ToQueryString"/> (paging keys are dropped).</summary>
    public static EventQuery Parse(string? text)
    {
        var query = new EventQuery();
        if (string.IsNullOrWhiteSpace(text))
        {
            return query;
        }

        var kinds = new List<WatchEventKind>();
        var categories = new List<EventCategory>();
        var objectKinds = new List<WatchObjectKind>();
        var tags = new List<string>();
        var statuses = new List<AttributionStatus>();
        foreach (var pair in text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = pair[..separator];
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            switch (key)
            {
                case "kind":
                    AddEnum(kinds, value);
                    break;
                case "category":
                    AddEnum(categories, value);
                    break;
                case "objectKind":
                    AddEnum(objectKinds, value);
                    break;
                case "attributionStatus":
                    AddEnum(statuses, value);
                    break;
                case "tag" when value.Length > 0:
                    tags.Add(value);
                    break;
                case "minSeverity" when Enum.TryParse<Severity>(value, ignoreCase: false, out var severity) && Enum.IsDefined(severity):
                    query = query with { MinSeverity = severity };
                    break;
                case "text":
                    query = query with { Text = value };
                    break;
                case "subtree":
                    query = query with { Subtree = value };
                    break;
                case "ruleId":
                    query = query with { RuleId = value };
                    break;
                case "actorSid":
                    query = query with { ActorSid = value };
                    break;
                case "objectGuid" when Guid.TryParse(value, out var guid):
                    query = query with { ObjectGuid = guid };
                    break;
                case "from" when TryParseTime(value, out var from):
                    query = query with { From = from };
                    break;
                case "to" when TryParseTime(value, out var to):
                    query = query with { To = to };
                    break;
                case "unacknowledgedOnly":
                    query = query with { UnacknowledgedOnly = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) };
                    break;
            }
        }

        return query with { Kinds = kinds, Categories = categories, ObjectKinds = objectKinds, Tags = tags, AttributionStatuses = statuses };
    }

    private static void AddEnum<T>(List<T> list, string value)
        where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) && !list.Contains(parsed))
        {
            list.Add(parsed);
        }
    }

    private static bool TryParseTime(string value, out DateTimeOffset time) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
