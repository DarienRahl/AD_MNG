using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.Search;

// K7 contract (docs/dev/k-computers-and-gpo.md 21.2, 21.11) – implementation: W1.

/// <summary>What a <see cref="GpoSettingUnit"/> is (the row GPMC shows).</summary>
public enum GpoSettingUnitKind
{
    AdministrativeTemplate,
    RegistryValue,
    RegistryKey,
    SecurityValue,
    UserRight,
    RestrictedGroup,
    SecurityRegistryValue,
    Service,
    ObjectSecurity,
    AuditSubcategory,
    AuditOption,
    Preference,
    Script,
    ScriptConfiguration,
    ScriptFile,
    Comment,
    UnreadableFile,
    Other,
}

/// <summary>Whether two occurrences of a unit can be compared by value (<see cref="GpoSettingUnits.SameValue"/>).</summary>
public enum GpoUnitComparability
{
    Comparable,

    /// <summary>A setting of the unit is masked (<c>cpassword</c>, <c>DefaultPassword</c>, SNMP communities, hidden value names).</summary>
    Masked,

    /// <summary>Item-level targeting on the preference item or on one of its collections.</summary>
    Conditional,

    /// <summary>Scripts: every GPO's scripts run.</summary>
    Cumulative,

    Unreadable,

    /// <summary>The preference identity fell back to the item id.</summary>
    Ambiguous,

    /// <summary>A key longer than the key cap (<see cref="GpoSettingKey.IsTruncated"/>).</summary>
    Truncated,
}

/// <summary>Why a unit cannot be copied or removed by K7 (null on <see cref="GpoSettingUnit.Refusal"/>: it can).</summary>
public enum GpoUnitRefusal
{
    /// <summary>Masked values and <c>cpassword</c> items (MS14-025).</summary>
    Secret,

    /// <summary>Scripts, script configuration and script files.</summary>
    Script,

    /// <summary><c>[Registry Keys]</c> / <c>[File Security]</c> (no writer).</summary>
    ObjectSecurity,

    /// <summary>Audit options and the global SACL rows of audit.csv (no writer).</summary>
    AuditOption,

    Comment,
    Unreadable,
    Truncated,

    /// <summary><see cref="GpoSettingUnitKind.Other"/> and <see cref="GpoUnitComparability.Ambiguous"/> preference items.</summary>
    Unsupported,

    /// <summary>A preference item below a <c>Collection</c> with <c>Filters</c> or <c>disabled="1"</c>.</summary>
    ScopedCollection,
}

/// <summary>
/// A setting unit: what GPMC shows as one row and what a person means by "the same setting in another GPO"
/// (docs/dev/k-computers-and-gpo.md 21.2). Built by <see cref="GpoSettingUnits.Of(IReadOnlyList{GpoSetting}, RegistryPolicyDocument?, RegistryPolicyDocument?, GpoUnitContext)"/>;
/// in memory only (CLAUDE.md rule 1). <see cref="Id"/>, <see cref="BaseId"/> and the settings hold directory values, so
/// <see cref="ToString"/> prints the kind and scope only.
/// </summary>
public sealed record GpoSettingUnit
{
    /// <summary>Unique in its GPO: <see cref="BaseId"/> for the first occurrence, <c>BaseId#n</c> for the n-th (file order).</summary>
    public required string Id { get; init; }

    /// <summary>The same setting in every GPO (21.2 grammar); compare with <see cref="StringComparer.OrdinalIgnoreCase"/>.</summary>
    public required string BaseId { get; init; }

    public required GpoSettingUnitKind Kind { get; init; }

    /// <summary>Computer or User (<see cref="GpoScope.Gpo"/> for <c>GPO.cmt</c>).</summary>
    public required GpoScope Scope { get; init; }

    public GpoSettingArea Area { get; init; }

    /// <summary>1 for the first occurrence of <see cref="BaseId"/> in the GPO, n for the n-th.</summary>
    public int Occurrence { get; init; } = 1;

    /// <summary>The relative file path the unit comes from.</summary>
    public string? Source { get; init; }

