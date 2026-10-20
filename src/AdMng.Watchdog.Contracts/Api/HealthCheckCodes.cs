using System.Globalization;

namespace AdMng.Watchdog.Contracts.Api;

/// <summary>
/// Health check codes (<see cref="HealthCheckDto.Code"/>) of the Linux service, stage B (docs/dev/watchdog-linux-design.md
/// 4.6, 4.7, 5.1, 6.2, 7.5, 7.9, 8.3, 8.4, 8.7, 9.2, 10, 11, 14.2). The GUI localizes <c>Watchdog.Health.{code}</c> and
/// <c>Watchdog.Health.{code}.Remedy</c> by the longest known prefix; codes with a subject are built by <see cref="Wec"/> and
/// <see cref="Gpo"/> as <c>&lt;area&gt;.&lt;subject&gt;.&lt;check&gt;</c> and resolved by their first and last segment
/// (<c>wec.dc01.contoso.com.silent</c> → <c>wec.silent</c>, subject <c>dc01.contoso.com</c>). Health codes of v1 (spec 19,
/// e.g. <c>storage.free</c>, <c>certificate.key</c>, <c>secret.unreadable</c>) keep their constants where they are reported.
/// </summary>
public static class HealthCheckCodes
{
    // ---- service account (4.6, 4.7) – raise HealthServiceAccount ------------------------------------------------------

    /// <summary>Keytab tool <c>status.json</c>: <c>changeFailed</c> → Warning; Error for <c>keytabWriteFailed</c>,
    /// <c>otpActive</c>, <c>verifyFailed</c> or when <c>pwdLastSet</c> is older than 35 days.</summary>
    public const string ServiceAccountRotation = "serviceAccount.rotation";

    /// <summary><c>pwdLastSet</c> age: Warning above 32 days, Error above 35 days.</summary>
    public const string ServiceAccountPasswordAge = "serviceAccount.passwordAge";

    /// <summary>Error: newest keytab kvno ≠ <c>msDS-KeyVersionNumber</c> (and not one less while replication is pending).</summary>
    public const string ServiceAccountKeytabStale = "serviceAccount.keytabStale";

    /// <summary>Error: the first keytab entry is not the account principal (4.4).</summary>
    public const string ServiceAccountKeytabOrder = "serviceAccount.keytabOrder";

    /// <summary>Error: <c>tokenGroups</c> of the account holds a privileged SID (at start: exit code 8 instead).</summary>
    public const string ServiceAccountPrivileged = "serviceAccount.privileged";

    /// <summary>Warning: delegation bits, enctypes, SPNs or <c>dNSHostName</c> differ from 4.1 (Args: the attribute names), or
    /// the Deny ACE for SELF is missing (Args: <see cref="DenySelfAceMissing"/>).</summary>
    public const string ServiceAccountDrift = "serviceAccount.drift";

    /// <summary>Args value of <see cref="ServiceAccountDrift"/> when the Deny ACE for SELF on the own object is missing (4.7 item 7).</summary>
    public const string DenySelfAceMissing = "denySelfAceMissing";

    // ---- host (4.7 item 6, 4.8, 10, 11) --------------------------------------------------------------------------------

    /// <summary>RootDSE <c>currentTime</c> vs. local time: Warning above 60 s, Error above 240 s.</summary>
    public const string HostClockSkew = "host.clockSkew";

    /// <summary>Warning: <c>primary_kdc</c> of the service's krb5.conf is not the PDC emulator (<c>fSMORoleOwner</c>).</summary>
    public const string HostPrimaryKdcNotPdc = "host.primaryKdcNotPdc";

    /// <summary>Ok (informational): the variant of the Data Protection key credential (Args: one of
    /// <see cref="HostCredentialKeys"/>).</summary>
    public const string HostCredentialKey = "host.credentialKey";

    /// <summary>Warning of the start-up permission checks (11 items 3 and 6): process umask not 0077, core dumps enabled, or
    /// <c>/etc/admng-watchdog/credentials</c> readable by the service (Args: <c>umask</c>, <c>coreDumps</c>,
    /// <c>credentialsReadable</c>).</summary>
    public const string HostHardening = "host.hardening";

    // ---- directory and permissions (5.1, 6.2) --------------------------------------------------------------------------

    /// <summary>Error: the LDAP bind negotiated an SSF below 56 (the session is refused and reconnected).</summary>
    public const string DirectoryNotSealed = "directory.notSealed";

