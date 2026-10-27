using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Comparison;

/// <summary>State of one attribute in the comparison of object A (source) with object B (target).</summary>
public enum AttributeComparisonState
{
    /// <summary>The same values on both sides ("Takie same").</summary>
    Same,

    /// <summary>Addresses that differ only in their domain part, with "Porównuj adresy bez domeny" ("Takie same (inna domena)").</summary>
    SameOtherDomain,

    /// <summary>Values on both sides, not the same ("Różne").</summary>
    Different,

    /// <summary>Values only on side A ("Tylko w A").</summary>
    OnlyA,

    /// <summary>Values only on side B ("Tylko w B").</summary>
    OnlyB,

    /// <summary>Side A's schema has no such attribute (e.g. an Exchange attribute), side B has values ("Brak w schemacie A").</summary>
    NotInSchemaA,

    /// <summary>Side B's schema has no such attribute, side A has values ("Brak w schemacie B").</summary>
    NotInSchemaB,
}

/// <summary>Where a value of a multi-valued attribute occurs.</summary>
public enum ValueSide
{
    Both,
    OnlyA,
    OnlyB,
}

/// <summary>One value of one side with its mark (a secret is <see cref="SecretAttributes.Mask"/>, never its value).</summary>
public sealed record ComparedValue(object Value, ValueSide Side)
{
    /// <summary>The side only (never the value).</summary>
    public override string ToString() => $"ComparedValue({Side})";
}

/// <summary>Switches of the attribute comparison (remembered by name in the settings, never values).</summary>
public sealed record ComparisonOptions
{
    public static ComparisonOptions Default { get; } = new();

    /// <summary>
    /// "Porównuj adresy bez domeny" (default on): userPrincipalName, mail, proxyAddresses and targetAddress are compared
    /// by the part before <c>@</c> (with the <c>smtp:</c> / <c>SMTP:</c> prefix).
    /// </summary>
    public bool CompareAddressesWithoutDomain { get; init; } = true;
}

/// <summary>One attribute of the comparison (a row of the "Atrybuty" tab).</summary>
public sealed record AttributeComparison
{
    /// <summary>LDAP display name.</summary>
    public required string Name { get; init; }

    public required AttributeComparisonState State { get; init; }

    /// <summary>Values of side A with their marks (empty when A has none).</summary>
    public IReadOnlyList<ComparedValue> ValuesA { get; init; } = [];

    /// <summary>Values of side B with their marks (empty when B has none).</summary>
    public IReadOnlyList<ComparedValue> ValuesB { get; init; } = [];

    /// <summary>A secret by either side's schema: only presence is compared, values are the mask.</summary>
    public bool IsSecret { get; init; }

    /// <summary>A volatile or identity attribute (hidden by default, <see cref="ComparisonAttributes.IsVolatile"/>).</summary>
    public bool IsVolatile { get; init; }

    /// <summary>At least one side returned the attribute only partially (ranged retrieval was not possible).</summary>
    public bool IsTruncated { get; init; }

    /// <summary>A <see cref="AttributeComparisonState.Different"/> whose values differ only in letter case ("tylko wielkość liter").</summary>
    public bool IsCaseOnly { get; init; }

    /// <summary>DN values, compared in their portable form (<c>CN=Jan Kowalski,OU=IT,{domain}</c>).</summary>
    public bool IsDnValued { get; init; }

    /// <summary>Number of values both sides have.</summary>
    public int CommonCount { get; init; }

    /// <summary>Number of values only side A has.</summary>
    public int OnlyACount { get; init; }

    /// <summary>Number of values only side B has.</summary>
    public int OnlyBCount { get; init; }

    /// <summary>The row differs (Różne, Tylko w A/B, Brak w schemacie A/B) - what "Tylko różnice" shows.</summary>
    public bool IsDifference => State is not (AttributeComparisonState.Same or AttributeComparisonState.SameOtherDomain);

