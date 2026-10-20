using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Records in a group's snapshot that its member list is <b>not</b> stored (section 10.8,
/// <c>MembershipTracking.PrivilegedAndWatchListOnly</c>: the group was neither privileged, on the watch list nor a member
/// of a stored list when it was last read), under the reserved key <see cref="Key"/>. An empty stored list and "never
/// stored" look the same in the member table; the marker tells them apart, so the first tracked read of a group stores
/// its list silently instead of reporting every existing member as added, and a group that stops being tracked drops
/// its list instead of keeping a stale one the PrivilegeEvaluator would trust. No marker = the list is stored.
/// </summary>
internal static class MemberListState
{
    /// <summary>Reserved snapshot attribute key (not an LDAP name: LDAP names never start with '$').</summary>
    public const string Key = "$members";

    private const string Untracked = "untracked";

    private static readonly SnapshotAttribute UntrackedValue = new() { Values = [Untracked], Kind = AttributeValueKind.Text };

    /// <summary>True when the group's member list was not stored when <paramref name="group"/> was built.</summary>
    public static bool IsUntracked(SnapshotObject group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Attributes.TryGetValue(Key, out var state) && state.Values.Contains(Untracked, StringComparer.Ordinal);
    }

    /// <summary><paramref name="group"/> with the marker set (not tracked) or removed (tracked).</summary>
    public static SnapshotObject With(SnapshotObject group, bool tracked)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (IsUntracked(group) == !tracked)
        {
            return group;
        }

        var attributes = new Dictionary<string, SnapshotAttribute>(group.Attributes, StringComparer.OrdinalIgnoreCase);
        if (tracked)
        {
            attributes.Remove(Key);
        }
        else
        {
            attributes[Key] = UntrackedValue;
        }

        return group with { Attributes = attributes };
    }
}