    /// <summary><see cref="GpoSettingUnitKind.AdministrativeTemplate"/>: the policy id (<c>ns:name</c>).</summary>
    public string? PolicyId { get; init; }

    public PolicyClass? PolicyClass { get; init; }

    /// <summary><see cref="GpoSettingUnitKind.AdministrativeTemplate"/>: the policy's state and values.</summary>
    public PolicySetting? Policy { get; init; }

    /// <summary><see cref="GpoSettingUnitKind.Preference"/>: the preference type (<c>Drives</c>, <c>Groups</c> …).</summary>
    public string? PreferenceType { get; init; }

    /// <summary><see cref="GpoSettingUnitKind.Preference"/>: <c>uid</c> or <c>&lt;Element&gt;#n</c> in this GPO's file.</summary>
    public string? ItemId { get; init; }

    /// <summary>The normalized settings of the unit (keys of this GPO).</summary>
    public IReadOnlyList<GpoSetting> Settings { get; init; } = [];

    /// <summary>The canonical value (21.2 Value); policies compare by <see cref="Policy"/> instead.</summary>
    public string ValueFingerprint { get; init; } = string.Empty;

    /// <summary>Preference items: the canonical <c>Filters</c> of the item and its collections (empty without targeting).</summary>
    public string FiltersFingerprint { get; init; } = string.Empty;

    public GpoUnitComparability Comparability { get; init; }

    /// <summary>
    /// A preference item with <c>disabled="1"</c>, or below a disabled collection: it does not apply (never a duplicate,
    /// never a search group's value), but it keeps its GPO from being "empty".
    /// </summary>
    public bool Disabled { get; init; }

    /// <summary>A preference item with <c>removePolicy="1"</c>: clients undo it when it disappears from the GPO.</summary>
    public bool RemovePolicy { get; init; }

    /// <summary>Null: copyable and removable.</summary>
    public GpoUnitRefusal? Refusal { get; init; }

    /// <summary>Principals as written (user rights, restricted groups, GPP groups, item-level targeting).</summary>
    public IReadOnlyList<string> Principals { get; init; } = [];

    /// <summary>The principals normalized to SIDs (upper case) where known (<see cref="GpoUnitContext.SidsByName"/> included).</summary>
    public IReadOnlyList<string> PrincipalSids { get; init; } = [];

    /// <summary>Counts as a setting (not a comment, unreadable-file marker or script payload).</summary>
    public bool CountsAsSetting => Kind is not (GpoSettingUnitKind.Comment or GpoSettingUnitKind.UnreadableFile or GpoSettingUnitKind.ScriptFile);

    /// <summary>Keeps its GPO from being merged: a refused setting, or an unreadable file.</summary>
    public bool BlocksMerge => (CountsAsSetting && Refusal is not null) || Kind == GpoSettingUnitKind.UnreadableFile;

    /// <summary>For diagnostics: kind and scope only (the ids and settings hold directory values).</summary>
    public override string ToString() => Kind + " " + Scope;
}

/// <summary>What <see cref="GpoSettingUnits.Of(IReadOnlyList{GpoSetting}, RegistryPolicyDocument?, RegistryPolicyDocument?, GpoUnitContext)"/> needs besides the settings.</summary>
/// <param name="Definitions">The ADMX set: with it, Registry.pol values a configured policy owns become one policy unit.</param>
/// <param name="DomainSid">The domain SID (domain RIDs of well-known groups).</param>
public sealed record GpoUnitContext(PolicyDefinitionSet? Definitions, string? DomainSid)
{
    public static GpoUnitContext None { get; } = new(null, null);

    /// <summary>
    /// Names resolved to SIDs by <see cref="GpoPrincipalResolver.ResolveAsync"/> (keys compared case-insensitively), so that
    /// a name and a SID of the same account compare equal (restricted-group ids, fingerprints, <see cref="GpoSettingUnit.PrincipalSids"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string>? SidsByName { get; init; }

    /// <summary>For diagnostics: whether a set and a map are present.</summary>
    public override string ToString() => $"definitions: {Definitions is not null}, names: {SidsByName?.Count ?? 0}";
}
