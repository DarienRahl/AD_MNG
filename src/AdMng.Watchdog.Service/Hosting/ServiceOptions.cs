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

    public WatchdogAuthenticationMode Authentication { get; init; } = WatchdogAuthenticationMode.Negotiate;

    public HostingOptions Hosting { get; init; } = new();

    public AuthorizationOptions Authorization { get; init; } = new();

    public NotificationBootstrapOptions Notifications { get; init; } = new();

    public DirectoryBootstrapOptions Directory { get; init; } = new();

    public DevelopmentOptions Development { get; init; } = new();
}
