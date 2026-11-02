using System.Globalization;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>Where a policy applies (<c>class</c> of an ADMX policy).</summary>
public enum PolicyClass
{
    /// <summary>Computer configuration (<c>Machine/Registry.pol</c>, HKLM).</summary>
    Machine,

    /// <summary>User configuration (<c>User/Registry.pol</c>, HKCU).</summary>
    User,

    /// <summary>Both: the policy is shown in both trees and kept separately in each Registry.pol.</summary>
    Both,
}

/// <summary>What a registry value of an ADMX definition is (<c>decimal</c>, <c>longDecimal</c>, <c>string</c>, <c>delete</c>).</summary>
public enum PolicyValueKind
{
    /// <summary><c>&lt;decimal value="n"/&gt;</c>: REG_DWORD.</summary>
    Decimal,

    /// <summary><c>&lt;longDecimal value="n"/&gt;</c>: REG_QWORD.</summary>
    LongDecimal,

    /// <summary><c>&lt;string&gt;text&lt;/string&gt;</c>: REG_SZ.</summary>
    String,

    /// <summary><c>&lt;delete/&gt;</c>: the value is deleted (<c>**del.</c> entry).</summary>
    Delete,
}

/// <summary>A registry value of an ADMX definition (<c>enabledValue</c>, <c>trueValue</c>, an <c>enum</c> item's value …).</summary>
/// <param name="Kind">What the value is.</param>
/// <param name="Number">The number of a decimal or long decimal value.</param>
/// <param name="Text">The text of a string value (null for the other kinds).</param>
public sealed record PolicyValue(PolicyValueKind Kind, ulong Number = 0, string? Text = null)
{
    /// <summary>REG_DWORD 1: what a policy with a value name and no <c>enabledValue</c> writes when enabled.</summary>
    public static PolicyValue One { get; } = new(PolicyValueKind.Decimal, 1);

    /// <summary>REG_DWORD 0: what a check box with no <c>falseValue</c> writes when cleared.</summary>
    public static PolicyValue Zero { get; } = new(PolicyValueKind.Decimal, 0);

    /// <summary>The value is deleted.</summary>
    public static PolicyValue Delete { get; } = new(PolicyValueKind.Delete);

    public static PolicyValue Decimal(uint value) => new(PolicyValueKind.Decimal, value);

    public static PolicyValue LongDecimal(ulong value) => new(PolicyValueKind.LongDecimal, value);

    public static PolicyValue String(string value) => new(PolicyValueKind.String, 0, value ?? string.Empty);

    public override string ToString() => Kind switch
    {
        PolicyValueKind.Decimal or PolicyValueKind.LongDecimal => Number.ToString(CultureInfo.InvariantCulture),
        PolicyValueKind.String => "\"" + Text + "\"",
        _ => "delete",
    };
}

/// <summary>One item of a value list (<c>enabledList</c>, <c>trueList</c>, an <c>enum</c> item's <c>valueList</c>).</summary>
/// <param name="Key">The item's key; null: the list's <c>defaultKey</c>, else the key of the policy (or element).</param>
/// <param name="ValueName">The value name.</param>
/// <param name="Value">The value written.</param>
public sealed record PolicyValueItem(string? Key, string ValueName, PolicyValue Value);

/// <summary>A value list: values written together (<c>enabledList</c>, <c>disabledList</c>, <c>trueList</c> …).</summary>
/// <param name="DefaultKey">The key of items without their own (null: the key of the policy or element).</param>
/// <param name="Items">The values.</param>
public sealed record PolicyValueList(string? DefaultKey, IReadOnlyList<PolicyValueItem> Items)
{
    /// <summary>The key an item is written to.</summary>
    public string KeyOf(PolicyValueItem item, string ownerKey)
    {
        ArgumentNullException.ThrowIfNull(item);
        return !string.IsNullOrEmpty(item.Key) ? item.Key : !string.IsNullOrEmpty(DefaultKey) ? DefaultKey : ownerKey;
    }
}

/// <summary>An element of a policy (an option of the policy's dialog).</summary>
public abstract record PolicyElement
{
    /// <summary>The element's id, unique within its policy (the presentation's <c>refId</c>).</summary>
    public required string Id { get; init; }

    /// <summary>The element's key; null: the key of the policy.</summary>
    public string? Key { get; init; }

    /// <summary>The value name (null only for <see cref="ListElement"/>).</summary>
    public string? ValueName { get; init; }