    /// <summary>Permission self-check: <c>tokenGroups</c> of a non-protected account is not readable (API role resolution).</summary>
    public const string PermissionTokenGroupsUnreadable = "permission.tokenGroupsUnreadable";

    // ---- WEF collector (7.5, 7.9): "wec.<dc>.<check>" built by Wec(dc, check) -------------------------------------------

    /// <summary>Prefix of the per-DC collector codes.</summary>
    public const string WecPrefix = "wec.";

    /// <summary>No record from the DC for <c>Attribution.CollectorSilenceWarning</c> / <c>…Error</c> (+ HealthAttributionDegraded).</summary>
    public const string WecSilent = "silent";

    /// <summary>Error: a writable DC of the domain never delivered.</summary>
    public const string WecNotForwarding = "notForwarding";

    /// <summary>Warning: median <c>TimeReceived − TimeCreated</c> above 5 minutes over 1 hour.</summary>
    public const string WecLag = "lag";

    /// <summary>Error: records rejected by the identity checks of 7.6 (the rogue-forwarder signal).</summary>
    public const string WecIdentityMismatch = "identityMismatch";

    /// <summary>Warning: malformed or oversized lines in the last 24 hours.</summary>
    public const string WecMalformedLines = "malformedLines";

    /// <summary>Warning: unconsumed bytes above 256 MiB or a rotated file unconsumed for more than 1 hour.</summary>
    public const string WecBacklog = "backlog";

    /// <summary>Error (+ HealthEventLogGap reason <c>collectorFileLost</c>): the cursor's file is gone (7.5).</summary>
    public const string WecCollectorFileLost = "collectorFileLost";

    /// <summary>Error: no file written by any DC for 10 minutes while the DCs are reachable over LDAP (openwecd down).</summary>
    public const string WecService = "wec.service";

    // ---- GPO content (8.3, 8.4, 8.7): "gpo.<guid>.<check>" built by Gpo(guid, check) -------------------------------------

    /// <summary>Prefix of the per-GPO codes.</summary>
    public const string GpoPrefix = "gpo.";

    /// <summary>Warning: a read limit of <c>Directory.GpoContent</c> was exceeded; the inventory is kept.</summary>
    public const string GpoTooLarge = "tooLarge";

    /// <summary>Warning: a file of the GPO could not be parsed, or the GPO was skipped after 3 failed attempts.</summary>
    public const string GpoParseError = "parseError";

    /// <summary>Warning: <c>GPT.INI</c> stayed older than the GPC version for <c>GpoContent.SysvolLagTolerance</c>.</summary>
    public const string GpoSysvolLag = "sysvolLag";

    /// <summary>HealthPermissionGap: smbclient answered <c>NT_STATUS_ACCESS_DENIED</c> for a GPO folder (8.3).</summary>
    public const string SysvolAccessDenied = "sysvol.accessDenied";

    // ---- domain and storage (9.2, 14.2) ------------------------------------------------------------------------------------

    /// <summary>Warning while <c>ms-DS-MachineAccountQuota</c> of the domain head is above 0.</summary>
    public const string DomainMachineAccountQuota = "domain.machineAccountQuota";

    /// <summary>Error: the nightly backup was skipped – free space below 1.5 × the database size.</summary>
    public const string StorageBackupSkipped = "storage.backupSkipped";

    /// <summary><c>wec.&lt;dc&gt;.&lt;check&gt;</c> for one DC (host name, lower-cased) and one of the <c>Wec…</c> checks.</summary>
    /// <exception cref="ArgumentException">The DC is not a host name or the check is empty or contains a dot.</exception>
    public static string Wec(string dc, string check)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dc);
        var host = dc.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || !host.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
        {
            throw new ArgumentException("The DC must be a DNS host name.", nameof(dc));
        }

        return WecPrefix + host + "." + Check(check);
    }

    /// <summary><c>gpo.&lt;guid&gt;.&lt;check&gt;</c> for one GPO (GUID in "D" format) and one of the <c>Gpo…</c> checks.</summary>
    /// <exception cref="ArgumentException">The check is empty or contains a dot.</exception>
    public static string Gpo(Guid gpo, string check) =>
        GpoPrefix + gpo.ToString("D", CultureInfo.InvariantCulture) + "." + Check(check);

    private static string Check(string check)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(check);
        return check.Contains('.', StringComparison.Ordinal)
            ? throw new ArgumentException("A check name has no dot.", nameof(check))
            : check;
    }
}
