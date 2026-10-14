using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Attribution.Fakes;

/// <summary>The snapshot lookups attribution uses (by SID and DN); the detection-cycle members are not needed here and
/// throw.</summary>
internal sealed class FakeDetectionStore : IDetectionStore
{
    private readonly List<SnapshotObject> _objects = [];

    public void Add(SnapshotObject snapshot) => _objects.Add(snapshot);

    public void AddPrincipal(string sid, string sam, string displayName, string dn, WatchObjectKind kind = WatchObjectKind.User) =>
        _objects.Add(new SnapshotObject { ObjectGuid = Guid.NewGuid(), Dn = dn, Kind = kind, Sid = sid, SamAccountName = sam, DisplayName = displayName, Name = dn.Split(',')[0][3..] });

    public Task<SnapshotObject?> FindBySidAsync(string sid, CancellationToken cancellationToken) =>
        Task.FromResult(_objects.FirstOrDefault(o => string.Equals(o.Sid, sid, StringComparison.OrdinalIgnoreCase)));

    public Task<SnapshotObject?> FindByDnAsync(string distinguishedName, CancellationToken cancellationToken) =>
        Task.FromResult(_objects.FirstOrDefault(o => AdMng.Core.Ldap.DistinguishedName.AreEqual(o.Dn, distinguishedName)));

    public Task<DcState?> GetDcStateAsync(string domain, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<DcWatermark?> GetWatermarkAsync(string dsServiceName, Guid invocationId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<SnapshotObject>> GetPendingCreationsAsync(DateTimeOffset dueBefore, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<SnapshotObject>> GetByKindAsync(WatchObjectKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<Guid, SnapshotObject>> GetSnapshotsAsync(IReadOnlyCollection<Guid> objectGuids, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<SnapshotObject> GetDescendantsAsync(string ancestorDn, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<Guid> GetLiveObjectGuidsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<long> CountLiveObjectsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<MemberReference>> GetMembersAsync(Guid groupGuid, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<Guid>> GetGroupsContainingAsync(string memberKey, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<(Guid GroupGuid, MemberReference Member)>> GetAllMembershipsAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<PrivilegeState?> GetPrivilegeStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<(string MemberKey, uint PrimaryGroupRid)>> GetNonDefaultPrimaryGroupsAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<WatchEvent>> CommitAsync(DetectionCommit commit, CancellationToken cancellationToken) => throw new NotSupportedException();
}
