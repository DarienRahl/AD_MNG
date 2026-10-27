using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Filters;

/// <summary>
/// Evaluates a <see cref="FilterDefinition"/> on an object read from the directory - the client side check for what the
/// server cannot evaluate (constructed attributes such as msDS-User-Account-Control-Computed). Semantics follow LDAP: a
/// condition matches when any value matches; the negations (<c>!</c>) also match an object without the attribute.
/// </summary>
public static class FilterEvaluator
{
    /// <summary>Whether the object matches. Raw LDAP filters are server-only and always match here.</summary>
    /// <param name="definition">A valid definition (see <see cref="FilterCompiler.Validate"/>).</param>
    /// <param name="obj">The object with the attributes of <see cref="FilterPlan.Attributes"/>.</param>
    /// <param name="context">Reference time, naming context and schema.</param>
    /// <param name="isMemberOf">
    /// Nested membership of a group DN (e.g. from the group graph of the privileged access review); null = the direct
    /// memberOf values only.
    /// </param>
    public static bool Matches(FilterDefinition definition, DirectoryObject obj, FilterContext context, Func<string, bool>? isMemberOf = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(context);
        if (definition.IsRaw)
        {
            return true;
        }

        var groups = definition.Groups.Where(g => g.Conditions.Count > 0).ToList();
        if (groups.Count == 0)
        {
            return true;
        }

        bool GroupMatches(FilterConditionGroup group) => group.Combinator == FilterCombinator.Any
            ? group.Conditions.Any(c => Matches(c, obj, context, isMemberOf))
            : group.Conditions.All(c => Matches(c, obj, context, isMemberOf));

        return definition.Combinator == FilterCombinator.Any ? groups.Any(GroupMatches) : groups.All(GroupMatches);
    }

    /// <summary>Whether one condition matches the object.</summary>
    public static bool Matches(FilterCondition condition, DirectoryObject obj, FilterContext context, Func<string, bool>? isMemberOf = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(context);
        var attribute = FilterCompiler.AttributeOf(condition);
        var value = (condition.Value ?? string.Empty).Trim();
        var kind = FilterCompiler.ValueKind(attribute, context.Schema);
        var values = Values(obj, attribute, context.Schema);
        return condition.Operator switch
        {
            FilterOperator.Equals => values.Any(v => AreEqual(v, value, kind)),
            FilterOperator.NotEquals => !values.Any(v => AreEqual(v, value, kind)),
            FilterOperator.Contains => values.Any(v => v.Contains(value, StringComparison.OrdinalIgnoreCase)),
            FilterOperator.StartsWith => values.Any(v => v.StartsWith(value, StringComparison.OrdinalIgnoreCase)),
            FilterOperator.EndsWith => values.Any(v => v.EndsWith(value, StringComparison.OrdinalIgnoreCase)),
            FilterOperator.Present => values.Count > 0,
            FilterOperator.NotPresent => values.Count == 0,
            FilterOperator.GreaterOrEqual => values.Any(v => Compare(v, value, kind) is >= 0),
            FilterOperator.LessOrEqual => values.Any(v => Compare(v, value, kind) is <= 0),
            FilterOperator.OlderThanDays => values.Any(v => Instant(v, kind) is { } at && at <= context.Now.AddDays(-Days(value))),
            FilterOperator.NewerThanDays => values.Any(v => Instant(v, kind) is { } at && at >= context.Now.AddDays(-Days(value))),
            FilterOperator.BitSet => HasBits(values, value),
            FilterOperator.BitNotSet => !HasBits(values, value),
            FilterOperator.MemberOf => IsMember(obj, FilterCompiler.ResolveDn(value, context), isMemberOf),
            FilterOperator.NotMemberOf => !IsMember(obj, FilterCompiler.ResolveDn(value, context), isMemberOf),
            _ => false,
        };
    }

    private static List<string> Values(DirectoryObject obj, string attribute, ISchemaCatalog? schema)
    {
        if (!obj.TryGet(attribute, out var values))
        {
            return [];
        }

        var isSid = schema?.FindAttribute(attribute)?.Syntax == AttributeSyntax.Sid
            || attribute.Equals(Attr.ObjectSid, StringComparison.OrdinalIgnoreCase)
            || attribute.Equals(Attr.SidHistory, StringComparison.OrdinalIgnoreCase);
        var result = new List<string>(values.Count);
        foreach (var raw in values)
        {
            result.Add(raw switch
            {
                byte[] bytes when isSid && bytes.Length >= 8 => AdConvert.SidToString(bytes),
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                _ => (string)raw,
            });
        }

        return result;
    }

    private static bool AreEqual(string actual, string expected, FilterValueKind kind) => kind switch
    {
        FilterValueKind.Integer or FilterValueKind.FileTime => FilterCompiler.ParseInteger(actual) is { } a && FilterCompiler.ParseInteger(expected) is { } e
            ? a == e
            : string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
        FilterValueKind.DistinguishedName => DistinguishedName.AreEqual(actual, expected),
        _ => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
    };

    private static int? Compare(string actual, string expected, FilterValueKind kind)
    {
        switch (kind)
        {
            case FilterValueKind.Integer:
                return FilterCompiler.ParseInteger(actual) is { } a && FilterCompiler.ParseInteger(expected) is { } e ? a.CompareTo(e) : null;
            case FilterValueKind.FileTime:
                var bound = FilterCompiler.ParseInteger(expected) ?? (FilterCompiler.ParseDate(expected) is { } date ? AdConvert.ToFileTime(date) : null);
                return FilterCompiler.ParseInteger(actual) is { } fileTime && bound is { } b ? fileTime.CompareTo(b) : null;
            case FilterValueKind.GeneralizedTime:
                return AdConvert.FromGeneralizedTime(actual) is { } at && FilterCompiler.ParseDate(expected) is { } day ? at.CompareTo(day) : null;
            default:
                return string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static DateTimeOffset? Instant(string value, FilterValueKind kind) => kind switch
    {
        FilterValueKind.GeneralizedTime => AdConvert.FromGeneralizedTime(value),
        _ => FilterCompiler.ParseInteger(value) is { } fileTime and > 0 and < long.MaxValue ? AdConvert.FromFileTime(fileTime) : null,
    };

    private static int Days(string value) => (int)Math.Clamp(FilterCompiler.ParseInteger(value) ?? 0, 0, FilterCompiler.MaxDays);

    private static bool HasBits(List<string> values, string mask) =>
        FilterCompiler.ParseMask(mask) is { } bits && values.Any(v => FilterCompiler.ParseInteger(v) is { } number && (number & bits) == bits);

    private static bool IsMember(DirectoryObject obj, string groupDn, Func<string, bool>? isMemberOf) =>
        isMemberOf?.Invoke(groupDn) ?? obj.GetStrings(Attr.MemberOf).Any(g => DistinguishedName.AreEqual(g, groupDn));
}
