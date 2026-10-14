using System.Globalization;
using System.Runtime.CompilerServices;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Stores;

// IDetectionStore: dc_state, dc_watermarks, snapshot_objects, snapshot_members, privilege_state.
public sealed partial class InMemoryWatchdogStore
{
    private static readonly uint[] DefaultPrimaryGroupRids = [513, 514, 515];

    private readonly Dictionary<string, DcState> _dcStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string DsServiceName, Guid InvocationId), DcWatermark> _watermarks = [];
    private readonly Dictionary<Guid, SnapshotObject> _snapshots = [];
    private readonly Dictionary<Guid, List<MemberReference>> _members = [];
    private PrivilegeState? _privileges;

    public Task<DcState?> GetDcStateAsync(string domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);
        lock (_gate)
        {
            return Task.FromResult(_dcStates.GetValueOrDefault(domain));
        }
    }

    public Task<DcWatermark?> GetWatermarkAsync(string dsServiceName, Guid invocationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dsServiceName);
        lock (_gate)
        {
            return Task.FromResult(_watermarks.GetValueOrDefault((NormalizeDn(dsServiceName), invocationId)));
        }
    }

    public Task<IReadOnlyList<SnapshotObject>> GetPendingCreationsAsync(DateTimeOffset dueBefore, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<SnapshotObject> result =
            [
                .. _snapshots.Values
                    .Where(s => !s.IsDeleted && s.CreationPendingUntil is { } until && until <= dueBefore)
                    .OrderBy(s => s.CreationPendingUntil)
                    .Select(Copy),
            ];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<SnapshotObject>> GetByKindAsync(WatchObjectKind kind, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<SnapshotObject> result = [.. _snapshots.Values.Where(s => !s.IsDeleted && s.Kind == kind).Select(Copy)];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyDictionary<Guid, SnapshotObject>> GetSnapshotsAsync(IReadOnlyCollection<Guid> objectGuids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectGuids);
        lock (_gate)
        {
            var result = new Dictionary<Guid, SnapshotObject>();
            foreach (var guid in objectGuids)
            {
                if (_snapshots.TryGetValue(guid, out var snapshot))
                {
                    result[guid] = Copy(snapshot);
                }
            }

            return Task.FromResult<IReadOnlyDictionary<Guid, SnapshotObject>>(result);
        }
    }

    public Task<SnapshotObject?> FindByDnAsync(string distinguishedName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        var key = NormalizeDn(distinguishedName);
        lock (_gate)
        {
            var found = _snapshots.Values.FirstOrDefault(s => !s.IsDeleted && NormalizeDn(s.Dn) == key);
            return Task.FromResult(found is null ? null : Copy(found));
        }
    }

    public Task<SnapshotObject?> FindBySidAsync(string sid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sid);
        lock (_gate)
        {
            var found = _snapshots.Values.FirstOrDefault(s => !s.IsDeleted && string.Equals(s.Sid, sid, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found is null ? null : Copy(found));
        }
    }

    public async IAsyncEnumerable<SnapshotObject> GetDescendantsAsync(string ancestorDn, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ancestorDn);
        List<SnapshotObject> result = [];
        if (DistinguishedName.TryParse(ancestorDn, out var ancestor) && !ancestor.IsRoot)
        {
            lock (_gate)
            {
                result.AddRange(_snapshots.Values
                    .Where(s => !s.IsDeleted && DistinguishedName.TryParse(s.Dn, out var dn) && dn.IsDescendantOf(ancestor))
                    .Select(Copy));
            }
        }

        foreach (var snapshot in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return snapshot;
        }
    }

    public async IAsyncEnumerable<Guid> GetLiveObjectGuidsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<Guid> result;
        lock (_gate)
        {
            result = [.. _snapshots.Values.Where(s => !s.IsDeleted).Select(s => s.ObjectGuid)];
        }

        foreach (var guid in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return guid;
        }
    }

    public Task<long> CountLiveObjectsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult((long)_snapshots.Values.Count(s => !s.IsDeleted));
        }
    }

    public Task<IReadOnlyList<MemberReference>> GetMembersAsync(Guid groupGuid, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<MemberReference> result = _members.TryGetValue(groupGuid, out var members) ? [.. members.Select(Copy)] : [];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<Guid>> GetGroupsContainingAsync(string memberKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memberKey);
        lock (_gate)
        {
            IReadOnlyList<Guid> result =
                [.. _members.Where(m => m.Value.Any(r => string.Equals(r.Key, memberKey, StringComparison.OrdinalIgnoreCase))).Select(m => m.Key)];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<(Guid GroupGuid, MemberReference Member)>> GetAllMembershipsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<(Guid, MemberReference)> result = [.. _members.SelectMany(m => m.Value.Select(r => (m.Key, Copy(r))))];
            return Task.FromResult(result);
        }
    }

    public Task<PrivilegeState?> GetPrivilegeStateAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_privileges is null ? null : Copy(_privileges));
        }
    }

    public Task<IReadOnlyList<(string MemberKey, uint PrimaryGroupRid)>> GetNonDefaultPrimaryGroupsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var result = new List<(string, uint)>();
            foreach (var snapshot in _snapshots.Values.Where(s => !s.IsDeleted
                && s.Kind is WatchObjectKind.User or WatchObjectKind.Computer or WatchObjectKind.ServiceAccount))
            {
                if (PrimaryGroupRid(snapshot) is { } rid && !DefaultPrimaryGroupRids.Contains(rid))
                {
                    result.Add((snapshot.ObjectGuid.ToString("N"), rid));
                }
            }

            return Task.FromResult<IReadOnlyList<(string, uint)>>(result);
        }
    }

    public Task<IReadOnlyList<WatchEvent>> CommitAsync(DetectionCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WatchEvent> stored;
        lock (_gate)
        {
            foreach (var rewrite in commit.DnRewrites)
            {
                ApplyRewrite(rewrite);
            }

            foreach (var upsert in commit.Upserts)
            {
                _snapshots[upsert.ObjectGuid] = Copy(upsert);
            }

            foreach (var (group, members) in commit.Memberships)
            {
                _members[group] = [.. members.Select(Copy)];
            }

            foreach (var (guid, deletedAt) in commit.Deletions)
            {
                MarkDeleted(guid, deletedAt);
            }

            foreach (var guid in commit.Removals)
            {
                _snapshots.Remove(guid);
                _members.Remove(guid);
            }

            stored = [.. commit.Events.Select(InsertEvent)];
            if (commit.Privileges is not null)
            {
                _privileges = Copy(commit.Privileges);
            }

            ApplyState(commit);
        }

        SignalAppended(stored.Count);
        return Task.FromResult(stored);
    }

    private void ApplyState(DetectionCommit commit)
    {
        var now = UtcNow;
        if (commit.NewState is { } state)
        {
            var previous = _dcStates.GetValueOrDefault(state.Domain);
            _dcStates[state.Domain] = state with
            {
                MembershipsChangedAt = previous?.MembershipsChangedAt ?? state.MembershipsChangedAt,
                BaselineCompletedAt = state.BaselineCompletedAt ?? previous?.BaselineCompletedAt,
            };
            _watermarks[(NormalizeDn(state.DsServiceName), state.InvocationId)] =
                new DcWatermark(state.DsServiceName, state.InvocationId, state.HighestUsn, now);
        }

        // A chunk commit (NewState = null) marks the domain(s) already known; one instance watches one domain.
        List<string> targets = commit.NewState is { } current ? [current.Domain] : [.. _dcStates.Keys];
        foreach (var domain in targets)
        {
            var row = _dcStates[domain];
            if (commit.MembershipRelevant)
            {
                row = row with { MembershipsChangedAt = now };
            }

            if (commit.IsBaseline && row.BaselineCompletedAt is null)
            {
                row = row with { BaselineCompletedAt = now };
            }

            _dcStates[domain] = row;
        }
    }

    private void MarkDeleted(Guid guid, DateTimeOffset deletedAt)
    {
        if (_snapshots.TryGetValue(guid, out var snapshot))
        {
            _snapshots[guid] = snapshot with { IsDeleted = true, DeletedAt = deletedAt, CreationPendingUntil = null };
        }

        var key = guid.ToString("N");
        foreach (var list in _members.Values)
        {
            list.RemoveAll(m => m.ObjectGuid == guid || string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ApplyRewrite(DnRewrite rewrite)
    {
        if (!DistinguishedName.TryParse(rewrite.OldSuffix, out var oldBase) || oldBase.IsRoot
            || !DistinguishedName.TryParse(rewrite.NewSuffix, out var newBase) || newBase.IsRoot)
        {
            return;
        }

        foreach (var snapshot in _snapshots.Values.ToList())
        {
            if (DistinguishedName.TryParse(snapshot.Dn, out var dn) && dn.IsDescendantOf(oldBase))
            {
                _snapshots[snapshot.ObjectGuid] = snapshot with { Dn = Rebase(dn, oldBase, newBase) };
            }
        }

        foreach (var list in _members.Values)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (DistinguishedName.TryParse(list[i].Dn, out var dn) && dn.IsDescendantOf(oldBase))
                {
                    list[i] = list[i] with { Dn = Rebase(dn, oldBase, newBase) };
                }
            }
        }
    }

    private static string Rebase(DistinguishedName dn, DistinguishedName oldBase, DistinguishedName newBase)
    {
        var result = newBase;
        for (var i = dn.Rdns.Count - oldBase.Rdns.Count - 1; i >= 0; i--)
        {
            result = result.Child(dn.Rdns[i].Type, dn.Rdns[i].Value);
        }

        return result.ToString();
    }

    private static uint? PrimaryGroupRid(SnapshotObject snapshot) =>
        snapshot.Attributes.TryGetValue(Attr.PrimaryGroupId, out var attribute) && attribute.Values.Count > 0
        && uint.TryParse(attribute.Values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rid)
            ? rid
            : null;
}
