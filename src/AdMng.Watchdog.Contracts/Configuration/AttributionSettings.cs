namespace AdMng.Watchdog.Contracts.Configuration;

public enum AttributionSourceKind { DomainControllers, ForwardedEvents, None }

/// <summary>Who/from where enrichment from DC Security logs (section 11).</summary>
public sealed record AttributionSettings
{
    public bool Enabled { get; init; } = true;
    public AttributionSourceKind Source { get; init; } = AttributionSourceKind.DomainControllers;
    public string? Collector { get; init; }                                  // WEF collector host for ForwardedEvents
    /// <summary>Empty = discover writable DCs of this domain; entries must be DCs known to DomainControllerDirectory.</summary>
    public IReadOnlyList<string> DomainControllers { get; init; } = [];
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MatchWindow { get; init; } = TimeSpan.FromSeconds(3);    // ± around originating time
    public TimeSpan FallbackWindow { get; init; } = TimeSpan.FromMinutes(2); // ± when no metadata
    public TimeSpan ReplicationLatencyBound { get; init; } = TimeSpan.FromMinutes(15);
    public IReadOnlyList<TimeSpan> PendingRetries { get; init; } =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];
    public bool LookupLogonSource { get; init; } = true;                     // 4624 by SubjectLogonId
    public bool LookupLockoutSources { get; init; } = true;                  // 4771/4776/4625 around lockouts
    /// <summary>Oldest 4624 considered (From bound of the backwards search; also capped by the log's oldest record).</summary>
    public TimeSpan LogonLookback { get; init; } = TimeSpan.FromHours(12);
    /// <summary>Timeout of one on-demand source query (4624/4771/4776); at most one such query per DC at a time.</summary>
    public TimeSpan SourceQueryTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Ingestion budget per source and poll; exceeding it raises HealthEventLogGap ("flooding").</summary>
    public int MaxRecordsPerPoll { get; init; } = 50_000;
    public bool ResolveReverseDns { get; init; } = true;
    public bool SuppressComputerSelfChanges { get; init; } = true;
}