    /// <summary>The name and state only (never values).</summary>
    public override string ToString() => $"AttributeComparison({Name}, {State})";
}

/// <summary>
/// The pure attribute diff of two objects read through two connections (design note docs/dev/multi-window.md 8.5):
/// multi-valued attributes as sets, strings ordinal (case-only differences marked), DN values in their portable form,
/// addresses optionally without the domain, times as instants, binary values by bytes, secrets by presence only.
/// </summary>
public static class ObjectDiffer
{
    /// <summary>Compares the attributes present on either side, without those shown on the membership tabs.</summary>
    public static IReadOnlyList<AttributeComparison> Compare(ObjectSnapshot a, ObjectSnapshot b, ComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        options ??= ComparisonOptions.Default;
        var isGroup = a.Kind == ObjectKind.Group || b.Kind == ObjectKind.Group;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(a.Attributes.Keys);
        names.UnionWith(b.Attributes.Keys);
        var rows = new List<AttributeComparison>(names.Count);
        foreach (var name in names)
        {
            if (ComparisonAttributes.IsShownOnMembershipTabs(name, isGroup))
            {
                continue;
            }

            rows.Add(CompareAttribute(name, a, b, options));
        }

        return rows;
    }

    private static AttributeComparison CompareAttribute(string name, ObjectSnapshot a, ObjectSnapshot b, ComparisonOptions options)
    {
        var valuesA = a.Attributes.GetValueOrDefault(name) ?? [];
        var valuesB = b.Attributes.GetValueOrDefault(name) ?? [];
        var isSecret = SecretAttributes.IsSecret(name, a.Schema) || SecretAttributes.IsSecret(name, b.Schema);
        var isDn = ComparisonAttributes.IsDnValued(name, a.Schema, b.Schema);
        var row = new AttributeComparison
        {
            Name = PreferredName(name, a, b),
            State = AttributeComparisonState.Same,
            IsSecret = isSecret,
            IsVolatile = ComparisonAttributes.IsVolatile(name),
            IsTruncated = a.TruncatedAttributes.Contains(name) || b.TruncatedAttributes.Contains(name),
            IsDnValued = isDn && !isSecret,
        };

        if (isSecret)
        {
            // Presence only: a secret value is never compared, shown, copied or exported.
            var hasA = valuesA.Count > 0;
            var hasB = valuesB.Count > 0;
            return row with
            {
                State = OneSided(name, hasA, hasB, a, b),
                ValuesA = hasA ? [new ComparedValue(SecretAttributes.Mask, hasB ? ValueSide.Both : ValueSide.OnlyA)] : [],
                ValuesB = hasB ? [new ComparedValue(SecretAttributes.Mask, hasA ? ValueSide.Both : ValueSide.OnlyB)] : [],
                CommonCount = hasA && hasB ? 1 : 0,
                OnlyACount = hasA && !hasB ? 1 : 0,
                OnlyBCount = hasB && !hasA ? 1 : 0,
            };
        }

        var kind = KindOf(name, isDn, a.Schema, b.Schema, options);
        var keysA = valuesA.Select(v => Key(v, kind, a.DefaultNamingContext)).ToList();
        var keysB = valuesB.Select(v => Key(v, kind, b.DefaultNamingContext)).ToList();

        // Multisets, not sets: without the domain, smtp:jan@contoso.com and smtp:jan@contoso.onmicrosoft.com share a key,
        // and three such addresses against two are a difference (the surplus value is marked "only A").
        var unmatchedB = Counts(keysB);
        var marksA = valuesA.Select((v, i) => new ComparedValue(v, Take(unmatchedB, keysA[i]) ? ValueSide.Both : ValueSide.OnlyA)).ToList();
        var unmatchedA = Counts(keysA);
        var marksB = valuesB.Select((v, i) => new ComparedValue(v, Take(unmatchedA, keysB[i]) ? ValueSide.Both : ValueSide.OnlyB)).ToList();
        var common = marksA.Count(m => m.Side == ValueSide.Both);
        row = row with
        {
            ValuesA = marksA,
            ValuesB = marksB,
            CommonCount = common,
            OnlyACount = valuesA.Count - common,
            OnlyBCount = valuesB.Count - common,
        };

        if (valuesA.Count == 0 || valuesB.Count == 0)
        {
            return row with { State = OneSided(name, valuesA.Count > 0, valuesB.Count > 0, a, b) };
        }

        if (row.OnlyACount == 0 && row.OnlyBCount == 0)
        {
            // Equal keys: for addresses the full values may still differ in their domain part.
            var sameOtherDomain = kind == ValueKind.Address
                && !valuesA.Select(Text).ToHashSet(StringComparer.Ordinal).SetEquals(valuesB.Select(Text));
            return row with { State = sameOtherDomain ? AttributeComparisonState.SameOtherDomain : AttributeComparisonState.Same };
        }

        var caseOnly = kind switch
        {
            ValueKind.Text => SameMultiset(keysA.Select(k => k.ToUpperInvariant()), keysB.Select(k => k.ToUpperInvariant())),

            // smtp: (secondary) and SMTP: (primary) are not a matter of letter case: only the address part is folded.
            ValueKind.Address => SameMultiset(keysA.Select(FoldAddress), keysB.Select(FoldAddress)),
            _ => false,
        };
        return row with { State = AttributeComparisonState.Different, IsCaseOnly = caseOnly };
    }

