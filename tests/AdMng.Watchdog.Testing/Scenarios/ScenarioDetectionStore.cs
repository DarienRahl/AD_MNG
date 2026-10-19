using System.Globalization;
using System.Runtime.CompilerServices;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Scenarios;

/// <summary>
/// In-memory <see cref="IDetectionStore"/>, <see cref="IEventStore"/> and <see cref="IExpiryNoticeStore"/> for the
/// detection scenarios (W-detect test infrastructure; the shared InMemoryWatchdogStore is W-notify's). Commit semantics
/// follow section 6.1: DN rewrites applied in order to stored descendants and member DNs, upserts, member list
/// replacements, deletions (row marked deleted, key removed from every member list), removals (row and its own member
/// list), events with ascending ids, privilege state, DC state with the per-DC watermark and MembershipsChangedAt.
/// <see cref="FailCommit"/> simulates a crash on a given commit.
/// </summary>
public sealed class ScenarioDetectionStore : IDetectionStore, IEventStore, IExpiryNoticeStore
{
    private static readonly uint[] DefaultPrimaryGroups = [WellKnownRids.DomainUsers, WellKnownRids.DomainGuests, WellKnownRids.DomainComputers];

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, SnapshotObject> _snapshots = [];
    private readonly Dictionary<Guid, List<MemberReference>> _members = [];
    private readonly List<WatchEvent> _events = [];
    private readonly Dictionary<(string Dsa, Guid Invocation), DcWatermark> _watermarks = [];
    private readonly HashSet<(Guid, ExpiryKind, long, int)> _notices = [];
    private readonly Dictionary<ExpiryKind, IReadOnlyList<UpcomingExpiry>> _upcoming = [];
    private readonly List<DetectionCommit> _commits = [];
    private DcState? _state;
    private PrivilegeState? _privileges;
    private int _commitCount;
    private int? _failOnCommit;
    private bool _failFinalCommit;

    public ScenarioDetectionStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>Every successful commit, in order.</summary>
    public IReadOnlyList<DetectionCommit> Commits
    {
        get
        {
            lock (_gate)
            {
                return [.. _commits];
            }
        }
    }

    /// <summary>Every stored event, by id.</summary>
    public IReadOnlyList<WatchEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>The stored DC state.</summary>
    public DcState? State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The stored privilege state.</summary>
    public PrivilegeState? Privileges
    {
        get
        {
            lock (_gate)
            {
                return _privileges;
            }
        }
    }

