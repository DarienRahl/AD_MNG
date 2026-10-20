namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>
/// GPO content tracking (stage B design 8): the service reads the GPO folders on SYSVOL of its DC read-only, parses the
/// settings and reports <c>GroupPolicySettingsChanged</c> / <c>GroupPolicyContentOutOfBand</c> /
/// <c>GroupPolicyPathRedirected</c>. Exceeding a limit stores the inventory, reads what fits and raises health Warning
/// <c>gpo.&lt;guid&gt;.tooLarge</c> (8.3). The smbclient path, its configuration file and the per-invocation timeout are
/// host settings (bootstrap <c>Watchdog:Sysvol</c>), not part of this record.
/// </summary>
public sealed record GpoContentSettings
{
    /// <summary>Fixed limit: folder depth below the GPO folder (8.3).</summary>
    public const int MaxDepth = 12;

    /// <summary>Fixed limit: settings per GPO (8.3).</summary>
    public const int MaxSettings = 10_000;

    public bool Enabled { get; init; } = true;

    /// <summary>While <c>GPT.INI</c> on the DC is older than the GPC <c>versionNumber</c>, the read is retried every 2 minutes
    /// up to this long, then done anyway with health Warning <c>gpo.&lt;guid&gt;.sysvolLag</c> (8.7).</summary>
    public TimeSpan SysvolLagTolerance { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Largest content file read (MiB).</summary>
    public int MaxFileMb { get; init; } = 5;

    /// <summary>Content read per GPO in total (MiB).</summary>
    public int MaxTotalMb { get; init; } = 50;

    /// <summary>Files per GPO.</summary>
    public int MaxFiles { get; init; } = 2000;
}
