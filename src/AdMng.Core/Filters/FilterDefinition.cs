namespace AdMng.Core.Filters;

/// <summary>Comparison of one condition of the filter builder (README U4, "Własne filtry").</summary>
public enum FilterOperator
{
    /// <summary>Any value equals the text ("równa się"); numbers compare as numbers.</summary>
    Equals,

    /// <summary>No value equals the text ("różne od").</summary>
    NotEquals,

    /// <summary>Any value contains the text ("zawiera").</summary>
    Contains,

    /// <summary>Any value starts with the text ("zaczyna się od").</summary>
    StartsWith,

    /// <summary>Any value ends with the text ("kończy się na").</summary>
    EndsWith,

    /// <summary>The attribute has a value ("istnieje").</summary>
    Present,

    /// <summary>The attribute has no value ("nie istnieje").</summary>
    NotPresent,

    /// <summary>A value ≥ the number ("większe lub równe").</summary>
    GreaterOrEqual,

    /// <summary>A value ≤ the number ("mniejsze lub równe").</summary>
    LessOrEqual,

    /// <summary>A date older than N days ("data starsza niż N dni"): FILETIME or GeneralizedTime.</summary>
    OlderThanDays,

    /// <summary>A date newer than N days ("data nowsza niż N dni").</summary>
    NewerThanDays,

    /// <summary>All bits of the mask are set ("flaga ustawiona", e.g. userAccountControl 2).</summary>
    BitSet,

    /// <summary>Not all bits of the mask are set ("flaga nieustawiona").</summary>
    BitNotSet,

    /// <summary>Member of the group, nested ("członek grupy", LDAP_MATCHING_RULE_IN_CHAIN); the value is the group DN.</summary>
    MemberOf,

    /// <summary>Not a member of the group, nested.</summary>
    NotMemberOf,
}

/// <summary>How conditions (or groups of conditions) combine: "I" (all) or "LUB" (any).</summary>
public enum FilterCombinator
{
    All,
    Any,
}

/// <summary>One condition: attribute, operator, value (text as typed; DNs of groups in the portable <c>{domain}</c> form).</summary>
/// <param name="Attribute">LDAP display name (ignored for <see cref="FilterOperator.MemberOf"/>, which uses memberOf).</param>
/// <param name="Operator">The comparison.</param>
/// <param name="Value">The value; empty for <see cref="FilterOperator.Present"/> / <see cref="FilterOperator.NotPresent"/>.</param>
public sealed record FilterCondition(string Attribute, FilterOperator Operator, string Value = "");

/// <summary>A group of conditions combined with <see cref="Combinator"/>.</summary>
public sealed record FilterConditionGroup
{
    public FilterCombinator Combinator { get; init; } = FilterCombinator.All;

    public IReadOnlyList<FilterCondition> Conditions { get; init; } = [];
}

/// <summary>
/// A filter of the builder: groups of conditions combined with <see cref="Combinator"/>, or a raw LDAP filter for advanced
/// users (<see cref="RawLdap"/> wins when set). Text only - names, attribute names, operators, typed values and group DNs;
/// never directory data. Compiled with <see cref="FilterCompiler"/> (every value escaped by <c>LdapFilter</c>, CLAUDE.md
/// rule 3) and evaluated on the client by <see cref="FilterEvaluator"/> where the server cannot (constructed attributes).
/// </summary>
public sealed record FilterDefinition
{
    public FilterCombinator Combinator { get; init; } = FilterCombinator.All;

    public IReadOnlyList<FilterConditionGroup> Groups { get; init; } = [];

    /// <summary>A raw LDAP filter typed by the operator (validated); null or empty = built from <see cref="Groups"/>.</summary>
    public string? RawLdap { get; init; }

    /// <summary>The filter is a raw LDAP filter.</summary>
    public bool IsRaw => !string.IsNullOrWhiteSpace(RawLdap);

    /// <summary>No condition and no raw filter.</summary>
    public bool IsEmpty => !IsRaw && Groups.All(g => g.Conditions.Count == 0);

    /// <summary>All conditions of all groups.</summary>
    public IEnumerable<FilterCondition> AllConditions => Groups.SelectMany(g => g.Conditions);

    /// <summary>A filter of one group with these conditions combined with "I" (all).</summary>
    public static FilterDefinition Of(params FilterCondition[] conditions) =>
        new() { Groups = [new FilterConditionGroup { Conditions = conditions }] };

    /// <summary>A raw LDAP filter.</summary>
    public static FilterDefinition Raw(string ldap) => new() { RawLdap = ldap };
}
