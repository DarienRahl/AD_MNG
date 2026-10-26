namespace AdMng.Core.Comparison;

/// <summary>How an object is a member of a group (or how a member belongs to a group).</summary>
public enum MembershipKind
{
    /// <summary>A value of the group's <c>member</c> ("bezpośrednio").</summary>
    Direct,

    /// <summary>The group of <c>primaryGroupID</c> ("grupa podstawowa"; AD does not list it in <c>member</c>).</summary>
    Primary,

    /// <summary>Through other groups only ("zagnieżdżone").</summary>
    Nested,
}

/// <summary>State of one group (or member) in the comparison of two memberships.</summary>
public enum MembershipComparisonState
{
    /// <summary>A member on both sides, the same way ("w obu").</summary>
    InBoth,

    /// <summary>A member on both sides, but differently: direct on one side, nested or primary on the other ("w obu, inaczej").</summary>
    InBothDifferently,

    /// <summary>Only on side A ("tylko A").</summary>
    OnlyA,

    /// <summary>Only on side B ("tylko B").</summary>
    OnlyB,
}

/// <summary>One group of one side (or one member of a group).</summary>
/// <param name="Key">The cross-domain key (<see cref="GroupKey"/>).</param>
/// <param name="Name">The name as that side's DC returns it.</param>
/// <param name="DistinguishedName">The DN on that side.</param>
/// <param name="Kind">Direct, primary or nested.</param>
public sealed record MembershipEntry(string Key, string Name, string DistinguishedName, MembershipKind Kind)
{
    /// <summary>The key and kind only (never a name or DN).</summary>
    public override string ToString() => $"MembershipEntry({Kind})";
}

/// <summary>One row of the "Członkostwo" (or "Członkowie") tab.</summary>
public sealed record MembershipComparison(string Key, MembershipEntry? A, MembershipEntry? B, MembershipComparisonState State)
{
    /// <summary>The row differs - what "Tylko różnice" shows.</summary>
    public bool IsDifference => State != MembershipComparisonState.InBoth;

    /// <summary>The name of side A, else of side B.</summary>
    public string Name => A?.Name ?? B?.Name ?? string.Empty;

    /// <summary>The state only (never a name).</summary>
    public override string ToString() => $"MembershipComparison({State})";
}

/// <summary>Compares two memberships by <see cref="MembershipEntry.Key"/> (design note docs/dev/multi-window.md 8.7).</summary>
public static class MembershipComparer
{
    /// <summary>
    /// One row per key of either side, sorted by name. An entry listed more than once on one side counts once, as its
    /// strongest kind (primary, then direct, then nested). Entries without a key are compared by their DN on their side
    /// only (never matched across sides).
    /// </summary>
    public static IReadOnlyList<MembershipComparison> Compare(IEnumerable<MembershipEntry> a, IEnumerable<MembershipEntry> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var sideA = Strongest(a, "a");
        var sideB = Strongest(b, "b");
        var keys = new HashSet<string>(sideA.Keys, StringComparer.Ordinal);
        keys.UnionWith(sideB.Keys);
        var rows = new List<MembershipComparison>(keys.Count);
        foreach (var key in keys)
        {
            var inA = sideA.GetValueOrDefault(key);
            var inB = sideB.GetValueOrDefault(key);
            var state = (inA, inB) switch
            {
                (not null, null) => MembershipComparisonState.OnlyA,
                (null, not null) => MembershipComparisonState.OnlyB,
                _ => inA!.Kind == inB!.Kind ? MembershipComparisonState.InBoth : MembershipComparisonState.InBothDifferently,
            };
            rows.Add(new MembershipComparison(key, inA, inB, state));
        }

        return [.. rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal)];
    }

    private static Dictionary<string, MembershipEntry> Strongest(IEnumerable<MembershipEntry> entries, string side)
    {
        var result = new Dictionary<string, MembershipEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // Without a key an entry can only be listed (by its DN, on its side) - never matched with the other side.
            var key = entry.Key.Length > 0 ? entry.Key : $"dn:{side}:{entry.DistinguishedName.ToUpperInvariant()}";
            if (!result.TryGetValue(key, out var existing) || Rank(entry.Kind) < Rank(existing.Kind))
            {
                result[key] = entry;
            }
        }

        return result;
    }

    private static int Rank(MembershipKind kind) => kind switch
    {
        MembershipKind.Primary => 0,
        MembershipKind.Direct => 1,
        _ => 2,
    };
}
