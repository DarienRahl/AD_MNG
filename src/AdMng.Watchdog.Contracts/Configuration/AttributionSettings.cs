namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>Where the DC Security events come from (stored as the name; the numbers are stable).</summary>
public enum AttributionSourceKind
{
    /// <summary>Windows service only: RPC reads of every DC's Security log (removed after 0.3.0, kept for stored configurations).</summary>
    DomainControllers = 0,

    /// <summary>Windows service only: the ForwardedEvents log of a Windows collector (kept for stored configurations).</summary>
    ForwardedEvents = 1,

    None = 2,

    /// <summary>Linux service: Windows Event Forwarding to OpenWEC on the Watchdog server, files tailed by the service (stage B
    /// design 7.8) – the default.</summary>
    OpenWec = 3,
}

/// <summary>Who/from where enrichment from DC Security logs (section 11; collector mode: stage B design 7.8).</summary>
public sealed record AttributionSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary><see cref="AttributionSourceKind.OpenWec"/> by default (stage B design 14.2); on Linux the validator rejects
    /// DomainControllers and ForwardedEvents (<c>sourceUnsupportedOnHost</c>) and the configuration migration to schema
    /// version 2 rewrites them once.</summary>
    public AttributionSourceKind Source { get; init; } = AttributionSourceKind.OpenWec;
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

    /// <summary>Collector mode: 4624 of computer accounts (TargetUserName ending in '$') are dropped at ingestion (7.7).</summary>
    public bool DropComputerLogons { get; init; } = true;

    /// <summary>Collector mode: 4624 of ANONYMOUS LOGON (S-1-5-7) are dropped at ingestion (7.7).</summary>
    public bool DropAnonymousLogons { get; init; } = true;

    /// <summary>Collector mode: no record from a DC for this long → health Warning <c>wec.&lt;dc&gt;.silent</c> (7.9).</summary>
    public TimeSpan CollectorSilenceWarning { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Collector mode: no record from a DC for this long → health Error <c>wec.&lt;dc&gt;.silent</c> and
    /// HealthAttributionDegraded (7.9).</summary>
    public TimeSpan CollectorSilenceError { get; init; } = TimeSpan.FromMinutes(60);
}
