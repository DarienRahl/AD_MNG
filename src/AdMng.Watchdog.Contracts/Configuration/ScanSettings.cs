namespace AdMng.Watchdog.Contracts.Configuration;

public sealed record AccountExpiryScan
{
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<int> ThresholdsDays { get; init; } = [14, 7, 1];
    public bool IncludeDisabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);
    /// <summary>A threshold whose day falls on a non-business day fires on the previous business day (10.14).</summary>
    public bool FireOnBusinessDays { get; init; } = true;
}

public enum PasswordExpiryScope { None, PrivilegedAndService, AllUsers }

public sealed record PasswordExpiryScan
{
    public PasswordExpiryScope Scope { get; init; } = PasswordExpiryScope.PrivilegedAndService;
    public IReadOnlyList<int> ThresholdsDays { get; init; } = [14, 7, 1];
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);
}

public sealed record InactiveAccountsScan
{
    public bool Enabled { get; init; } = true;
    public int Days { get; init; } = 90;              // ≥ 30 (lastLogonTimestamp lag)
    public int PrivilegedDays { get; init; } = 180;
    public int MinAgeDays { get; init; } = 30;        // ignore accounts younger than this
    public DayOfWeek ReportDay { get; init; } = DayOfWeek.Monday;
}

public sealed record BurstSettings
{
    public int Threshold { get; init; }
    public TimeSpan Window { get; init; }
    /// <summary>MassLockout only: effective threshold = max(Threshold, ceil(percent × enabled users / 100)).</summary>
    public double PercentOfEnabledUsers { get; init; }
}

public sealed record PasswordSprayDetection
{
    /// <summary>Off by default: needs Kerberos Authentication Service / Credential Validation failure auditing (docs 4.5).</summary>
    public bool Enabled { get; init; }
    public int DistinctAccounts { get; init; } = 20;               // per source IP / workstation
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Scheduled scans and burst detectors (section 10.13–10.17).</summary>
public sealed record ScanSettings
{
    public AccountExpiryScan AccountExpiry { get; init; } = new();
    public PasswordExpiryScan PasswordExpiry { get; init; } = new();
    public TimeSpan LockoutRecheckInterval { get; init; } = TimeSpan.FromMinutes(5);
    public InactiveAccountsScan InactiveAccounts { get; init; } = new();
    public BurstSettings MassLockout { get; init; } =
        new() { Threshold = 5, Window = TimeSpan.FromMinutes(10), PercentOfEnabledUsers = 0.5 };
    /// <summary>Counts deletions of users, groups, OUs and service accounts.</summary>
    public BurstSettings MassDeletion { get; init; } = new() { Threshold = 10, Window = TimeSpan.FromMinutes(10) };
    /// <summary>Counts deletions of computers and contacts (stale-computer clean-ups are routine).</summary>
    public BurstSettings MassComputerDeletion { get; init; } = new() { Threshold = 50, Window = TimeSpan.FromMinutes(10) };
    public PasswordSprayDetection PasswordSpray { get; init; } = new();
    public TimeSpan PermissionSelfCheckInterval { get; init; } = TimeSpan.FromHours(24);
}
