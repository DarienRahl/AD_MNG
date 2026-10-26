namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Bootstrap settings (spec 8.4), bound from the "Watchdog" section of <c>appsettings.json</c>, the bootstrap file
/// (<see cref="ConfigurationSetup.BootstrapFilePath"/>: <c>/etc/admng-watchdog/appsettings.json</c> on Linux,
/// <c>%ProgramData%\AdMng\Watchdog\config\appsettings.json</c> on Windows) and <c>ADMNG_WATCHDOG__…</c>. Editable only by
/// root / local administrators, never through the API. Collections default to empty (the configuration binder appends to non-empty
/// defaults); effective defaults are applied by the consumers (e.g. <see cref="NotificationBootstrapOptions.EffectiveOAuthSmtpHosts"/>).
/// </summary>
public sealed record ServiceOptions
{
    public const string SectionName = "Watchdog";

    /// <summary>The data folder of the Linux package (stage B design 11).</summary>
    public const string DefaultLinuxDataDirectory = "/var/lib/admng-watchdog";

    /// <summary>The data folder of the 0.2.0 Windows service (spec 19.5).</summary>
    public const string DefaultWindowsDataDirectory = @"%ProgramData%\AdMng\Watchdog\data";

    /// <summary>Environment variables are expanded (<c>DataDirectory.Resolve</c>). Defaults to
    /// <see cref="DefaultLinuxDataDirectory"/> on Linux and <see cref="DefaultWindowsDataDirectory"/> elsewhere.</summary>
    public string DataDirectory { get; init; } = OperatingSystem.IsLinux() ? DefaultLinuxDataDirectory : DefaultWindowsDataDirectory;

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