    private static Dictionary<string, int> Counts(IEnumerable<string> keys)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    /// <summary>Pairs one value of <paramref name="key"/> with an unpaired value of the other side, if one is left.</summary>
    private static bool Take(Dictionary<string, int> unmatched, string key)
    {
        var left = unmatched.GetValueOrDefault(key);
        if (left <= 0)
        {
            return false;
        }

        unmatched[key] = left - 1;
        return true;
    }

    private static bool SameMultiset(IEnumerable<string> a, IEnumerable<string> b)
    {
        var counts = Counts(a);
        foreach (var key in b)
        {
            if (!Take(counts, key))
            {
                return false;
            }
        }

        return counts.Values.All(n => n == 0);
    }

    /// <summary>OnlyA / OnlyB, or "Brak w schemacie" when the side without values has no such attribute in its schema.</summary>
    private static AttributeComparisonState OneSided(string name, bool hasA, bool hasB, ObjectSnapshot a, ObjectSnapshot b)
    {
        if (hasA && hasB)
        {
            return AttributeComparisonState.Same;
        }

        var inSchemaA = a.Schema.FindAttribute(name) is not null;
        var inSchemaB = b.Schema.FindAttribute(name) is not null;
        if (hasA)
        {
            return inSchemaA && !inSchemaB && b.Schema.Attributes.Count > 0 ? AttributeComparisonState.NotInSchemaB : AttributeComparisonState.OnlyA;
        }

        return inSchemaB && !inSchemaA && a.Schema.Attributes.Count > 0 ? AttributeComparisonState.NotInSchemaA : AttributeComparisonState.OnlyB;
    }

    /// <summary>The LDAP name as the schema spells it (A's schema first), else as the DC returned it.</summary>
    private static string PreferredName(string name, ObjectSnapshot a, ObjectSnapshot b) =>
        a.Schema.FindAttribute(name)?.LdapName ?? b.Schema.FindAttribute(name)?.LdapName ?? name;

    private static ValueKind KindOf(string name, bool isDn, ISchemaCatalog schemaA, ISchemaCatalog schemaB, ComparisonOptions options)
    {
        if (isDn)
        {
            return ValueKind.Dn;
        }

        if (options.CompareAddressesWithoutDomain && ComparisonAttributes.IsAddress(name))
        {
            return ValueKind.Address;
        }

        var syntax = schemaA.FindAttribute(name)?.Syntax ?? schemaB.FindAttribute(name)?.Syntax ?? AttributeSyntax.Unknown;
        if (syntax is AttributeSyntax.GeneralizedTime or AttributeSyntax.UtcTime)
        {
            return ValueKind.GeneralizedTime;
        }

        if (Attr.FileTimeAttributes.Contains(name))
        {
            return ValueKind.FileTime;
        }

        return syntax is AttributeSyntax.Integer or AttributeSyntax.LargeInteger or AttributeSyntax.Enumeration
            ? ValueKind.Number
            : ValueKind.Text;
    }

