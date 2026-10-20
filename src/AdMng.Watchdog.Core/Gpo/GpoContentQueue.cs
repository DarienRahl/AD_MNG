namespace AdMng.Watchdog.Core.Gpo;

/// <summary>Why a GPO is in the read queue (stage B design 8.1). Stable numbers (column <c>reason</c> of
/// <c>gpo_content_queue</c>); a higher number wins when two triggers are merged.</summary>
public enum GpoQueueReason
{
    /// <summary>First read (no snapshot yet): stored silently, no event.</summary>
    Baseline = 0,
    /// <summary>The daily reconciliation found a listing difference without a <c>versionNumber</c> change →
    /// GroupPolicyContentOutOfBand when the settings differ.</summary>
    OutOfBand = 1,
    /// <summary>A GroupPolicyChanged event with a <c>versionNumber</c> change → GroupPolicySettingsChanged.</summary>
    Changed = 2,
    /// <summary>A GPO was created (GroupPolicyChanged with <c>operation: created</c>): its first content is reported.</summary>
    Created = 3,
    /// <summary>A GPO was restored (ObjectRestored): the content is compared with the snapshot kept at its deletion.</summary>
    Restored = 4,
    /// <summary>A GroupPolicyChanged event with a <c>gPCFileSysPath</c> change: the path is validated (8.2) before anything
    /// is read.</summary>
    PathChanged = 5,
}

/// <summary>One entry of the GPO read queue (table <c>gpo_content_queue</c>, idempotent by <see cref="GpoGuid"/>).</summary>
public sealed record GpoQueueEntry
{
    /// <summary>The GUID of the GPC's <c>cn</c> (the SYSVOL folder name).</summary>
    public required Guid GpoGuid { get; init; }

    /// <summary>The GPC's <c>objectGUID</c> when known (the detection snapshot; events reference the GPO by it).</summary>
    public Guid? ObjectGuid { get; init; }

    public GpoQueueReason Reason { get; init; }

    public required DateTimeOffset DueAt { get; init; }

    /// <summary>Started reads. Incremented and stored BEFORE a read, so a GPO whose content crashes or hangs the reader is
    /// dropped after <see cref="GpoContentJob.MaxAttempts"/> (poison-input guard) – also across restarts.</summary>
    public int Attempts { get; init; }

    /// <summary>The <c>ChangeSetId</c> of the triggering GroupPolicyChanged / ObjectRestored event (the content event's
    /// change set, 8.6); empty for the baseline and the reconciliation.</summary>
    public Guid ChangeSetId { get; init; }

    public required DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>The first read attempt – the start of the SYSVOL lag tolerance (8.7).</summary>
    public DateTimeOffset? FirstAttemptAt { get; init; }

    /// <summary>The entry was parked after <see cref="GpoContentJob.MaxAttempts"/> failed attempts.</summary>
    public bool IsParked => Attempts >= GpoContentJob.MaxAttempts;

    /// <summary>
    /// Merges a new trigger into this queued entry: the earlier due time, the stronger reason, the newer change set (when
    /// it has one), the object GUID when this one has none; attempts, enqueue time and first attempt are kept. A parked
    /// entry is replaced by the trigger (the GPO changed: it gets new attempts).
    /// </summary>
    public GpoQueueEntry Merge(GpoQueueEntry trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (IsParked)
        {
            return trigger with { ObjectGuid = trigger.ObjectGuid ?? ObjectGuid };
        }

        return this with
        {
            ObjectGuid = ObjectGuid ?? trigger.ObjectGuid,
            Reason = trigger.Reason > Reason ? trigger.Reason : Reason,
            DueAt = trigger.DueAt < DueAt ? trigger.DueAt : DueAt,
            ChangeSetId = trigger.ChangeSetId != Guid.Empty ? trigger.ChangeSetId : ChangeSetId,
        };
    }
}

/// <summary>A stored GPO snapshot and its deletion mark.</summary>
public sealed record StoredGpoContent
{
    public required GpoContentSnapshot Snapshot { get; init; }

    /// <summary>Set while the GPO is deleted (the snapshot is purged after <c>DeletedSnapshotDays</c>).</summary>
    public DateTimeOffset? DeletedAt { get; init; }
}

/// <summary>Cursor and milestones of <see cref="GpoContentJob"/> (keys <c>gpo_content.*</c> of the <c>meta</c> table).</summary>
public sealed record GpoContentJobState
{
    /// <summary>The last event id examined for triggers; null before the first run (the job then starts at the newest
    /// event – older changes are covered by the baseline).</summary>
    public long? EventCursor { get; init; }

    public DateTimeOffset? BaselineCompletedAt { get; init; }

    public DateTimeOffset? LastReconciliationAt { get; init; }

    public DateTimeOffset? LastReadAt { get; init; }
}
