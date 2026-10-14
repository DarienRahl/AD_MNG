using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Model;

public sealed record DnRewrite(string OldSuffix, string NewSuffix);   // descendants of a moved/renamed container

/// <summary>Everything one detection cycle (or one chunk of it) writes – applied atomically by
/// <see cref="Abstractions.IDetectionStore.CommitAsync"/>.</summary>
public sealed record DetectionCommit
{
    /// <summary>Null = DC state unchanged (every chunk commit). Only the final commit of a cycle writes it – a chunk
    /// never writes a new DC identity with an old watermark (crash mid-Resync must lead to another Resync).</summary>
    public DcState? NewState { get; init; }
    public IReadOnlyList<SnapshotObject> Upserts { get; init; } = [];
    /// <summary>Full replacement of the member list of these groups.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<MemberReference>> Memberships { get; init; } =
        new Dictionary<Guid, IReadOnlyList<MemberReference>>();
    /// <summary>Objects now deleted (tombstone seen): marked deleted (kept for RetentionSettings.DeletedSnapshotDays)
    /// and removed from every member list.</summary>
    public IReadOnlyList<(Guid ObjectGuid, DateTimeOffset DeletedAt)> Deletions { get; init; } = [];
    /// <summary>Hard removal of snapshot rows (moved out of scope, disappeared without tombstone): the row and, for a
    /// group, its own member rows. Other groups' member lists are NOT touched (the object may still be a member).</summary>
    public IReadOnlyList<Guid> Removals { get; init; } = [];
    public IReadOnlyList<DnRewrite> DnRewrites { get; init; } = [];
    public IReadOnlyList<WatchEvent> Events { get; init; } = [];       // Id = 0
    public PrivilegeState? Privileges { get; init; }                   // null = unchanged
    /// <summary>The commit contains membership replacements, deletions/removals of principals or primaryGroupID
    /// changes – the store sets DcState.MembershipsChangedAt.</summary>
    public bool MembershipRelevant { get; init; }
    public bool IsBaseline { get; init; }                              // marks BaselineCompletedAt
}