    /// <summary>The comparison key of a value: equal keys mean equal values for the attribute's kind.</summary>
    private static string Key(object value, ValueKind kind, string namingContext)
    {
        if (value is byte[] bytes)
        {
            return "0x" + Convert.ToHexString(bytes);
        }

        var text = (string)value;
        switch (kind)
        {
            case ValueKind.Dn:
                return DnKey(text, namingContext);
            case ValueKind.Address:
                return AddressKey(text);
            case ValueKind.GeneralizedTime:
                return AdConvert.FromGeneralizedTime(text) is { } instant
                    ? "t:" + instant.UtcTicks.ToString(CultureInfo.InvariantCulture)
                    : text;
            case ValueKind.FileTime:
                return AdConvert.ParseInt64(text) is { } fileTime
                    ? fileTime is <= 0 or AdConvert.FileTimeNever ? "t:never" : "t:" + fileTime.ToString(CultureInfo.InvariantCulture)
                    : text;
            case ValueKind.Number:
                return AdConvert.ParseInt64(text) is { } number ? number.ToString(CultureInfo.InvariantCulture) : text;
            default:
                return text;
        }
    }

    /// <summary>
    /// DN, DN-Binary (<c>B:&lt;n&gt;:&lt;hex&gt;:&lt;dn&gt;</c>) or DN-String (<c>S:&lt;n&gt;:&lt;text&gt;:&lt;dn&gt;</c>): the prefix is kept,
    /// the DN part is normalized to its portable form (case-insensitive like DN equality).
    /// </summary>
    private static string DnKey(string value, string namingContext)
    {
        var (prefix, dn) = SplitDnWithPrefix(value);
        var normalized = DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString() : dn;
        var portable = string.IsNullOrEmpty(namingContext) ? normalized : DistinguishedName.ToPortable(normalized, namingContext);
        return prefix + portable.ToUpperInvariant();
    }

    /// <summary>Splits the B:/S: prefix of a DN-Binary or DN-String value; plain DNs have an empty prefix.</summary>
    internal static (string Prefix, string Dn) SplitDnWithPrefix(string value)
    {
        if (value.Length > 4 && value[0] is 'B' or 'S' or 'b' or 's' && value[1] == ':')
        {
            var countEnd = value.IndexOf(':', 2);
            if (countEnd > 2 && int.TryParse(value.AsSpan(2, countEnd - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                && countEnd + 1 + count < value.Length && value[countEnd + 1 + count] == ':')
            {
                var split = countEnd + 1 + count;
                return (value[..(split + 1)], value[(split + 1)..]);
            }
        }

        return (string.Empty, value);
    }

    /// <summary>The address without its domain: "smtp:" / "SMTP:" prefix (kept) and the part before the last <c>@</c>.</summary>
    private static string AddressKey(string value)
    {
        var at = value.LastIndexOf('@');
        return at > 0 ? value[..at] : value;
    }

    private static string Text(object value) => value as string ?? Encoding.UTF8.GetString((byte[])value);

    /// <summary>An address key with its letter case folded, except the <c>smtp:</c> / <c>SMTP:</c> prefix (primary or not).</summary>
    private static string FoldAddress(string key) => key.Length > 5 && key[4] == ':' && key.StartsWith("smtp", StringComparison.OrdinalIgnoreCase)
        ? key[..5] + key[5..].ToUpperInvariant()
        : key.ToUpperInvariant();

    private enum ValueKind
    {
        Text,
        Number,
        Dn,
        Address,
        GeneralizedTime,
        FileTime,
    }
}
