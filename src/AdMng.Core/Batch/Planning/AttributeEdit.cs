using AdMng.Core.Ldap;
using AdMng.Core.Schema;

namespace AdMng.Core.Batch.Planning;

/// <summary>
/// Working state of one attribute of one object while the operations of a batch are applied in order.
/// Converted to at most one <see cref="AttributeChange"/> by <see cref="ToChange"/>.
/// </summary>
internal sealed class AttributeEdit
{
    private readonly IEqualityComparer<string> _matchComparer;
    private readonly IEqualityComparer<string> _identityComparer;
    private readonly List<string> _current;
    private readonly List<string> _added = [];
    private readonly List<string> _removed = [];
    private EditMode _mode;

    public AttributeEdit(string attribute, IReadOnlyList<string> original, AttributeDefinition? definition)
    {
        Attribute = attribute;
        Original = original;
        _current = [.. original];
        _matchComparer = ValueComparers.ForMatching(definition);
        _identityComparer = ValueComparers.ForIdentity(definition);
    }

    private enum EditMode
    {
        None,
        Replace,
        AddValues,
        RemoveValues,
    }

    /// <summary>LDAP display name (schema spelling when known).</summary>
    public string Attribute { get; }

    /// <summary>Values read from the directory while planning.</summary>
    public IReadOnlyList<string> Original { get; }

    /// <summary>Values after the operations applied so far.</summary>
    public IReadOnlyList<string> Current => _current;

    /// <summary>Write the value even when it equals the original (e.g. pwdLastSet=0 after a password reset).</summary>
    public bool ForceWrite { get; set; }

    /// <summary>Replaces all values (empty = clear). Duplicates (per the attribute's matching rule) are dropped.</summary>
    public void Replace(IEnumerable<string> values)
    {
        var distinct = Distinct(values);
        _current.Clear();
        _current.AddRange(distinct);
        _mode = EditMode.Replace;
    }

    /// <summary>Adds the values that are not present yet.</summary>
    public void Add(IEnumerable<string> values)
    {
        var toAdd = Distinct(values).Where(v => !_current.Contains(v, _matchComparer)).ToList();
        _current.AddRange(toAdd);
        _added.AddRange(toAdd);
        _mode = _mode switch
        {
            EditMode.None or EditMode.AddValues => EditMode.AddValues,
            _ => EditMode.Replace,
        };
    }

    /// <summary>Removes the present values matching any of <paramref name="values"/>.</summary>
    public void Remove(IEnumerable<string> values)
    {
        var requested = values.ToList();
        var matched = _current.Where(c => requested.Contains(c, _matchComparer)).ToList();
        _current.RemoveAll(c => matched.Contains(c, StringComparer.Ordinal));
        _removed.AddRange(matched);
        _mode = _mode switch
        {
            EditMode.None or EditMode.RemoveValues => EditMode.RemoveValues,
            _ => EditMode.Replace,
        };
    }

    /// <summary>The resulting change, or null when nothing changes.</summary>
    public AttributeChange? ToChange()
    {
        switch (_mode)
        {
            case EditMode.AddValues:
                return _added.Count == 0 ? null : new AttributeChange(Attribute, AttributeChangeKind.AddValues, Original, [.. _added]);
            case EditMode.RemoveValues:
                return _removed.Count == 0 ? null : new AttributeChange(Attribute, AttributeChangeKind.RemoveValues, Original, [.. _removed]);
            case EditMode.Replace:
                if (!ForceWrite && SameValues(Original, _current))
                {
                    return null;
                }

                return _current.Count == 0
                    ? new AttributeChange(Attribute, AttributeChangeKind.Clear, Original, [])
                    : new AttributeChange(Attribute, AttributeChangeKind.Replace, Original, [.. _current]);
            default:
                return null;
        }
    }

    private List<string> Distinct(IEnumerable<string> values)
    {
        var result = new List<string>();
        foreach (var value in values)
        {
            if (!result.Contains(value, _matchComparer))
            {
                result.Add(value);
            }
        }

        return result;
    }

    /// <summary>Whole-attribute comparison (order-insensitive, case-sensitive for text, DN-aware for DNs).</summary>
    private bool SameValues(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var remaining = b.ToList();
        foreach (var value in a)
        {
            var index = remaining.FindIndex(r => _identityComparer.Equals(r, value));
            if (index < 0)
            {
                return false;
            }

            remaining.RemoveAt(index);
        }

        return true;
    }
}

/// <summary>Value comparers per attribute syntax.</summary>
internal static class ValueComparers
{
    /// <summary>
    /// How the directory matches values (used for "already present" / "remove this value"): DN-aware for DN syntax,
    /// ordinal for case-exact strings, case-insensitive otherwise (AD's default matching rule).
    /// </summary>
    public static IEqualityComparer<string> ForMatching(AttributeDefinition? definition) => definition?.Syntax switch
    {
        AttributeSyntax.DistinguishedName => DnStringComparer.Instance,
        AttributeSyntax.CaseExactString => StringComparer.Ordinal,
        _ => StringComparer.OrdinalIgnoreCase,
    };

    /// <summary>
    /// Whether a replace is a no-op: case-sensitive (a case-only change is a real change the operator asked for),
    /// DN-aware for DN syntax.
    /// </summary>
    public static IEqualityComparer<string> ForIdentity(AttributeDefinition? definition) =>
        definition?.Syntax == AttributeSyntax.DistinguishedName ? DnStringComparer.Instance : StringComparer.Ordinal;
}

/// <summary>Compares DN strings with <see cref="DistinguishedName.AreEqual"/> semantics.</summary>
internal sealed class DnStringComparer : IEqualityComparer<string>
{
    public static DnStringComparer Instance { get; } = new();

    public bool Equals(string? x, string? y) => DistinguishedName.AreEqual(x, y);

    public int GetHashCode(string obj) =>
        DistinguishedName.TryParse(obj, out var dn) ? dn.GetHashCode() : StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
}
