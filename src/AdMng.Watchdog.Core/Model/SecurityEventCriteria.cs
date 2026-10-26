namespace AdMng.Watchdog.Core.Model;

/// <summary>Search of stored Security-log records (<see cref="Abstractions.ISecurityEventStore.FindAsync"/>).</summary>
public sealed record SecurityEventCriteria
{
    public IReadOnlyList<int> EventCodes { get; init; } = [];
    public string? Computer { get; init; }                 // null = any DC
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public string? TargetSid { get; init; }
    public Guid? ObjectGuid { get; init; }
    public string? MemberSid { get; init; }
    public string? TargetUserName { get; init; }
    public string? ObjectDn { get; init; }
    /// <summary>4624 TargetLogonId, normalized (<see cref="SecurityEvent.NormalizeLogonId"/>) – collector mode (stage B
    /// design 7.8).</summary>
    public string? LogonId { get; init; }
    public int Limit { get; init; } = 200;
}
