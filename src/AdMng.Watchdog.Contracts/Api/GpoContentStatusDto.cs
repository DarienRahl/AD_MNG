namespace AdMng.Watchdog.Contracts.Api;

/// <summary>
/// Status section „Zawartość GPO” (stage B design 8, 18 item 2): baseline progress, last read, failures, SYSVOL lag. Per
/// GPO problems are health checks <c>gpo.&lt;guid&gt;.*</c> and <c>sysvol.*</c> (<see cref="HealthCheckCodes"/>).
/// </summary>
public sealed record GpoContentStatusDto
{
    public required HealthState State { get; init; }

    /// <summary><c>Directory.GpoContent.Enabled</c> and bootstrap <c>Watchdog:Sysvol:Enabled</c>.</summary>
    public bool Enabled { get; init; }

    /// <summary>DC whose SYSVOL is read (the session DC).</summary>
    public string? Dc { get; init; }

    /// <summary>GPOs (groupPolicyContainer objects) in the domain.</summary>
    public int TotalGpos { get; init; }

    /// <summary>GPOs with a stored snapshot – the baseline progress is <see cref="ReadGpos"/> of <see cref="TotalGpos"/>.</summary>
    public int ReadGpos { get; init; }

    /// <summary>When every GPO had a snapshot for the first time.</summary>
    public DateTimeOffset? BaselineCompletedAt { get; init; }

    /// <summary>GPOs waiting in the read queue.</summary>
    public int QueuedGpos { get; init; }

    public DateTimeOffset? LastReadAt { get; init; }

    /// <summary>Last daily reconciliation (listing comparison, out-of-band edits).</summary>
    public DateTimeOffset? LastReconciliationAt { get; init; }

    /// <summary>GPOs whose last read failed (parse error, limits, access denied).</summary>
    public int FailedGpos { get; init; }

    /// <summary>GPOs whose <c>GPT.INI</c> on the DC is older than the GPC version (SYSVOL replication behind).</summary>
    public int SysvolLaggingGpos { get; init; }
}
