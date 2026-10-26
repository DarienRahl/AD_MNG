using System.Collections.Frozen;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;

namespace AdMng.Core.Filters;

/// <summary>What a value of an attribute is, for the operators that need to know (numbers, dates, flags).</summary>
public enum FilterValueKind
{
    Text,
    Integer,
    Boolean,
    DistinguishedName,

    /// <summary>A FILETIME large integer (pwdLastSet, lastLogonTimestamp, accountExpires …).</summary>
    FileTime,

    /// <summary>A GeneralizedTime string (whenCreated, whenChanged).</summary>
    GeneralizedTime,
}

/// <summary>Why a condition cannot be compiled (the UI shows a localized text per code).</summary>
public enum FilterIssueCode
{
    /// <summary>The filter has no condition.</summary>
    Empty,

    /// <summary>No attribute chosen.</summary>
    MissingAttribute,

    /// <summary>The attribute name has characters an attribute description cannot have.</summary>
    InvalidAttribute,

    /// <summary>The operator needs a value.</summary>
    MissingValue,

    /// <summary>The operator needs a whole number.</summary>
    NotANumber,

    /// <summary>"Older/newer than N days" needs 0 … 36 500 days.</summary>
    InvalidDays,

    /// <summary>The operator compares dates but the attribute holds no date.</summary>
    NotADate,

    /// <summary>A bit mask must be a positive number (decimal or 0x hex).</summary>
    InvalidMask,

    /// <summary>The group of "member of" is not a valid DN.</summary>
    InvalidGroup,

    /// <summary>The raw LDAP filter is not valid.</summary>
    InvalidRawFilter,
}

/// <summary>One problem of a filter: the code and where (group and condition index; -1 for the whole filter).</summary>
public sealed record FilterIssue(FilterIssueCode Code, int GroupIndex = -1, int ConditionIndex = -1);

/// <summary>A filter definition that cannot be compiled.</summary>
public sealed class FilterDefinitionException(IReadOnlyList<FilterIssue> issues)
    : FormatException($"The filter is not valid: {string.Join(", ", issues.Select(i => i.Code))}.")
{
    public IReadOnlyList<FilterIssue> Issues { get; } = issues;
}

/// <summary>What a compiled filter needs: the server part and whether the client must check the rest.</summary>
/// <param name="ServerFilter">
/// A filter the DC evaluates; every object the full filter matches matches it (a necessary condition). Null when the
/// server cannot narrow down at all (e.g. "LUB" with a constructed attribute).
/// </param>
/// <param name="NeedsClientCheck">Constructed attributes are involved: <see cref="FilterEvaluator"/> decides on the client.</param>
/// <param name="Attributes">Attributes the client check reads (empty without a client check).</param>
public sealed record FilterPlan(string? ServerFilter, bool NeedsClientCheck, IReadOnlyList<string> Attributes);

/// <summary>What compiling and evaluating a filter needs.</summary>
public sealed record FilterContext
{
    /// <summary>Reference time of the date operators.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>Replaces <c>{domain}</c> in group DNs; null leaves it (the filter then cannot be sent to a DC).</summary>
    public string? DefaultNamingContext { get; init; }

    /// <summary>Schema of the session: syntaxes and constructed attributes; null = names known to the application.</summary>
    public ISchemaCatalog? Schema { get; init; }
}

/// <summary>
/// Compiles <see cref="FilterDefinition"/> into an LDAP filter, only through <see cref="LdapFilter"/> (every value escaped,
/// attribute names checked - CLAUDE.md rule 3), and splits it into what the server can evaluate and what the client must
/// check (constructed attributes cannot be used in a filter).
/// </summary>
public static class FilterCompiler
{
    /// <summary>Placeholder of the default naming context in portable DNs.</summary>
    public const string DomainPlaceholder = "{domain}";

    /// <summary>Largest N of the day operators (100 years).</summary>
    public const int MaxDays = 36_500;

