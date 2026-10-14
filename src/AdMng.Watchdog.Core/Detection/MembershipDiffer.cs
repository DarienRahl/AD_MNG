using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Old vs new member list of one group → GroupMemberAdded / GroupMemberRemoved drafts (section 10.8), compared by
/// member key (a renamed member is no change). Members that were deleted (or vanished) in the same cycle produce no
/// removal – the ObjectDeleted event covers them. The member reference comes from the snapshot when known, else from
/// the DN.
/// </summary>
internal sealed class MembershipDiffer
{
    /// <summary>The member drafts of one group.</summary>
    /// <param name="before">Stored members.</param>
    /// <param name="after">Members read now.</param>
    /// <param name="knownMembers">Snapshots of the members by GUID (display data).</param>
    /// <param name="isGone">True for a removed member key that no longer exists live (deleted / disappeared).</param>
    public IReadOnlyList<EventDraft> Diff(
        IReadOnlyList<MemberReference> before,
        IReadOnlyList<MemberReference> after,
        IReadOnlyDictionary<Guid, SnapshotObject> knownMembers,
        Func<MemberReference, bool> isGone)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(knownMembers);
        ArgumentNullException.ThrowIfNull(isGone);
        var oldKeys = before.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newKeys = after.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var drafts = new List<EventDraft>();
        foreach (var added in after.Where(m => !oldKeys.Contains(m.Key)))
        {
            drafts.Add(Draft(WatchEventKind.GroupMemberAdded, added, knownMembers));
        }

        foreach (var removed in before.Where(m => !newKeys.Contains(m.Key)))
        {
            if (!isGone(removed))
            {
                drafts.Add(Draft(WatchEventKind.GroupMemberRemoved, removed, knownMembers));
            }
        }

        return drafts;
    }

    /// <summary>A reference to a member: from its snapshot when known, else from the DN of the member value.</summary>
    public static ObjectRef ToRef(MemberReference member, IReadOnlyDictionary<Guid, SnapshotObject> knownMembers)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.ObjectGuid is { } guid && knownMembers.TryGetValue(guid, out var snapshot) && !snapshot.IsDeleted)
        {
            return snapshot.ToRef();
        }

        var name = DistinguishedName.TryParse(member.Dn, out var dn) && !dn.IsRoot ? dn.Name : null;
        return new ObjectRef { ObjectGuid = member.ObjectGuid, Sid = member.Sid, Dn = member.Dn, Name = name };
    }

    private static EventDraft Draft(WatchEventKind kind, MemberReference member, IReadOnlyDictionary<Guid, SnapshotObject> knownMembers) => new()
    {
        Kind = kind,
        Member = ToRef(member, knownMembers),
        MetadataAttributes = [Attr.Member],
    };
}