    /// <summary>A client-side extension that processes the element (added to the GPO's extension names when written).</summary>
    public Guid? ClientExtension { get; init; }

    /// <summary>The key the element writes to.</summary>
    public string KeyIn(AdmxPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return string.IsNullOrEmpty(Key) ? policy.Key : Key;
    }
}

/// <summary>A check box (<c>boolean</c>): true/false values or lists; without them REG_DWORD 1 / 0.</summary>
public sealed record BooleanElement : PolicyElement
{
    public PolicyValue? TrueValue { get; init; }

    public PolicyValue? FalseValue { get; init; }

    public PolicyValueList? TrueList { get; init; }

    public PolicyValueList? FalseList { get; init; }
}

/// <summary>A number (<c>decimal</c>: REG_DWORD; <c>longDecimal</c>: REG_QWORD; <c>storeAsText</c>: REG_SZ).</summary>
public sealed record DecimalElement : PolicyElement
{
    /// <summary>Default largest value of a decimal element (the ADMX schema's default).</summary>
    public const ulong DefaultMaxValue = 9999;

    /// <summary>A <c>longDecimal</c> element (REG_QWORD).</summary>
    public bool IsLong { get; init; }

    public bool Required { get; init; }

    public ulong MinValue { get; init; }

    public ulong MaxValue { get; init; } = DefaultMaxValue;

    /// <summary>The number is written as text (REG_SZ).</summary>
    public bool StoreAsText { get; init; }

    /// <summary>Written as <c>**soft.</c>: the client keeps an existing value.</summary>
    public bool Soft { get; init; }
}

/// <summary>A text (<c>text</c>: REG_SZ, <c>expandable</c>: REG_EXPAND_SZ).</summary>
public sealed record TextElement : PolicyElement
{
    /// <summary>Default longest text (the ADMX schema's default).</summary>
    public const int DefaultMaxLength = 1023;

    public bool Required { get; init; }

    public int MaxLength { get; init; } = DefaultMaxLength;

    public bool Expandable { get; init; }

    public bool Soft { get; init; }
}

/// <summary>Several lines of text (<c>multiText</c>: REG_MULTI_SZ).</summary>
public sealed record MultiTextElement : PolicyElement
{
    public bool Required { get; init; }

    /// <summary>Longest line.</summary>
    public int MaxLength { get; init; } = TextElement.DefaultMaxLength;

    /// <summary>Most lines (0: no limit).</summary>
    public int MaxStrings { get; init; }

    public bool Soft { get; init; }
}

/// <summary>An item of an <see cref="EnumElement"/>.</summary>
/// <param name="DisplayName">The item's text (a <c>$(string.…)</c> reference until resolved).</param>
/// <param name="Value">The value written for the item.</param>
/// <param name="ValueList">More values written with it.</param>
public sealed record EnumItem(string DisplayName, PolicyValue Value, PolicyValueList? ValueList);

/// <summary>A choice of items (<c>enum</c>, shown as a drop-down list).</summary>
public sealed record EnumElement : PolicyElement
{
    public bool Required { get; init; }

    public bool Soft { get; init; }

    public required IReadOnlyList<EnumItem> Items { get; init; }
}

/// <summary>
/// A list of values below a key (<c>list</c>): with <see cref="ExplicitValue"/> the operator names each value; with a
/// <see cref="ValuePrefix"/> (even empty) the values are named prefix + 1, 2, 3 …; otherwise each value is named as its data.
/// Unless <see cref="Additive"/>, the key's values are deleted first (<c>**delvals.</c>).
/// </summary>
public sealed record ListElement : PolicyElement
{
    /// <summary>The value name prefix; null: no prefix attribute (names = data).</summary>
    public string? ValuePrefix { get; init; }

    public bool Additive { get; init; }

    public bool Expandable { get; init; }

    public bool ExplicitValue { get; init; }
}

/// <summary>A policy of an ADMX file (texts still as <c>$(string.…)</c> references; <see cref="PolicyDefinitionSet"/> resolves them).</summary>
public sealed record AdmxPolicy
{
    /// <summary>The policy's name, unique within its namespace.</summary>
    public required string Name { get; init; }

    /// <summary>The target namespace of the ADMX file that defines it.</summary>
    public required string Namespace { get; init; }

    public required PolicyClass Class { get; init; }