    /// <summary>Constructed attributes the application knows (used when no schema is at hand).</summary>
    private static readonly FrozenSet<string> KnownConstructed = new[]
    {
        Attr.UserAccountControlComputed, Attr.PasswordExpiryComputed, Attr.TokenGroups, Attr.CanonicalName,
        Attr.AllowedAttributesEffective, Attr.AllowedChildClassesEffective, Attr.SdRightsEffective, Attr.MsDsPrincipalName,
        "msDS-ResultantPSO", "primaryGroupToken", "tokenGroupsGlobalAndUniversal", "tokenGroupsNoGCAcceptable",
        "msDS-ReplAttributeMetaData", "msDS-ReplValueMetaData", "createTimeStamp", "modifyTimeStamp", "structuralObjectClass",
        "msDS-isRODC", "msDS-isGC", "msDS-Approx-Immed-Subordinates", "parentGUID", "entryTTL",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> KnownGeneralizedTime = new[]
    {
        Attr.WhenCreated, Attr.WhenChanged, "dSCorePropagationData", "createTimeStamp", "modifyTimeStamp",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The operators that take no value.</summary>
    public static bool NeedsValue(FilterOperator op) => op is not (FilterOperator.Present or FilterOperator.NotPresent);

    /// <summary>The operators whose value is a group DN (and whose attribute is memberOf).</summary>
    public static bool IsMembership(FilterOperator op) => op is FilterOperator.MemberOf or FilterOperator.NotMemberOf;

    /// <summary>The attribute of a condition (memberOf for the membership operators).</summary>
    public static string AttributeOf(FilterCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return IsMembership(condition.Operator) ? Attr.MemberOf : (condition.Attribute ?? string.Empty).Trim();
    }

    /// <summary>A constructed attribute (computed by the DC, readable but never usable in a filter).</summary>
    public static bool IsConstructed(string attribute, ISchemaCatalog? schema)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return schema?.FindAttribute(attribute) is { } definition ? definition.IsConstructed : KnownConstructed.Contains(attribute);
    }

    /// <summary>The kind of the values of an attribute (schema syntax; known names without a schema).</summary>
    public static FilterValueKind ValueKind(string attribute, ISchemaCatalog? schema)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (schema?.FindAttribute(attribute) is { } definition)
        {
            return definition.Syntax switch
            {
                AttributeSyntax.GeneralizedTime or AttributeSyntax.UtcTime => FilterValueKind.GeneralizedTime,
                AttributeSyntax.LargeInteger => Attr.FileTimeAttributes.Contains(attribute) || IsLikelyFileTime(attribute)
                    ? FilterValueKind.FileTime
                    : FilterValueKind.Integer,
                AttributeSyntax.Integer or AttributeSyntax.Enumeration => FilterValueKind.Integer,
                AttributeSyntax.Boolean => FilterValueKind.Boolean,
                AttributeSyntax.DistinguishedName => FilterValueKind.DistinguishedName,
                _ => FilterValueKind.Text,
            };
        }

        if (Attr.FileTimeAttributes.Contains(attribute))
        {
            return FilterValueKind.FileTime;
        }

        if (KnownGeneralizedTime.Contains(attribute))
        {
            return FilterValueKind.GeneralizedTime;
        }

        return attribute.Equals(Attr.UserAccountControl, StringComparison.OrdinalIgnoreCase)
            || attribute.Equals(Attr.AdminCount, StringComparison.OrdinalIgnoreCase)
            || attribute.Equals(Attr.PrimaryGroupId, StringComparison.OrdinalIgnoreCase)
            || attribute.Equals(Attr.GroupType, StringComparison.OrdinalIgnoreCase)
            || attribute.Equals(Attr.SamAccountType, StringComparison.OrdinalIgnoreCase)
            || attribute.Equals(Attr.UserAccountControlComputed, StringComparison.OrdinalIgnoreCase)
            ? FilterValueKind.Integer
            : FilterValueKind.Text;
    }

