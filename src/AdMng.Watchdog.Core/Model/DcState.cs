namespace AdMng.Watchdog.Core.Model;

/// <summary>Detection state of the watched domain (one row per domain).</summary>
public sealed record DcState
{
    public required string Domain { get; init; }              // DNS name, store key
    public required string DcHostName { get; init; }
    public required string DsServiceName { get; init; }       // DN of the NTDS Settings object
    public required Guid InvocationId { get; init; }
    public required long HighestUsn { get; init; }            // watermark W (all changes ≤ W processed)
    public DateTimeOffset? LastPollAt { get; init; }
    public DateTimeOffset? LastChangeAt { get; init; }
    public DateTimeOffset? LastFullSyncAt { get; init; }
    public DateTimeOffset? BaselineCompletedAt { get; init; }
    /// <summary>Set by the store on every commit with MembershipRelevant = true (PrivilegeEvaluator crash safety).</summary>
    public DateTimeOffset? MembershipsChangedAt { get; init; }
}

/// <summary>Last processed USN per DC identity (failback without a full resync, 10.3). The store upserts it for the
/// DC of every committed NewState.</summary>
public sealed record DcWatermark(string DsServiceName, Guid InvocationId, long HighestUsn, DateTimeOffset UpdatedAt);
