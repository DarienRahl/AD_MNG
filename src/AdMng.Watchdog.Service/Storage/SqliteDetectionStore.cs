using System.Runtime.CompilerServices;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// <see cref="IDetectionStore"/> over <c>dc_state</c>, <c>dc_watermarks</c>, <c>snapshot_objects</c>,
/// <c>snapshot_members</c> and <c>privilege_state</c>. <see cref="CommitAsync"/> applies one detection cycle (or chunk) in
/// one transaction in this order: DN rewrites, upserts, membership replacements, deletions, removals, events, privilege
/// state, DC state. Store-written times (watermarks, <c>MembershipsChangedAt</c>, <c>BaselineCompletedAt</c>) come from
/// the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class SqliteDetectionStore : IDetectionStore
{
    private static readonly int[] AccountKinds = [(int)WatchObjectKind.User, (int)WatchObjectKind.Computer, (int)WatchObjectKind.ServiceAccount];

    private readonly SqliteDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly EventAppendedNotifier _notifier;

    internal SqliteDetectionStore(SqliteDatabase database, TimeProvider timeProvider, EventAppendedNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(notifier);
        _database = database;
        _timeProvider = timeProvider;
        _notifier = notifier;
    }

    public Task<DcState?> GetDcStateAsync(string domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return Task.FromResult(_database.Read(c => ReadState(c, null, domain)));
    }

    public Task<DcWatermark?> GetWatermarkAsync(string dsServiceName, Guid invocationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dsServiceName);
        var result = _database.Read(c => SqliteValues.Query(
            c,
            null,
            "SELECT highest_usn, updated_at FROM dc_watermarks WHERE ds_service_name_norm = $dsa AND invocation_id = $invocation;",
            r => new DcWatermark(dsServiceName, invocationId, r.GetInt64(0), SqliteValues.FromUnixMs(r.GetInt64(1))),
            ("$dsa", SqliteValues.NormalizeDn(dsServiceName)),
            ("$invocation", SqliteValues.ToBlob(invocationId))).FirstOrDefault());
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SnapshotObject>> GetPendingCreationsAsync(DateTimeOffset dueBefore, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SnapshotObject>>(QuerySnapshots(
            "is_deleted = 0 AND creation_pending_until IS NOT NULL AND creation_pending_until <= $due ORDER BY creation_pending_until",
            ("$due", SqliteValues.ToUnixMs(dueBefore))));

    public Task<IReadOnlyList<SnapshotObject>> GetByKindAsync(WatchObjectKind kind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SnapshotObject>>(QuerySnapshots("is_deleted = 0 AND kind = $kind", ("$kind", (int)kind)));

    public Task<IReadOnlyDictionary<Guid, SnapshotObject>> GetSnapshotsAsync(IReadOnlyCollection<Guid> objectGuids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectGuids);
        var result = new Dictionary<Guid, SnapshotObject>();
        if (objectGuids.Count > 0)
        {
            _database.Read(connection =>
            {
                using var command = SqliteValues.Command(connection, null, $"SELECT {SnapshotRows.Columns} FROM snapshot_objects WHERE object_guid = $guid;");
                var parameter = command.Parameters.Add("$guid", SqliteType.Blob);
                foreach (var guid in objectGuids.Distinct())
                {
                    parameter.Value = SqliteValues.ToBlob(guid);
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        result[guid] = SnapshotRows.Read(reader);
                    }
                }

                return true;
            });
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, SnapshotObject>>(result);
    }

    public Task<SnapshotObject?> FindByDnAsync(string distinguishedName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        return Task.FromResult(QuerySnapshots(
            "is_deleted = 0 AND dn_norm = $dn LIMIT 1",
            ("$dn", SqliteValues.NormalizeDn(distinguishedName))).FirstOrDefault());
    }

    public Task<SnapshotObject?> FindBySidAsync(string sid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sid);
        return Task.FromResult(QuerySnapshots("is_deleted = 0 AND sid = $sid LIMIT 1", ("$sid", SqliteValues.SidOrNull(sid))).FirstOrDefault());
    }

    public async IAsyncEnumerable<SnapshotObject> GetDescendantsAsync(string ancestorDn, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ancestorDn);
        if (!DistinguishedName.TryParse(ancestorDn, out var ancestor) || ancestor.IsRoot)
        {
            yield break;
        }

        var suffix = "," + SqliteValues.NormalizeDn(ancestorDn);
        var rows = QuerySnapshots(
            "is_deleted = 0 AND substr(dn_norm, -length($suffix)) = $suffix",
            ("$suffix", suffix));
        foreach (var snapshot in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DistinguishedName.TryParse(snapshot.Dn, out var dn) && dn.IsDescendantOf(ancestor))
            {
                yield return snapshot;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async IAsyncEnumerable<Guid> GetLiveObjectGuidsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var guids = _database.Read(c => SqliteValues.Query(c, null, "SELECT object_guid FROM snapshot_objects WHERE is_deleted = 0;", r => SqliteValues.GuidFrom(r, 0)));
        foreach (var guid in guids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return guid;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<long> CountLiveObjectsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM snapshot_objects WHERE is_deleted = 0;")));

    public Task<IReadOnlyList<MemberReference>> GetMembersAsync(Guid groupGuid, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MemberReference>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            "SELECT member_key, member_guid, member_sid, member_dn FROM snapshot_members WHERE group_guid = $group ORDER BY member_key;",
            ReadMember,
            ("$group", SqliteValues.ToBlob(groupGuid)))));

    public Task<IReadOnlyList<Guid>> GetGroupsContainingAsync(string memberKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memberKey);
        return Task.FromResult<IReadOnlyList<Guid>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            "SELECT DISTINCT group_guid FROM snapshot_members WHERE member_key = $key COLLATE NOCASE;",
            r => SqliteValues.GuidFrom(r, 0),
            ("$key", memberKey))));
    }

    public Task<IReadOnlyList<(Guid GroupGuid, MemberReference Member)>> GetAllMembershipsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(Guid, MemberReference)>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            "SELECT member_key, member_guid, member_sid, member_dn, group_guid FROM snapshot_members;",
            r => (SqliteValues.GuidFrom(r, 4), ReadMember(r)))));

    public Task<PrivilegeState?> GetPrivilegeStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_database.Read(c => SqliteValues.Query(
            c,
            null,
            "SELECT state FROM privilege_state WHERE id = 1;",
            r => BlobCodec.Decode((byte[])r.GetValue(0), WatchdogStorageJsonContext.Default.PrivilegeState)).FirstOrDefault()));

    public Task<IReadOnlyList<(string MemberKey, uint PrimaryGroupRid)>> GetNonDefaultPrimaryGroupsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(string, uint)>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            $"""
            SELECT object_guid, primary_group_rid FROM snapshot_objects
            WHERE primary_group_rid IS NOT NULL AND primary_group_rid NOT IN (513, 514, 515)
              AND is_deleted = 0 AND kind IN ({string.Join(", ", AccountKinds)});
            """,
            r => (SqliteValues.GuidFrom(r, 0).ToString("N"), (uint)r.GetInt64(1)))));

    public async Task<IReadOnlyList<WatchEvent>> CommitAsync(DetectionCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var stored = await _database.WriteAsync((connection, transaction) => Apply(connection, transaction, commit), cancellationToken)
            .ConfigureAwait(false);
        if (stored.Count > 0)
        {
            _notifier.Notify();
        }

        return stored;
    }

    private List<WatchEvent> Apply(SqliteConnection connection, SqliteTransaction transaction, DetectionCommit commit)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var rewrite in commit.DnRewrites)
        {
            SnapshotDnRewrite.Apply(connection, transaction, rewrite);
        }

        foreach (var upsert in commit.Upserts)
        {
            SnapshotRows.Upsert(connection, transaction, upsert, now);
        }

        foreach (var (group, members) in commit.Memberships)
        {
            ReplaceMembers(connection, transaction, group, members);
        }

        foreach (var (guid, deletedAt) in commit.Deletions)
        {
            MarkDeleted(connection, transaction, guid, deletedAt, now);
        }

        foreach (var guid in commit.Removals)
        {
            var blob = SqliteValues.ToBlob(guid);
            SqliteValues.Execute(connection, transaction, "DELETE FROM snapshot_objects WHERE object_guid = $guid;", ("$guid", blob));
            SqliteValues.Execute(connection, transaction, "DELETE FROM snapshot_members WHERE group_guid = $guid;", ("$guid", blob));
        }

        var stored = commit.Events.Select(e => EventRows.Insert(connection, transaction, e)).ToList();
        if (commit.Privileges is { } privileges)
        {
            SqliteValues.Execute(
                connection,
                transaction,
                "INSERT OR REPLACE INTO privilege_state (id, state, computed_at) VALUES (1, $state, $computedAt);",
                ("$state", BlobCodec.Encode(privileges, WatchdogStorageJsonContext.Default.PrivilegeState)),
                ("$computedAt", SqliteValues.ToUnixMs(privileges.ComputedAt)));
        }

        ApplyState(connection, transaction, commit, now);
        return stored;
    }

    private static void ReplaceMembers(SqliteConnection connection, SqliteTransaction transaction, Guid group, IReadOnlyList<MemberReference> members)
    {
        var groupBlob = SqliteValues.ToBlob(group);
        SqliteValues.Execute(connection, transaction, "DELETE FROM snapshot_members WHERE group_guid = $group;", ("$group", groupBlob));
        using var insert = SqliteValues.Command(
            connection,
            transaction,
            """
            INSERT OR REPLACE INTO snapshot_members (group_guid, member_key, member_guid, member_sid, member_dn)
            VALUES ($group, $key, $guid, $sid, $dn);
            """);
        insert.Parameters.AddWithValue("$group", groupBlob);
        var key = insert.Parameters.Add("$key", SqliteType.Text);
        var guid = insert.Parameters.Add("$guid", SqliteType.Blob);
        var sid = insert.Parameters.Add("$sid", SqliteType.Text);
        var dn = insert.Parameters.Add("$dn", SqliteType.Text);
        foreach (var member in members)
        {
            key.Value = member.Key;
            guid.Value = SqliteValues.ToBlobOrNull(member.ObjectGuid);
            sid.Value = SqliteValues.OrNull(member.Sid);
            dn.Value = member.Dn;
            insert.ExecuteNonQuery();
        }
    }

    private static void MarkDeleted(SqliteConnection connection, SqliteTransaction transaction, Guid guid, DateTimeOffset deletedAt, DateTimeOffset now)
    {
        var blob = SqliteValues.ToBlob(guid);
        SqliteValues.Execute(
            connection,
            transaction,
            "UPDATE snapshot_objects SET is_deleted = 1, deleted_at = $deletedAt, creation_pending_until = NULL, updated_at = $now WHERE object_guid = $guid;",
            ("$guid", blob),
            ("$deletedAt", SqliteValues.ToUnixMs(deletedAt)),
            ("$now", SqliteValues.ToUnixMs(now)));
        SqliteValues.Execute(
            connection,
            transaction,
            "DELETE FROM snapshot_members WHERE member_guid = $guid OR member_key = $key COLLATE NOCASE;",
            ("$guid", blob),
            ("$key", guid.ToString("N")));
    }

    private static void ApplyState(SqliteConnection connection, SqliteTransaction transaction, DetectionCommit commit, DateTimeOffset now)
    {
        if (commit.NewState is { } state)
        {
            var previous = ReadState(connection, transaction, state.Domain);
            var merged = state with
            {
                MembershipsChangedAt = previous?.MembershipsChangedAt ?? state.MembershipsChangedAt,
                BaselineCompletedAt = state.BaselineCompletedAt ?? previous?.BaselineCompletedAt,
            };
            WriteState(connection, transaction, merged);
            SqliteValues.Execute(
                connection,
                transaction,
                """
                INSERT OR REPLACE INTO dc_watermarks (ds_service_name_norm, invocation_id, highest_usn, updated_at)
                VALUES ($dsa, $invocation, $usn, $now);
                """,
                ("$dsa", SqliteValues.NormalizeDn(state.DsServiceName)),
                ("$invocation", SqliteValues.ToBlob(state.InvocationId)),
                ("$usn", state.HighestUsn),
                ("$now", SqliteValues.ToUnixMs(now)));
        }

        // A chunk commit (NewState = null) marks the known domain row(s); one service instance watches one domain.
        var domainFilter = commit.NewState is not null ? " WHERE domain = $domain COLLATE NOCASE" : string.Empty;
        var domain = ("$domain", (object?)commit.NewState?.Domain);
        if (commit.MembershipRelevant)
        {
            SqliteValues.Execute(connection, transaction, $"UPDATE dc_state SET memberships_changed_at = $now{domainFilter};", ("$now", SqliteValues.ToUnixMs(now)), domain);
        }

        if (commit.IsBaseline)
        {
            SqliteValues.Execute(
                connection,
                transaction,
                $"UPDATE dc_state SET baseline_completed_at = coalesce(baseline_completed_at, $now){domainFilter};",
                ("$now", SqliteValues.ToUnixMs(now)),
                domain);
        }
    }

    private static DcState? ReadState(SqliteConnection connection, SqliteTransaction? transaction, string domain) =>
        SqliteValues.Query(
            connection,
            transaction,
            """
            SELECT domain, dc_host, ds_service_name, invocation_id, highest_usn, last_poll_at, last_change_at, last_full_sync_at,
              baseline_completed_at, memberships_changed_at
            FROM dc_state WHERE domain = $domain COLLATE NOCASE;
            """,
            r => new DcState
            {
                Domain = r.GetString(0),
                DcHostName = r.GetString(1),
                DsServiceName = r.GetString(2),
                InvocationId = SqliteValues.GuidFrom(r, 3),
                HighestUsn = r.GetInt64(4),
                LastPollAt = SqliteValues.FromUnixMsOrNull(r, 5),
                LastChangeAt = SqliteValues.FromUnixMsOrNull(r, 6),
                LastFullSyncAt = SqliteValues.FromUnixMsOrNull(r, 7),
                BaselineCompletedAt = SqliteValues.FromUnixMsOrNull(r, 8),
                MembershipsChangedAt = SqliteValues.FromUnixMsOrNull(r, 9),
            },
            ("$domain", domain)).FirstOrDefault();

    private static void WriteState(SqliteConnection connection, SqliteTransaction transaction, DcState state)
    {
        SqliteValues.Execute(connection, transaction, "DELETE FROM dc_state WHERE domain = $domain COLLATE NOCASE;", ("$domain", state.Domain));
        SqliteValues.Execute(
            connection,
            transaction,
            """
            INSERT INTO dc_state (domain, dc_host, ds_service_name, invocation_id, highest_usn, last_poll_at, last_change_at,
              last_full_sync_at, baseline_completed_at, memberships_changed_at)
            VALUES ($domain, $host, $dsa, $invocation, $usn, $poll, $change, $fullSync, $baseline, $memberships);
            """,
            ("$domain", state.Domain),
            ("$host", state.DcHostName),
            ("$dsa", state.DsServiceName),
            ("$invocation", SqliteValues.ToBlob(state.InvocationId)),
            ("$usn", state.HighestUsn),
            ("$poll", SqliteValues.ToUnixMsOrNull(state.LastPollAt)),
            ("$change", SqliteValues.ToUnixMsOrNull(state.LastChangeAt)),
            ("$fullSync", SqliteValues.ToUnixMsOrNull(state.LastFullSyncAt)),
            ("$baseline", SqliteValues.ToUnixMsOrNull(state.BaselineCompletedAt)),
            ("$memberships", SqliteValues.ToUnixMsOrNull(state.MembershipsChangedAt)));
    }

    private List<SnapshotObject> QuerySnapshots(string where, params (string Name, object? Value)[] parameters) =>
        _database.Read(c => SqliteValues.Query(c, null, $"SELECT {SnapshotRows.Columns} FROM snapshot_objects WHERE {where};", SnapshotRows.Read, parameters));

    private static MemberReference ReadMember(SqliteDataReader reader) => new()
    {
        Key = reader.GetString(0),
        ObjectGuid = SqliteValues.GuidOrNull(reader, 1),
        Sid = SqliteValues.StringOrNull(reader, 2),
        Dn = reader.GetString(3),
    };
}
