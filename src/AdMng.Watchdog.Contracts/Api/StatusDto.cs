using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Contracts.Api;

/// <summary>State of the watched domain controller and the detection cycle.</summary>
public sealed record DirectoryStatusDto
{
    public required HealthState State { get; init; }
    public string? DcHostName { get; init; }
    public bool IsPdc { get; init; }
    public Guid? InvocationId { get; init; }
    public long HighestUsn { get; init; }
    public DateTimeOffset? LastPollAt { get; init; }
    public DateTimeOffset? LastChangeAt { get; init; }
    public DateTimeOffset? LastFullSyncAt { get; init; }
    public DateTimeOffset? BaselineCompletedAt { get; init; }
    public long SnapshotObjects { get; init; }
}

/// <summary>Security-log reading of one DC (attribution source).</summary>
public sealed record DcLogStatusDto
{
    public required string Host { get; init; }
    public required HealthState State { get; init; }
    public DateTimeOffset? LastReadAt { get; init; }
    public long? LastRecordId { get; init; }
    public double? Coverage24h { get; init; }   // share of attributable LDAP-detected changes on this DC attributed in the last 24 h
    public string? ErrorCode { get; init; }
    public int SourceLookups24h { get; init; }  // on-demand 4624/4771/4776 queries against this DC
    public TimeSpan? SourceLookupAverage { get; init; }
    public int SourceLookupTimeouts24h { get; init; }
}

public sealed record AttributionStatusDto
{
    public bool Enabled { get; init; }
    public string Source { get; init; } = "DomainControllers";
    public IReadOnlyList<DcLogStatusDto> DomainControllers { get; init; } = [];
}

public sealed record StorageStatusDto
{
    public required string DataDirectory { get; init; }
    public long DatabaseBytes { get; init; }
    public long FreeBytes { get; init; }
    public long EventCount { get; init; }
    public DateTimeOffset? OldestEventAt { get; init; }
    public DateTimeOffset? LastBackupAt { get; init; }

    /// <summary>A decreased <c>Retention.EventsDays</c> that is not in effect yet (stage B design 14.2, SHOULD: a decrease
    /// applies 7 days after the change); null when none is pending.</summary>
    public int? PendingEventsDays { get; init; }

    /// <summary>When <see cref="PendingEventsDays"/> takes effect.</summary>
    public DateTimeOffset? PendingEventsDaysFrom { get; init; }
}

public sealed record EventCountsDto
{
    public int Total { get; init; }
    public IReadOnlyDictionary<Severity, int> BySeverity { get; init; } = new Dictionary<Severity, int>();
    public int Unacknowledged { get; init; }
    public int UnacknowledgedHighOrAbove { get; init; }   // tab-header badge
}

/// <summary>A suggested (not applied) rule exclusion, e.g. a detected Entra Connect sync account for KeyCredentialChanged.
/// The GUI offers "Dodaj wyjątek", which goes through the normal PUT /config.</summary>
public sealed record ExclusionSuggestionDto
{
    public required string RuleId { get; init; }
    public required string ActorSid { get; init; }
    public string? ActorAccount { get; init; }
    public required string ReasonCode { get; init; }   // "entraConnectSync", "computerCleanupAccount" …
}

/// <summary><c>GET /api/v1/status</c>.</summary>
public sealed record StatusDto
{
    public required string ServiceVersion { get; init; }
    public int ApiVersion { get; init; } = WatchdogRoutes.ApiVersion;
    public required string InstanceName { get; init; }
    public string? Domain { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset Now { get; init; }
    public required DirectoryStatusDto Directory { get; init; }
    public required AttributionStatusDto Attribution { get; init; }
    public IReadOnlyList<HealthCheckDto> Checks { get; init; } = [];
    public IReadOnlyList<ChannelHealthDto> Channels { get; init; } = [];
    public required StorageStatusDto Storage { get; init; }
    public required EventCountsDto Last24Hours { get; init; }
    public DateTimeOffset? NextDigestAt { get; init; }
    /// <summary>End of rollout mode (12.5); null when not active.</summary>
    public DateTimeOffset? RolloutModeUntil { get; init; }
    public IReadOnlyList<ExclusionSuggestionDto> Suggestions { get; init; } = [];

    // Stage B sections (docs/dev/watchdog-linux-design.md 18 item 2) – null when the service does not report them (the
    // Windows service, an older service, or before the first check).

    /// <summary>„Konto usługi” – the own computer account and its keytab (4.7).</summary>
    public ServiceAccountStatusDto? ServiceAccount { get; init; }

    /// <summary>„Kolektor WEF” – OpenWEC ingestion per DC (7.9).</summary>
    public CollectorStatusDto? Collector { get; init; }

    /// <summary>„Zawartość GPO” – SYSVOL reading (8).</summary>
    public GpoContentStatusDto? GpoContent { get; init; }

    /// <summary>„Host” – the Linux server (10, 4.7 item 6, 5.1).</summary>
    public HostStatusDto? Host { get; init; }
}
