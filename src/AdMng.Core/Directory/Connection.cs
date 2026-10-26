namespace AdMng.Core.Directory;

/// <summary>How the operator authenticates to the directory.</summary>
public enum AuthenticationMode
{
    /// <summary>The Windows identity of the process (Kerberos/NTLM via Negotiate, no password typed).</summary>
    CurrentUser,

    /// <summary>Explicit credentials typed by the operator (Negotiate - Kerberos preferred, NTLM fallback).</summary>
    Credentials,

    /// <summary>
    /// LDAP simple bind. Only allowed together with <see cref="TransportSecurity.Tls"/> or for the
    /// development test lab (explicit opt-in); never over clear text in production.
    /// </summary>
    SimpleBind,
}

/// <summary>Protection of the LDAP channel.</summary>
public enum TransportSecurity
{
    /// <summary>Kerberos/NTLM signing + sealing on port 389 (AD default, works with LDAP signing enforcement).</summary>
    SignAndSeal,

    /// <summary>LDAPS (TLS on 636 / 3269) with server certificate validation.</summary>
    Tls,

    /// <summary>StartTLS upgrade on port 389.</summary>
    StartTls,

    /// <summary>No protection. Development lab only - password operations will be refused by AD.</summary>
    None,
}

/// <summary>Parameters of a connection attempt. Passwords are passed separately and never stored.</summary>
public sealed record ConnectionOptions
{
    /// <summary>DNS name of the domain, e.g. <c>contoso.com</c>. Null = the computer's domain.</summary>
    public string? Domain { get; init; }

    /// <summary>A specific domain controller. Null = locate one (site aware on Windows).</summary>
    public string? Server { get; init; }

    /// <summary>TCP port; null = default for <see cref="Security"/> (389 / 636) or global catalog (3268 / 3269).</summary>
    public int? Port { get; init; }

    /// <summary>Connect to the global catalog for forest-wide, read-only searches.</summary>
    public bool GlobalCatalog { get; init; }

    public AuthenticationMode Authentication { get; init; } = AuthenticationMode.CurrentUser;

    public TransportSecurity Security { get; init; } = TransportSecurity.SignAndSeal;

    /// <summary>User name for <see cref="AuthenticationMode.Credentials"/>: <c>DOMAIN\user</c>, <c>user@domain</c> or <c>user</c>.</summary>
    public string? UserName { get; init; }

    /// <summary>Per-request timeout.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Connect timeout (DC unreachable detection).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Number of pooled LDAP connections to the pinned DC (parallel reads / batch writes).</summary>
    public int MaxConnections { get; init; } = 4;

    /// <summary>Lab only: accept any TLS certificate. Never enable in production.</summary>
    public bool AllowUntrustedCertificate { get; init; }

    /// <summary>
    /// Lab only: permit <see cref="AuthenticationMode.SimpleBind"/> over an unprotected channel
    /// (<see cref="TransportSecurity.None"/>). Without it such a combination is refused before any packet is sent.
    /// </summary>
    public bool AllowInsecureSimpleBind { get; init; }

    /// <summary>Port that will be used when <see cref="Port"/> is not set.</summary>
    public int EffectivePort => Port ?? (GlobalCatalog
        ? (Security == TransportSecurity.Tls ? 3269 : 3268)
        : (Security == TransportSecurity.Tls ? 636 : 389));
}

/// <summary>A domain controller candidate for the connection dialog.</summary>
public sealed record DomainControllerInfo(
    string HostName,
    string? Site,
    bool IsGlobalCatalog,
    bool IsReadOnly,
    bool IsInClientSite);

/// <summary>Facts about the bound connection, read from RootDSE and the bound identity.</summary>
public sealed record SessionInfo
{
    /// <summary>DNS host name of the DC all operations of this session are pinned to.</summary>
    public required string Server { get; init; }

    public required int Port { get; init; }

    /// <summary>DNS domain name, e.g. contoso.com.</summary>
    public required string DomainDnsName { get; init; }

    /// <summary>NetBIOS domain name when known (e.g. CONTOSO).</summary>
    public string? DomainNetBiosName { get; init; }

    public required string DefaultNamingContext { get; init; }

    public required string ConfigurationNamingContext { get; init; }

    public required string SchemaNamingContext { get; init; }

    public string? RootDomainNamingContext { get; init; }

    /// <summary>Who we are bound as, e.g. CONTOSO\jan.kowalski (msDS-PrincipalName / whoami extended op).</summary>
    public required string BoundIdentity { get; init; }

    /// <summary>DN of the bound account when resolvable.</summary>
    public string? BoundUserDn { get; init; }

    public AuthenticationMode Authentication { get; init; }

    public TransportSecurity Security { get; init; }

    /// <summary>
    /// Negotiated SASL security strength factor of a Kerberos (GSSAPI) session on Linux/macOS: ≥ 56 = sealed, 0 = no SASL
    /// security layer (e.g. over LDAPS). Null where it is not reported (Windows SSPI, simple bind).
    /// </summary>
    public int? SecurityStrengthFactor { get; init; }

    public bool IsGlobalCatalog { get; init; }

    public int DomainFunctionality { get; init; }

    public int ForestFunctionality { get; init; }

    public IReadOnlySet<string> SupportedControls { get; init; } = new HashSet<string>();

    /// <summary>AD Recycle Bin optional feature enabled in the forest.</summary>
    public bool RecycleBinEnabled { get; init; }

    /// <summary>Exchange schema extension detected (msExch* attributes available).</summary>
    public bool HasExchangeSchema { get; init; }

    /// <summary>Windows LAPS / legacy LAPS schema detected.</summary>
    public bool HasLapsSchema { get; init; }

    public DateTimeOffset ConnectedAt { get; init; }

    public bool Supports(string controlOid) => SupportedControls.Contains(controlOid);
}