    public required string DisplayName { get; init; }

    public string? ExplainText { get; init; }

    /// <summary>The presentation id (from <c>$(presentation.id)</c>); null: the policy has no options.</summary>
    public string? Presentation { get; init; }

    /// <summary>The registry key below HKLM / HKCU.</summary>
    public required string Key { get; init; }

    /// <summary>The policy's own value (empty: the key's default value); null: the policy is expressed by its lists and elements only.</summary>
    public string? ValueName { get; init; }

    /// <summary>The parent category reference (<c>prefix:name</c> or <c>name</c>).</summary>
    public string? ParentCategory { get; init; }

    /// <summary>The supportedOn reference (<c>prefix:name</c> or <c>name</c>).</summary>
    public string? SupportedOn { get; init; }

    public PolicyValue? EnabledValue { get; init; }

    public PolicyValue? DisabledValue { get; init; }

    public PolicyValueList? EnabledList { get; init; }

    public PolicyValueList? DisabledList { get; init; }

    public IReadOnlyList<PolicyElement> Elements { get; init; } = [];

    /// <summary>A client-side extension that processes the policy (added to the GPO's extension names when written).</summary>
    public Guid? ClientExtension { get; init; }

    /// <summary><c>namespace:name</c>.</summary>
    public string Id => Namespace + ":" + Name;

    /// <summary>The policy can be expressed in Registry.pol (a value, a list or an element); otherwise it is shown read-only.</summary>
    public bool IsWritable => ValueName is not null || EnabledList is { Items.Count: > 0 } || DisabledList is { Items.Count: > 0 } || Elements.Count > 0;

    public bool AppliesTo(PolicyClass scope) => Class == PolicyClass.Both || Class == scope;

    public override string ToString() => Id;
}

/// <summary>A category of an ADMX file.</summary>
/// <param name="Name">The category's name, unique within its namespace.</param>
/// <param name="Namespace">The target namespace of the ADMX file that defines it.</param>
/// <param name="DisplayName">The display name reference.</param>
/// <param name="ExplainText">The explain text reference.</param>
/// <param name="ParentCategory">The parent reference (<c>prefix:name</c> or <c>name</c>); null: a root category.</param>
public sealed record AdmxCategory(string Name, string Namespace, string DisplayName, string? ExplainText, string? ParentCategory)
{
    public string Id => Namespace + ":" + Name;
}

/// <summary>A supportedOn definition (<c>supportedOn/definitions/definition</c>).</summary>
/// <param name="Name">The definition's name, unique within its namespace.</param>
/// <param name="DisplayName">The display name reference.</param>
public sealed record AdmxSupportedOnDefinition(string Name, string DisplayName);

/// <summary>One parsed ADMX file.</summary>
public sealed record AdmxFile
{
    /// <summary>The file name (<c>WindowsUpdate.admx</c>).</summary>
    public required string FileName { get; init; }

    public required string TargetPrefix { get; init; }

    public required string TargetNamespace { get; init; }

    /// <summary>Prefix → namespace of the <c>using</c> entries (the target prefix included).</summary>
    public required IReadOnlyDictionary<string, string> Prefixes { get; init; }

    /// <summary>The <c>revision</c> attribute.</summary>
    public string? Revision { get; init; }

    /// <summary>The ADML revision the file needs (<c>resources minRequiredRevision</c>).</summary>
    public string? MinRequiredRevision { get; init; }

    /// <summary>The culture of the ADML used when the requested one is missing (<c>resources fallbackCulture</c>).</summary>
    public string? FallbackCulture { get; init; }

    public IReadOnlyList<AdmxCategory> Categories { get; init; } = [];

    public IReadOnlyList<AdmxPolicy> Policies { get; init; } = [];

    public IReadOnlyList<AdmxSupportedOnDefinition> SupportedOn { get; init; } = [];

    /// <summary>
    /// The namespace a reference names: <c>prefix:name</c> through the prefixes, a bare name in the file's own namespace;
    /// null for an unknown prefix.
    /// </summary>
    public (string Namespace, string Name)? Resolve(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var colon = reference.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return (TargetNamespace, reference.Trim());
        }

        var prefix = reference[..colon].Trim();
        var name = reference[(colon + 1)..].Trim();
        return name.Length > 0 && Prefixes.TryGetValue(prefix, out var ns) ? (ns, name) : null;
    }

    public override string ToString() => FileName;
}
