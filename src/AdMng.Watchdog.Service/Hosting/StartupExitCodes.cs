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

    /// <summary>Linux: the service account (<c>ADMNG-WD$</c>) is in a privileged group and
    /// <c>Watchdog:AllowPrivilegedServiceAccount</c> is not set (stage B design 4.7 item 1).</summary>
    public const int PrivilegedServiceAccount = 8;

    /// <summary>Linux: production start without the systemd credential <c>$CREDENTIALS_DIRECTORY/dp-key</c> (stage B
    /// design 10). systemd itself reports <c>243/CREDENTIALS</c> when the credential cannot be decrypted.</summary>
    public const int MissingCredential = 9;

    /// <summary>Linux: the keytab has the wrong owner, is writable by group/other or not readable by the service (stage B
    /// design 11 item 3).</summary>
    public const int Keytab = 10;
}
