using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Snapshots, member lists, privilege state and DC watermarks (section 9).</summary>
public interface IDetectionStore
{
    Task<DcState?> GetDcStateAsync(string domain, CancellationToken cancellationToken);
    /// <summary>Last watermark processed on that DC identity, or null when this (dsServiceName, invocationId) was never polled.</summary>
    Task<DcWatermark?> GetWatermarkAsync(string dsServiceName, Guid invocationId, CancellationToken cancellationToken);
    /// <summary>Live snapshots whose CreationPendingUntil ≤ dueBefore (settle window over).</summary>
    Task<IReadOnlyList<SnapshotObject>> GetPendingCreationsAsync(DateTimeOffset dueBefore, CancellationToken cancellationToken);
    /// <summary>Live snapshot rows of one kind (DomainController, PublicKeyInfrastructure, Configuration – small sets).</summary>
    Task<IReadOnlyList<SnapshotObject>> GetByKindAsync(WatchObjectKind kind, CancellationToken cancellationToken);
    /// <summary>Snapshots by GUID, including rows marked deleted (restore detection). Missing GUIDs are absent.</summary>
    Task<IReadOnlyDictionary<Guid, SnapshotObject>> GetSnapshotsAsync(IReadOnlyCollection<Guid> objectGuids, CancellationToken cancellationToken);
    Task<SnapshotObject?> FindByDnAsync(string distinguishedName, CancellationToken cancellationToken);
    Task<SnapshotObject?> FindBySidAsync(string sid, CancellationToken cancellationToken);
    /// <summary>Live objects whose DN ends with ",{ancestorDn}" (case-insensitive, normalized).</summary>
    IAsyncEnumerable<SnapshotObject> GetDescendantsAsync(string ancestorDn, CancellationToken cancellationToken);
    IAsyncEnumerable<Guid> GetLiveObjectGuidsAsync(CancellationToken cancellationToken);
    Task<long> CountLiveObjectsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MemberReference>> GetMembersAsync(Guid groupGuid, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> GetGroupsContainingAsync(string memberKey, CancellationToken cancellationToken);
    /// <summary>All group → member edges (privilege evaluation). Mid-size domains: ≤ a few 100 k edges.</summary>
    Task<IReadOnlyList<(Guid GroupGuid, MemberReference Member)>> GetAllMembershipsAsync(CancellationToken cancellationToken);
    Task<PrivilegeState?> GetPrivilegeStateAsync(CancellationToken cancellationToken);
    /// <summary>Live users/computers/service accounts whose primaryGroupID RID is not 513, 514 or 515 (indexed column).</summary>
    Task<IReadOnlyList<(string MemberKey, uint PrimaryGroupRid)>> GetNonDefaultPrimaryGroupsAsync(CancellationToken cancellationToken);
    /// <summary>Applies one cycle atomically and returns the stored events (ids assigned, ascending).</summary>
    Task<IReadOnlyList<WatchEvent>> CommitAsync(DetectionCommit commit, CancellationToken cancellationToken);
}
