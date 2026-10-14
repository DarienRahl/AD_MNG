using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>The long-lived collaborators of the detection pipeline (one instance per engine).</summary>
internal sealed record DetectionServices
{
    public required IDetectionStore Store { get; init; }
    public required IEventStore Events { get; init; }
    public required TimeProvider Time { get; init; }
    public required DomainControllerDirectory DomainControllers { get; init; }
    public required SyncAccountDetector SyncAccounts { get; init; }
    public required LockoutPolicyResolver LockoutPolicies { get; init; }
    public required SeverityResolver Severity { get; init; }
    public ReplicationMetadataReader Metadata { get; init; } = new();
    public MembershipReader Members { get; init; } = new();
    public MembershipDiffer MemberDiffer { get; init; } = new();
    public EventTagger Tagger { get; init; } = new();
    public ObjectDiffer Differ { get; init; } = new();
    public DetectionHostOptions HostOptions { get; init; } = new();
}