    /// <summary>Every problem of the definition (empty when it compiles).</summary>
    public static IReadOnlyList<FilterIssue> Validate(FilterDefinition definition, FilterContext context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        if (definition.IsRaw)
        {
            return LdapFilter.Validate(definition.RawLdap) is null ? [] : [new FilterIssue(FilterIssueCode.InvalidRawFilter)];
        }

        var issues = new List<FilterIssue>();
        var any = false;
        for (var g = 0; g < definition.Groups.Count; g++)
        {
            var conditions = definition.Groups[g]?.Conditions ?? [];
            for (var c = 0; c < conditions.Count; c++)
            {
                any = true;
                if (Check(conditions[c], context) is { } code)
                {
                    issues.Add(new FilterIssue(code, g, c));
                }
            }
        }

        if (!any)
        {
            issues.Add(new FilterIssue(FilterIssueCode.Empty));
        }

        return issues;
    }

    /// <summary>
    /// The complete LDAP filter (also conditions on constructed attributes - the preview shows them; a DC would not
    /// evaluate them, see <see cref="Plan"/>). Throws <see cref="FilterDefinitionException"/> when the definition is not valid.
    /// </summary>
    public static string ToLdap(FilterDefinition definition, FilterContext context)
    {
        ThrowIfInvalid(definition, context);
        return definition.IsRaw
            ? LdapFilter.Wrap(definition.RawLdap!)
            : Combine(definition.Combinator, definition.Groups.Select(g => Combine(g.Combinator, g.Conditions.Select(c => Compile(c, context)))));
    }

