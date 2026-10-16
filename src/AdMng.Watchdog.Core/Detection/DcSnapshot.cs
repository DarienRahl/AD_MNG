namespace AdMng.Watchdog.Core.Detection;

/// <summary>What <see cref="DcStateReader"/> read about the watched DC at the start of a cycle (section 10.2).</summary>
public sealed record DcSnapshot
{
    /// <summary>highestCommittedUSN H, read before any search.</summary>
    public required long HighestUsn { get; init; }
    public required string DsServiceName { get; init; }
    public required string DnsHostName { get; init; }
    public required Guid InvocationId { get; init; }
    public bool IsSynchronized { get; init; } = true;
    public DateTimeOffset? CurrentTime { get; init; }
    /// <summary>fSMORoleOwner of the domain head equals dsServiceName (reported only).</summary>
    public bool IsPdc { get; init; }
    public required DirectoryFacts Facts { get; init; }
    public required WellKnownContainers Containers { get; init; }
}
