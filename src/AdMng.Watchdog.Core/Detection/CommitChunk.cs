using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The pending writes of one chunk of a cycle (one result page, the deletion pass, the settler …), committed with
/// <c>NewState = null</c> (section 10.4: a chunk never writes the DC identity or W).
/// </summary>
internal sealed class CommitChunk
{
    private readonly Dictionary<Guid, SnapshotObject> _upserts = [];
    private readonly Dictionary<Guid, IReadOnlyList<MemberReference>> _memberships = [];
    private readonly List<(Guid ObjectGuid, DateTimeOffset DeletedAt)> _deletions = [];
    private readonly HashSet<Guid> _removals = [];
    private readonly List<WatchEvent> _events = [];

    public bool MembershipRelevant { get; set; }

    public bool IsEmpty => _upserts.Count == 0 && _memberships.Count == 0 && _deletions.Count == 0 && _removals.Count == 0 && _events.Count == 0;

    public int EventCount => _events.Count;

    /// <summary>The events added to this chunk so far.</summary>
    public IReadOnlyList<WatchEvent> Events => _events;

    /// <summary>Pending upsert of an object (latest wins).</summary>
    public SnapshotObject? PendingUpsert(Guid guid) => _upserts.TryGetValue(guid, out var snapshot) ? snapshot : null;

    /// <summary>Pending member list of a group.</summary>
    public IReadOnlyList<MemberReference>? PendingMembers(Guid group) => _memberships.TryGetValue(group, out var members) ? members : null;

    public void Upsert(SnapshotObject snapshot) => _upserts[snapshot.ObjectGuid] = snapshot;

    public void ReplaceMembers(Guid group, IReadOnlyList<MemberReference> members) => _memberships[group] = members;

    public void Delete(Guid guid, DateTimeOffset deletedAt)
    {
        _upserts.Remove(guid);
        _deletions.Add((guid, deletedAt));
    }

    /// <summary>
    /// Upserts <paramref name="marker"/> (IsDeleted) and adds its Deletions entry: the row exists afterwards even when the
    /// GUID had none (an unknown tombstone), and the key leaves every member list.
    /// </summary>
    public void UpsertDeleted(SnapshotObject marker)
    {
        _upserts[marker.ObjectGuid] = marker;
        _deletions.Add((marker.ObjectGuid, marker.DeletedAt ?? DateTimeOffset.MinValue));
    }

    public void Remove(Guid guid)
    {
        _upserts.Remove(guid);
        _memberships.Remove(guid);
        _removals.Add(guid);
    }

    public void Add(WatchEvent watchEvent) => _events.Add(watchEvent);

    /// <summary>The commit of this chunk (DC state unchanged).</summary>
    public DetectionCommit ToCommit(IReadOnlyList<DnRewrite> rewrites) => new()
    {
        Upserts = [.. _upserts.Values],
        Memberships = new Dictionary<Guid, IReadOnlyList<MemberReference>>(_memberships),
        Deletions = [.. _deletions],
        Removals = [.. _removals],
        DnRewrites = rewrites,
        Events = [.. _events],
        MembershipRelevant = MembershipRelevant,
    };
}