    /// <summary>All snapshot rows (incl. deleted).</summary>
    public IReadOnlyCollection<SnapshotObject> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return [.. _snapshots.Values];
            }
        }
    }

    /// <summary>Throws an <see cref="IOException"/> instead of applying the <paramref name="commitNumber"/>-th commit from now (1 = next).</summary>
    public void FailCommit(int commitNumber)
    {
        lock (_gate)
        {
            _failOnCommit = _commitCount + commitNumber;
        }
    }

    /// <summary>
    /// Throws an <see cref="IOException"/> instead of applying the next final commit of a cycle (the one that writes
    /// <see cref="DetectionCommit.NewState"/>) – a crash after the chunk commits, before W moves.
    /// </summary>
    public void FailNextFinalCommit()
    {
        lock (_gate)
        {
            _failFinalCommit = true;
        }
    }

    // ---------------------------------------------------------------- IDetectionStore

    public Task<DcState?> GetDcStateAsync(string domain, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_state is not null && string.Equals(_state.Domain, domain, StringComparison.OrdinalIgnoreCase) ? _state : null);
        }
    }

    public Task<DcWatermark?> GetWatermarkAsync(string dsServiceName, Guid invocationId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_watermarks.TryGetValue((Normalize(dsServiceName), invocationId), out var watermark) ? watermark : null);
        }
    }

    public Task<IReadOnlyList<SnapshotObject>> GetPendingCreationsAsync(DateTimeOffset dueBefore, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SnapshotObject>>([.. _snapshots.Values.Where(s => !s.IsDeleted && s.CreationPendingUntil <= dueBefore)]);
        }
    }

    public Task<IReadOnlyList<SnapshotObject>> GetByKindAsync(WatchObjectKind kind, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SnapshotObject>>([.. _snapshots.Values.Where(s => !s.IsDeleted && s.Kind == kind)]);
        }
    }

    public Task<IReadOnlyDictionary<Guid, SnapshotObject>> GetSnapshotsAsync(IReadOnlyCollection<Guid> objectGuids, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<Guid, SnapshotObject> result = objectGuids.Where(_snapshots.ContainsKey).Distinct().ToDictionary(g => g, g => _snapshots[g]);
            return Task.FromResult(result);
        }
    }

    public Task<SnapshotObject?> FindByDnAsync(string distinguishedName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_snapshots.Values.FirstOrDefault(s => !s.IsDeleted && DistinguishedName.AreEqual(s.Dn, distinguishedName)));
        }
    }

    public Task<SnapshotObject?> FindBySidAsync(string sid, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_snapshots.Values.FirstOrDefault(s => !s.IsDeleted && string.Equals(s.Sid, sid, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public async IAsyncEnumerable<SnapshotObject> GetDescendantsAsync(string ancestorDn, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<SnapshotObject> result;
        lock (_gate)
        {
            var ancestor = DistinguishedName.Parse(ancestorDn);
            result = [.. _snapshots.Values.Where(s => !s.IsDeleted && DistinguishedName.TryParse(s.Dn, out var dn) && dn.IsDescendantOf(ancestor))];
        }

        foreach (var snapshot in result)
        {
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
            return Task.FromResult<IReadOnlyList<MemberReference>>(_members.TryGetValue(groupGuid, out var members) ? [.. members] : []);
        }
    }

    public Task<IReadOnlyList<Guid>> GetGroupsContainingAsync(string memberKey, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Guid>>([.. _members.Where(m => m.Value.Any(r => SameKey(r.Key, memberKey))).Select(m => m.Key)]);
        }
    }

    public Task<IReadOnlyList<(Guid GroupGuid, MemberReference Member)>> GetAllMembershipsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<(Guid, MemberReference)>>([.. _members.SelectMany(m => m.Value.Select(r => (m.Key, r)))]);
        }
    }

    public Task<PrivilegeState?> GetPrivilegeStateAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_privileges);
        }
    }

    public Task<IReadOnlyList<(string MemberKey, uint PrimaryGroupRid)>> GetNonDefaultPrimaryGroupsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var result = new List<(string, uint)>();
            foreach (var snapshot in _snapshots.Values.Where(s => !s.IsDeleted && s.Kind is WatchObjectKind.User or WatchObjectKind.Computer or WatchObjectKind.ServiceAccount))
            {
                if (snapshot.Attributes.TryGetValue(Attr.PrimaryGroupId, out var primary) && primary.Values.Count > 0
                    && uint.TryParse(primary.Values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rid) && !DefaultPrimaryGroups.Contains(rid))
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
        lock (_gate)
        {
            _commitCount++;
            if (_failOnCommit == _commitCount)
            {
                _failOnCommit = null;
                throw new IOException("Injected store failure (simulated crash).");
            }

            if (_failFinalCommit && commit.NewState is not null)
            {
                _failFinalCommit = false;
                throw new IOException("Injected failure of the final commit (simulated crash).");
            }

            foreach (var rewrite in commit.DnRewrites)
            {
                ApplyRewrite(rewrite);
            }

            foreach (var upsert in commit.Upserts)
            {
                _snapshots[upsert.ObjectGuid] = upsert;
            }

            foreach (var (group, members) in commit.Memberships)
            {
                _members[group] = [.. members];
            }

            foreach (var (guid, deletedAt) in commit.Deletions)
            {
                if (_snapshots.TryGetValue(guid, out var snapshot))
                {
                    _snapshots[guid] = snapshot with { IsDeleted = true, DeletedAt = deletedAt, CreationPendingUntil = null };
                }

                var key = guid.ToString("N");
                foreach (var list in _members.Values)
                {
                    list.RemoveAll(m => SameKey(m.Key, key));
                }
            }

            foreach (var guid in commit.Removals)
            {
                _snapshots.Remove(guid);
                _members.Remove(guid);
            }

            var stored = new List<WatchEvent>();
            foreach (var watchEvent in commit.Events)
            {
                var withId = watchEvent with { Id = _events.Count + 1 };
                _events.Add(withId);
                stored.Add(withId);
            }

            if (commit.Privileges is not null)
            {
                _privileges = commit.Privileges;
            }

            if (commit.NewState is { } state)
            {
                _state = state with { MembershipsChangedAt = _state?.MembershipsChangedAt ?? state.MembershipsChangedAt };
                _watermarks[(Normalize(state.DsServiceName), state.InvocationId)] =
                    new DcWatermark(state.DsServiceName, state.InvocationId, state.HighestUsn, _timeProvider.GetUtcNow());
            }

            if (commit.MembershipRelevant && _state is not null)
            {
                _state = _state with { MembershipsChangedAt = _timeProvider.GetUtcNow() };
            }

            if (commit.IsBaseline && _state is not null)
            {
                _state = _state with { BaselineCompletedAt = _state.BaselineCompletedAt ?? _timeProvider.GetUtcNow() };
            }

            _commits.Add(commit);
            return Task.FromResult<IReadOnlyList<WatchEvent>>(stored);
        }
    }

    // ---------------------------------------------------------------- IEventStore

    public Task<long> AppendAsync(WatchEvent watchEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        lock (_gate)
        {
            var withId = watchEvent with { Id = _events.Count + 1 };
            _events.Add(withId);
            return Task.FromResult(withId.Id);
        }
    }

    public Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var items = _events.OrderByDescending(e => e.Id).Take(query.Limit).ToList();
            return Task.FromResult(new EventPage { Items = items });
        }
    }

    public Task WaitForNewerAsync(long afterId, TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<WatchEvent?> GetAsync(long id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.FirstOrDefault(e => e.Id == id));
        }
    }

    public Task<WatchEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.FirstOrDefault(e => e.EventId == eventId));
        }
    }

    public Task<IReadOnlyList<WatchEvent>> GetChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WatchEvent>>([.. _events.Where(e => e.ChangeSetId == changeSetId)]);
        }
    }

    public async IAsyncEnumerable<WatchEvent> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<WatchEvent> result;
        lock (_gate)
        {
            result = [.. _events.Where(e => e.DetectedAt >= from && e.DetectedAt <= to)];
        }

        foreach (var watchEvent in result)
        {
            await Task.Yield();
            yield return watchEvent;
        }
    }

    public Task<IReadOnlyList<WatchEvent>> GetPendingAttributionAsync(DateTimeOffset dueBefore, int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WatchEvent>>([.. _events.Where(e => e.Attribution.Status == AttributionStatus.Pending).Take(limit)]);
        }
    }

    public Task UpdateAttributionAsync(long id, AttributionUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var index = _events.FindIndex(e => e.Id == id);
            if (index >= 0)
            {
                var current = _events[index];
                _events[index] = current with
                {
                    Attribution = update.Attribution,
                    Tags = [.. current.Tags.Concat(update.AddedTags).Distinct(StringComparer.Ordinal)],
                    Severity = update.Severity ?? current.Severity,
                    Lockout = update.Lockout ?? current.Lockout,
                };
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WatchEvent>>([]);

    public Task CommitEvaluationAsync(IReadOnlyList<EventEvaluation> evaluations, IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<bool> AcknowledgeAsync(long id, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<int> AcknowledgeManyAsync(IReadOnlyList<long> ids, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<int> CountForObjectAsync(Guid objectGuid, WatchEventKind kind, DateTimeOffset since, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.Count(e => e.Object.ObjectGuid == objectGuid && e.Kind == kind && e.DetectedAt >= since));
        }
    }

    public Task<EventCountsDto> CountAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var recent = _events.Where(e => e.DetectedAt >= since).ToList();
            return Task.FromResult(new EventCountsDto
            {
                Total = recent.Count,
                BySeverity = recent.GroupBy(e => e.Severity).ToDictionary(g => g.Key, g => g.Count()),
            });
        }
    }

    public Task<long> GetLatestIdAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult((long)_events.Count);
        }
    }

    // ---------------------------------------------------------------- IExpiryNoticeStore

    public Task<bool> TryRecordAsync(ExpiryNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        lock (_gate)
        {
            return Task.FromResult(_notices.Add((notice.ObjectGuid, notice.Kind, notice.ExpiresAtFileTime, notice.ThresholdDays)));
        }
    }

    public Task ReplaceUpcomingAsync(ExpiryKind kind, IReadOnlyList<UpcomingExpiry> upcoming, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _upcoming[kind] = [.. upcoming];
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<UpcomingExpiry>> GetUpcomingAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<UpcomingExpiry>>([.. _upcoming.Values.SelectMany(v => v)]);
        }
    }

    private void ApplyRewrite(DnRewrite rewrite)
    {
        if (!DistinguishedName.TryParse(rewrite.OldSuffix, out var oldBase) || !DistinguishedName.TryParse(rewrite.NewSuffix, out var newBase))
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

    private static string Normalize(string dn) => DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString().ToUpperInvariant() : dn.ToUpperInvariant();

    private static bool SameKey(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
