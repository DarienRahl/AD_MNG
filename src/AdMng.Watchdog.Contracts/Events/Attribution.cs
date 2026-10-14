namespace AdMng.Watchdog.Contracts.Events;

public enum AttributionStatus { NotApplicable = 0, Pending = 1, Resolved = 2, Ambiguous = 3, Unknown = 4 }
public enum AttributionConfidence { None = 0, Low = 1, Medium = 2, High = 3 }

/// <summary>One Security-log record an attribution is based on.</summary>
public sealed record EvidenceRef
{
    public required string Dc { get; init; }
    public string Log { get; init; } = "Security";
    public required int EventCode { get; init; }
    public required long RecordId { get; init; }
    public required DateTimeOffset TimeCreated { get; init; }
}

/// <summary>Who made a change and from where (asynchronous enrichment from DC Security logs).</summary>
public sealed record Attribution
{
    public AttributionStatus Status { get; init; } = AttributionStatus.Pending;
    public AttributionConfidence Confidence { get; init; }
    /// <summary>One of <see cref="AttributionReasons"/> when not Resolved (or Resolved with caveats).</summary>
    public string? Reason { get; init; }
    public PrincipalRef? Actor { get; init; }
    /// <summary>Client-supplied or DNS-derived name – never authoritative (shown with its origin, never used in rule
    /// matching or exclusions).</summary>
    public string? Workstation { get; init; }
    /// <summary>Primary source when known (from 4624/4771/4776).</summary>
    public string? IpAddress { get; init; }
    /// <summary>"4624", "4740", "4771", "4776", "dns", "local" – how Workstation/IpAddress were obtained.</summary>
    public string? SourceMethod { get; init; }
    /// <summary>Confidence of the source: capped at Low for SourceMethod dns / 4740 / NTLM WorkstationName; never
    /// higher than <see cref="Confidence"/>.</summary>
    public AttributionConfidence SourceConfidence { get; init; }
    /// <summary>Why the source is missing: SOURCE_LOGON_NOT_FOUND, SOURCE_NOT_COLLECTED, DC_UNREACHABLE …</summary>
    public string? SourceReason { get; init; }
    public int? LogonType { get; init; }
    public IReadOnlyList<EvidenceRef> Evidence { get; init; } = [];
    public IReadOnlyList<PrincipalRef> Candidates { get; init; } = [];   // Ambiguous
    public DateTimeOffset? ResolvedAt { get; init; }

    public static Attribution Pending { get; } = new();
    public static Attribution NotApplicable { get; } = new() { Status = AttributionStatus.NotApplicable };
}
