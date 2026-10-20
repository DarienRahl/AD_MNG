namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Bootstrap <c>Watchdog:Kerberos</c> (Linux, stage B design 4.7, 6.2, 14.1): the realm the API accepts principals of and
/// the service's own computer account. The keytab and krb5.conf come from the unit's environment (<c>KRB5_KTNAME</c>,
/// <c>KRB5_CLIENT_KTNAME</c>, <c>KRB5_CONFIG</c>), never from here.
/// </summary>
public sealed record KerberosBootstrapOptions
{
    /// <summary>The keytab of the package (root:admng-keytab 0640) – the unit's <c>KRB5_KTNAME</c>.</summary>
    public const string DefaultKeytabPath = "/etc/admng-watchdog/admng-wd.keytab";

    /// <summary>The keytab tool's status file (root:admng-watchdog 0640, no secrets).</summary>
    public const string DefaultKeytabStatusFile = "/var/lib/admng-watchdog-keytab/status.json";

    /// <summary>Kerberos realm, e.g. <c>CONTOSO.COM</c>: API principals of another realm get no SID claims (6.2 step 1).</summary>
    public string? Realm { get; init; }

    /// <summary>The service's own account principal, e.g. <c>ADMNG-WD$@CONTOSO.COM</c>: the account the self-checks of 4.7
    /// resolve (by sAMAccountName) and the principal the first keytab entry must carry (4.4).</summary>
    public string? ServicePrincipal { get; init; }

    /// <summary><c>status.json</c> of <c>admng-watchdog-keytab</c> (4.4), read for the rotation checks.</summary>
    public string KeytabStatusFile { get; init; } = DefaultKeytabStatusFile;
}
