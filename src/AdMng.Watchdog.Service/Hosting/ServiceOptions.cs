namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Bootstrap settings (spec 8.4), bound from the "Watchdog" section of <c>appsettings.json</c>,
/// <c>%ProgramData%\AdMng\Watchdog\config\appsettings.json</c> and <c>ADMNG_WATCHDOG__…</c>. Editable only by local
/// administrators, never through the API. Collections default to empty (the configuration binder appends to non-empty
/// defaults); effective defaults are applied by the consumers (e.g. <see cref="NotificationBootstrapOptions.EffectiveOAuthSmtpHosts"/>).
/// </summary>
public sealed record ServiceOptions
{
    public const string SectionName = "Watchdog";

    /// <summary>Environment variables are expanded (<c>DataDirectory.Resolve</c>).</summary>
    public string DataDirectory { get; init; } = @"%ProgramData%\AdMng\Watchdog\data";

    /// <summary>Local-admin escape hatch: a data folder ACL outside the allow-list only raises health Warning <c>storage.acl</c>.</summary>
    public bool AllowInsecureDataDirectoryAcl { get; init; }

    /// <summary>Linux: start although the service account is in a privileged group (otherwise exit code 8; at run time the
    /// account stays health Error <c>serviceAccount.privileged</c>) – stage B design 4.7 item 1.</summary>
    public bool AllowPrivilegedServiceAccount { get; init; }

    public WatchdogAuthenticationMode Authentication { get; init; } = WatchdogAuthenticationMode.Negotiate;

    public HostingOptions Hosting { get; init; } = new();

    public AuthorizationOptions Authorization { get; init; } = new();

    public NotificationBootstrapOptions Notifications { get; init; } = new();

    public DirectoryBootstrapOptions Directory { get; init; } = new();

    /// <summary>Linux: Kerberos identity of the service (stage B design 4.7, 6.2, 14.1).</summary>
    public KerberosBootstrapOptions Kerberos { get; init; } = new();

    /// <summary>Linux: OpenWEC output read by the service (stage B design 7.3–7.6, 14.1).</summary>
    public CollectorBootstrapOptions Collector { get; init; } = new();

    /// <summary>Root-owned retention floor (stage B design 14.2).</summary>
    public RetentionBootstrapOptions Retention { get; init; } = new();

    /// <summary>Linux: SYSVOL reading through smbclient (stage B design 8.3, 14.1).</summary>
    public SysvolBootstrapOptions Sysvol { get; init; } = new();

    public DevelopmentOptions Development { get; init; } = new();
}
