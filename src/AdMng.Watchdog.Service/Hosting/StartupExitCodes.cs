namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Process exit codes of failed start-up checks (spec 19.1, 19.5) – documented for SCM recovery and monitoring.</summary>
public static class StartupExitCodes
{
    /// <summary>Unexpected fatal error (also the <see cref="FatalErrorHandler"/> path).</summary>
    public const int Fatal = 1;

    /// <summary>Invalid command line or bootstrap settings (<c>--check-config</c> with errors).</summary>
    public const int InvalidSettings = 2;

    /// <summary>The database was written by a newer version, or a migration failed.</summary>
    public const int Migration = 3;

    /// <summary>No usable TLS certificate (missing, no private key access) or a plain-HTTP listener off loopback.</summary>
    public const int Certificate = 4;

    /// <summary>The data folder ACL is outside the allow-list, or its keys cannot be protected.</summary>
    public const int DataDirectory = 5;

    /// <summary>The service identity can write its bootstrap configuration file.</summary>
    public const int BootstrapFile = 6;

    /// <summary>Development authentication requested where it is never allowed.</summary>
    public const int DevelopmentAuthentication = 7;
}