    /// <summary>
    /// The server filter and the client check of a definition. Raw filters go to the server as typed. Throws
    /// <see cref="FilterDefinitionException"/> when the definition is not valid.
    /// </summary>
    public static FilterPlan Plan(FilterDefinition definition, FilterContext context)
    {
        ThrowIfInvalid(definition, context);
        if (definition.IsRaw)
        {
            return new FilterPlan(LdapFilter.Wrap(definition.RawLdap!), false, []);
        }

        var needsClient = false;
        var groupFilters = new List<string?>();
        foreach (var group in definition.Groups.Where(g => g.Conditions.Count > 0))
        {
            var parts = group.Conditions.Select(c => IsConstructed(AttributeOf(c), context.Schema) ? null : Compile(c, context)).ToList();
            var clientParts = parts.Count(p => p is null);
            needsClient |= clientParts > 0;
            groupFilters.Add(clientParts == parts.Count || (group.Combinator == FilterCombinator.Any && clientParts > 0)
                ? null
                : Combine(group.Combinator, parts));
        }

        string? server = definition.Combinator == FilterCombinator.Any && groupFilters.Any(f => f is null)
            ? null
            : groupFilters.Any(f => f is not null) ? Combine(definition.Combinator, groupFilters) : null;
        var attributes = needsClient
            ? definition.AllConditions.Select(AttributeOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        return new FilterPlan(server, needsClient, attributes);
    }

    /// <summary>The LDAP filter of one valid condition.</summary>
    public static string Compile(FilterCondition condition, FilterContext context)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(context);
        if (Check(condition, context) is { } code)
        {
            throw new FilterDefinitionException([new FilterIssue(code)]);
        }

        var attribute = AttributeOf(condition);
        var value = (condition.Value ?? string.Empty).Trim();
        var kind = ValueKind(attribute, context.Schema);
        return condition.Operator switch
        {
            FilterOperator.Equals => EqualFilter(attribute, value, kind),
            FilterOperator.NotEquals => LdapFilter.Not(EqualFilter(attribute, value, kind)),
            FilterOperator.Contains => LdapFilter.Contains(attribute, value),
            FilterOperator.StartsWith => LdapFilter.StartsWith(attribute, value),
            FilterOperator.EndsWith => LdapFilter.EndsWith(attribute, value),
            FilterOperator.Present => LdapFilter.Present(attribute),
            FilterOperator.NotPresent => LdapFilter.NotPresent(attribute),
            FilterOperator.GreaterOrEqual => Ordering(attribute, value, kind, greater: true),
            FilterOperator.LessOrEqual => Ordering(attribute, value, kind, greater: false),
            FilterOperator.OlderThanDays => Days(attribute, value, kind, context.Now, older: true),
            FilterOperator.NewerThanDays => Days(attribute, value, kind, context.Now, older: false),
            FilterOperator.BitSet => LdapFilter.BitAnd(attribute, ParseMask(value)!.Value),
            FilterOperator.BitNotSet => LdapFilter.Not(LdapFilter.BitAnd(attribute, ParseMask(value)!.Value)),
            FilterOperator.MemberOf => LdapFilter.InChain(Attr.MemberOf, ResolveDn(value, context)),
            FilterOperator.NotMemberOf => LdapFilter.Not(LdapFilter.InChain(Attr.MemberOf, ResolveDn(value, context))),
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition.Operator, null),
        };
    }

    /// <summary>A portable DN (<c>{domain}</c>) in the domain of the context; unchanged without a naming context.</summary>
    public static string ResolveDn(string dn, FilterContext context)
    {
        ArgumentNullException.ThrowIfNull(dn);
        ArgumentNullException.ThrowIfNull(context);
        return context.DefaultNamingContext is { } nc
            ? dn.Trim().Replace(DomainPlaceholder, nc, StringComparison.OrdinalIgnoreCase)
            : dn.Trim();
    }

    /// <summary>A bit mask: a positive decimal number or 0x hex.</summary>
    public static long? ParseMask(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        long mask;
        var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out mask)
            : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out mask);
        return ok && mask > 0 ? mask : null;
    }

    /// <summary>A whole number (sign allowed), invariant culture.</summary>
    public static long? ParseInteger(string? value) =>
        long.TryParse((value ?? string.Empty).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>A date typed as yyyy-MM-dd (UTC midnight), for ≥/≤ on date attributes.</summary>
    public static DateTimeOffset? ParseDate(string? value) =>
        DateOnly.TryParseExact((value ?? string.Empty).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;

    private static FilterIssueCode? Check(FilterCondition? condition, FilterContext context)
    {
        if (condition is null)
        {
            return FilterIssueCode.MissingAttribute;
        }

        var attribute = AttributeOf(condition);
        if (attribute.Length == 0)
        {
            return FilterIssueCode.MissingAttribute;
        }

        if (!attribute.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ';' or '.'))
        {
            return FilterIssueCode.InvalidAttribute;
        }

        var value = (condition.Value ?? string.Empty).Trim();
        if (NeedsValue(condition.Operator) && value.Length == 0)
        {
            return FilterIssueCode.MissingValue;
        }

        var kind = ValueKind(attribute, context.Schema);
        switch (condition.Operator)
        {
            case FilterOperator.Equals or FilterOperator.NotEquals when kind == FilterValueKind.Integer && ParseInteger(value) is null:
                return FilterIssueCode.NotANumber;
            case FilterOperator.GreaterOrEqual or FilterOperator.LessOrEqual:
                var ok = kind switch
                {
                    FilterValueKind.Integer => ParseInteger(value) is not null,
                    FilterValueKind.FileTime => ParseInteger(value) is not null || ParseDate(value) is not null,
                    FilterValueKind.GeneralizedTime => ParseDate(value) is not null,
                    _ => true,
                };
                return ok ? null : kind is FilterValueKind.GeneralizedTime ? FilterIssueCode.NotADate : FilterIssueCode.NotANumber;
            case FilterOperator.OlderThanDays or FilterOperator.NewerThanDays:
                if (kind is not (FilterValueKind.FileTime or FilterValueKind.GeneralizedTime))
                {
                    return FilterIssueCode.NotADate;
                }

                return ParseInteger(value) is >= 0 and <= MaxDays ? null : FilterIssueCode.InvalidDays;
            case FilterOperator.BitSet or FilterOperator.BitNotSet:
                return ParseMask(value) is null ? FilterIssueCode.InvalidMask : null;
            case FilterOperator.MemberOf or FilterOperator.NotMemberOf:
                var probe = value.Replace(DomainPlaceholder, "DC=example,DC=com", StringComparison.OrdinalIgnoreCase);
                return DistinguishedName.TryParse(probe, out var dn) && !dn.IsRoot ? null : FilterIssueCode.InvalidGroup;
        }

        return null;
    }

    private static void ThrowIfInvalid(FilterDefinition definition, FilterContext context)
    {
        var issues = Validate(definition, context);
        if (issues.Count > 0)
        {
            throw new FilterDefinitionException(issues);
        }
    }

    private static string Combine(FilterCombinator combinator, IEnumerable<string?> parts) =>
        combinator == FilterCombinator.Any ? LdapFilter.Or(parts) : LdapFilter.And(parts);

    private static string EqualFilter(string attribute, string value, FilterValueKind kind) => kind switch
    {
        FilterValueKind.Integer => LdapFilter.Equal(attribute, ParseInteger(value)!.Value),
        FilterValueKind.Boolean => LdapFilter.Equal(attribute, value.ToUpperInvariant()),
        _ => LdapFilter.Equal(attribute, value),
    };

    private static string Ordering(string attribute, string value, FilterValueKind kind, bool greater)
    {
        switch (kind)
        {
            case FilterValueKind.Integer:
                var number = ParseInteger(value)!.Value;
                return greater ? LdapFilter.GreaterOrEqual(attribute, number) : LdapFilter.LessOrEqual(attribute, number);
            case FilterValueKind.FileTime:
                var fileTime = ParseInteger(value) ?? AdConvert.ToFileTime(ParseDate(value)!.Value);
                return greater ? LdapFilter.GreaterOrEqual(attribute, fileTime) : LdapFilter.LessOrEqual(attribute, fileTime);
            case FilterValueKind.GeneralizedTime:
                var time = AdConvert.ToGeneralizedTime(ParseDate(value)!.Value);
                return greater ? LdapFilter.GreaterOrEqual(attribute, time) : LdapFilter.LessOrEqual(attribute, time);
            default:
                return greater ? LdapFilter.GreaterOrEqual(attribute, value) : LdapFilter.LessOrEqual(attribute, value);
        }
    }

    /// <summary>
    /// "Older than N days": a date at or before now − N (FILETIME 0 = never is not a date). "Newer": at or after now − N
    /// (FILETIME 2^63−1 = never is not a date either).
    /// </summary>
    private static string Days(string attribute, string value, FilterValueKind kind, DateTimeOffset now, bool older)
    {
        var threshold = now.AddDays(-ParseInteger(value)!.Value);
        if (kind == FilterValueKind.GeneralizedTime)
        {
            var time = AdConvert.ToGeneralizedTime(threshold);
            return older ? LdapFilter.LessOrEqual(attribute, time) : LdapFilter.GreaterOrEqual(attribute, time);
        }

        var fileTime = AdConvert.ToFileTime(threshold);
        return older
            ? LdapFilter.And(LdapFilter.GreaterOrEqual(attribute, 1), LdapFilter.LessOrEqual(attribute, fileTime))
            : LdapFilter.And(LdapFilter.GreaterOrEqual(attribute, fileTime), LdapFilter.Not(LdapFilter.Equal(attribute, long.MaxValue)));
    }

    private static bool IsLikelyFileTime(string attribute) =>
        attribute.Contains("Time", StringComparison.OrdinalIgnoreCase) || attribute.Contains("LastLogon", StringComparison.OrdinalIgnoreCase)
        || attribute.Contains("Expires", StringComparison.OrdinalIgnoreCase);
}
