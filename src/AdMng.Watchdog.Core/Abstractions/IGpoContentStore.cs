using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>
/// GPO content snapshots, the read queue and the state of <see cref="GpoContentJob"/> (stage B design 8.1, 8.8 – tables
/// <c>gpo_content</c> and <c>gpo_content_queue</c> of migration 0005, the job state in <c>meta</c>). Only the latest
/// snapshot per GPO is kept; masked settings (GPP <c>cpassword</c> …) carry no value, so nothing secret is stored
/// (ADR-0008 as amended by ADR-0009). GPOs are keyed by the GUID of the GPC's <c>cn</c> (the SYSVOL folder name).
/// </summary>
public interface IGpoContentStore
{
    /// <summary>The stored snapshot of a GPO (also one marked deleted), or null.</summary>
    Task<StoredGpoContent?> GetGpoContentAsync(Guid gpoGuid, CancellationToken cancellationToken);

    /// <summary>Every stored snapshot, ordered by GUID; without <paramref name="includeSettings"/> the settings are left out
    /// (status, reconciliation – the inventory is always included).</summary>
    Task<IReadOnlyList<StoredGpoContent>> ListGpoContentAsync(bool includeSettings, CancellationToken cancellationToken);

    /// <summary>
    /// One transaction: stores <paramref name="snapshot"/> as the GPO's only snapshot (clearing a deletion mark), appends
    /// <paramref name="events"/> (ids assigned, returned in order) and removes the GPO from the queue.
    /// </summary>
    Task<IReadOnlyList<WatchEvent>> CommitGpoContentAsync(GpoContentSnapshot snapshot, IReadOnlyList<WatchEvent> events, CancellationToken cancellationToken);

    /// <summary>One transaction: appends <paramref name="events"/> and removes the GPO from the queue without touching its
    /// snapshot (a redirected or missing <c>gPCFileSysPath</c> – nothing is read).</summary>
    Task<IReadOnlyList<WatchEvent>> CompleteGpoQueueEntryAsync(Guid gpoGuid, IReadOnlyList<WatchEvent> events, CancellationToken cancellationToken);

    /// <summary>Marks the snapshot deleted at <paramref name="deletedAt"/> (kept <c>DeletedSnapshotDays</c> for the diff of a
    /// restore) and removes the GPO from the queue. No-op for an unknown GPO.</summary>
    Task MarkGpoContentDeletedAsync(Guid gpoGuid, DateTimeOffset deletedAt, CancellationToken cancellationToken);

    /// <summary>Adds the GPO to the queue, idempotent by GUID: an existing entry is merged
    /// (<see cref="GpoQueueEntry.Merge"/>: earliest due time, strongest reason, newest change set, attempts kept).</summary>
    Task EnqueueGpoContentAsync(GpoQueueEntry entry, CancellationToken cancellationToken);

    /// <summary>The whole queue, ordered by due time, then GUID.</summary>
    Task<IReadOnlyList<GpoQueueEntry>> GetGpoQueueAsync(CancellationToken cancellationToken);

    /// <summary>Replaces an existing queue entry (attempts, due time, first attempt); no-op when the GPO is not queued.</summary>
    Task UpdateGpoQueueEntryAsync(GpoQueueEntry entry, CancellationToken cancellationToken);

    /// <summary>Removes the GPO from the queue (no-op when it is not queued).</summary>
    Task RemoveGpoQueueEntryAsync(Guid gpoGuid, CancellationToken cancellationToken);

    /// <summary>The job's cursor and milestones (empty state on a new database).</summary>
    Task<GpoContentJobState> GetGpoContentStateAsync(CancellationToken cancellationToken);

    Task SaveGpoContentStateAsync(GpoContentJobState state, CancellationToken cancellationToken);
}
