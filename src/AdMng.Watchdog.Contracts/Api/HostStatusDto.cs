namespace AdMng.Watchdog.Contracts.Api;

/// <summary>
/// Status section „Host” (stage B design 18 item 2): operating system, runtime, secrets-key variant (10), clock skew (4.7
/// item 6) and the LDAP security strength factor (5.1). Free space is <see cref="StorageStatusDto.FreeBytes"/>.
/// </summary>
public sealed record HostStatusDto
{
    /// <summary>Operating system, e.g. <c>Debian GNU/Linux 13 (trixie)</c>.</summary>
    public string? OperatingSystem { get; init; }

    /// <summary>.NET runtime, e.g. <c>.NET 10.0.13</c>.</summary>
    public string? Runtime { get; init; }

    /// <summary>How the Data Protection key credential is sealed – one of <see cref="HostCredentialKeys"/>; null when the
    /// service does not run with a systemd credential.</summary>
    public string? CredentialKey { get; init; }

    /// <summary>RootDSE <c>currentTime</c> minus local time (positive: the DC is ahead) at the last detection cycle; Warning
    /// above 60 s, Error above 240 s (<c>host.clockSkew</c>).</summary>
    public TimeSpan? ClockSkew { get; init; }

    /// <summary>Negotiated SASL security strength factor of the LDAP session (≥ 56 required with SignAndSeal; 0 over LDAPS).</summary>
    public int? LdapSsf { get; init; }

    /// <summary><c>Watchdog:Directory:Security</c> in effect: <c>SignAndSeal</c> (389, GSSAPI) or <c>Tls</c> (LDAPS).</summary>
    public string? LdapSecurity { get; init; }

    /// <summary>The process runs as a systemd unit (notify socket present).</summary>
    public bool Systemd { get; init; }
}

/// <summary>Variants of the systemd credential that protects the Data Protection key (stage B design 10); the GUI
/// localizes <c>Watchdog.CredentialKey.{value}</c>.</summary>
public static class HostCredentialKeys
{
    /// <summary>Sealed with the TPM2 and the host key (<c>systemd-creds encrypt --with-key=host+tpm2</c>).</summary>
    public const string Tpm2Host = "tpm2+host";

    /// <summary>Sealed with the host key <c>/var/lib/systemd/credential.secret</c> only.</summary>
    public const string Host = "host";

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } = [Tpm2Host, Host];
}
