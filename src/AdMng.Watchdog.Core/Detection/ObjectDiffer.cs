using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Snapshot vs snapshot → <see cref="RawObjectDiff"/> (section 10.6). Only the currently watched attributes are
/// compared; an attribute that was not watched when the old snapshot was built is adopted silently
/// (<see cref="WatchSignature"/>). Security descriptors compare their owner/ACE summary sets – a different hash with an
/// identical summary is no change (9.4).
/// </summary>
internal sealed class ObjectDiffer
{
    private static readonly HashSet<string> DescriptorAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.NtSecurityDescriptor, WatchdogAttributes.AllowedToActOnBehalfOfOtherIdentity, WatchdogAttributes.GroupMsaMembership,
    };

    /// <summary>Diffs <paramref name="old"/> (whose DN is <paramref name="oldDn"/> after rewrites) and <paramref name="current"/>.</summary>
    public RawObjectDiff Diff(SnapshotObject old, string oldDn, SnapshotObject current, IReadOnlySet<string> watched)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(watched);
        var (moved, renamed) = CompareDns(oldDn, current.Dn);
        var deltas = new List<AttributeDelta>();
        foreach (var name in watched.Order(StringComparer.OrdinalIgnoreCase))
        {
            old.Attributes.TryGetValue(name, out var before);
            current.Attributes.TryGetValue(name, out var after);
            if (before is null && after is null)
            {
                continue;
            }

            if (before is null && !WatchSignature.WasWatched(old, name))
            {
                continue;
            }

            if (!AreEqual(name, before, after))
            {
                deltas.Add(new AttributeDelta(name, before, after));
            }
        }

        return new RawObjectDiff { Old = old, New = current, OldDn = oldDn, Moved = moved, Renamed = renamed, Attributes = deltas };
    }

    /// <summary>(parent changed, RDN changed) between two DNs.</summary>
    public static (bool Moved, bool Renamed) CompareDns(string oldDn, string newDn)
    {
        if (!DistinguishedName.TryParse(oldDn, out var before) || !DistinguishedName.TryParse(newDn, out var after) || before.IsRoot || after.IsRoot)
        {
            return (false, false);
        }

        var moved = before.Parent != after.Parent;
        var renamed = !string.Equals(before.Leaf.Value, after.Leaf.Value, StringComparison.Ordinal)
            || !string.Equals(before.Leaf.Type, after.Leaf.Type, StringComparison.OrdinalIgnoreCase);
        return (moved, renamed);
    }

    private static bool AreEqual(string name, SnapshotAttribute? before, SnapshotAttribute? after)
    {
        if (before is null || after is null)
        {
            return false;
        }

        if (DescriptorAttributes.Contains(name))
        {
            return SecurityDescriptorSummary.HashOf(before.Values) == SecurityDescriptorSummary.HashOf(after.Values)
                || SecurityDescriptorSummary.ComparisonSet(before.Values).SetEquals(SecurityDescriptorSummary.ComparisonSet(after.Values));
        }

        return before.Values.SequenceEqual(after.Values, StringComparer.Ordinal);
    }
}
