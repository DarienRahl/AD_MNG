namespace AdMng.Watchdog.Service.Hosting;

/// <summary>The WEF collector the service reads (stage B design 7).</summary>
public enum CollectorKind
{
    /// <summary>OpenWEC (package admng-openwec) writing RawJson files per DC.</summary>
    OpenWec,
}

/// <summary>
/// Bootstrap <c>Watchdog:Collector</c> (Linux, stage B design 7.3–7.6, 14.1). A host setting – an API admin must not
/// redirect file reading (7.8). <c>print-wec-subscription</c> reads only this section (7.3).
/// </summary>
public sealed record CollectorBootstrapOptions
{
    /// <summary>The uuid of the shipped subscription <c>admng-dc-security</c>; it must equal the <c>uuid</c> of the loaded
    /// OpenWEC subscription and never changes (7.3).</summary>
    public static readonly Guid DefaultSubscriptionUuid = Guid.Parse("4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10");

    public const string DefaultOutputDirectory = "/var/lib/openwec/out";

    public const string DefaultFileName = "security.jsonl";

    public const string DefaultProgressFile = "/run/admng-watchdog/wec-progress.json";

    public CollectorKind Kind { get; init; } = CollectorKind.OpenWec;

    /// <summary>OpenWEC output root: one directory per client (<c>&lt;client&gt;/security.jsonl</c> and rotated files).</summary>
    public string OutputDirectory { get; init; } = DefaultOutputDirectory;

    /// <summary>Name of the active output file in each client directory.</summary>
    public string FileName { get; init; } = DefaultFileName;

    /// <summary><c>meta.Subscription.Uuid</c> every accepted record must carry (7.6 check 1).</summary>
    public Guid SubscriptionUuid { get; init; } = DefaultSubscriptionUuid;

    /// <summary>Consumption progress for <c>admng-wec-rotate</c> (7.4), rewritten after every commit.</summary>
    public string ProgressFile { get; init; } = DefaultProgressFile;

    /// <summary>Extra source addresses accepted for DC records (NAT, multi-homed DCs – 7.6 check 4).</summary>
    public string[] AdditionalSourceAddresses { get; init; } = [];

    /// <summary>Ingest budget of 4624/4625/4771/4776 per DC and minute; beyond it records are counted, not stored, and
    /// HealthEventLogGap "flooding" is raised (7.5).</summary>
    public int MaxLogonRecordsPerDcPerMinute { get; init; } = 20_000;
}
