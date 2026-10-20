namespace AdMng.Watchdog.Contracts.Api;

/// <summary>
/// Status section „Konto usługi” (stage B design 4.7, 18 item 2): the service's own computer account (e.g.
/// <c>ADMNG-WD$</c>) and its keytab. The checks themselves are health checks <c>serviceAccount.*</c>
/// (<see cref="HealthCheckCodes"/>) in <see cref="StatusDto.Checks"/>; this record holds the values shown next to them.
/// No secrets: kvnos, times, enctypes and flags only.
/// </summary>
public sealed record ServiceAccountStatusDto
{
    public required HealthState State { get; init; }

    /// <summary><c>Watchdog:Kerberos:ServicePrincipal</c>, e.g. <c>ADMNG-WD$@CONTOSO.COM</c>.</summary>
    public string? Principal { get; init; }

    /// <summary>Distinguished name of the account (resolved by sAMAccountName).</summary>
    public string? AccountDn { get; init; }

    /// <summary>Newest key version number in the keytab.</summary>
    public int? KeytabKvno { get; init; }

    /// <summary><c>msDS-KeyVersionNumber</c> of the account in AD (equal to <see cref="KeytabKvno"/>, or one more while
    /// the keytab tool reports <see cref="KeytabStatusResults.ReplicationPending"/>).</summary>
    public int? DirectoryKvno { get; init; }

    /// <summary><c>pwdLastSet</c> of the account (Warning after 32 days, Error after 35 – <c>serviceAccount.passwordAge</c>).</summary>
    public DateTimeOffset? PasswordLastSetAt { get; init; }

    /// <summary><c>status.json</c> <c>lastChange</c> of the keytab tool.</summary>
    public DateTimeOffset? LastRotationAt { get; init; }

    /// <summary><c>status.json</c> <c>lastAttempt</c>.</summary>
    public DateTimeOffset? LastRotationAttemptAt { get; init; }

    /// <summary><c>status.json</c> <c>nextDue</c>.</summary>
    public DateTimeOffset? NextRotationDueAt { get; init; }

    /// <summary><c>status.json</c> <c>result</c> – one of <see cref="KeytabStatusResults"/>; null when the file is missing.</summary>
    public string? RotationResult { get; init; }

    /// <summary><c>status.json</c> <c>errorCode</c> (a krb5 error name, never a message).</summary>
    public string? RotationErrorCode { get; init; }

    /// <summary><c>msDS-SupportedEncryptionTypes</c> of the account (required 0x18 = AES128 + AES256).</summary>
    public int? SupportedEncryptionTypes { get; init; }

    /// <summary>Encryption types of the keytab entries, e.g. <c>aes256-cts-hmac-sha1-96</c>.</summary>
    public IReadOnlyList<string> KeytabEncryptionTypes { get; init; } = [];

    /// <summary><c>userAccountControl</c> has NOT_DELEGATED and no delegation bit.</summary>
    public bool? NotDelegated { get; init; }

    /// <summary><c>tokenGroups</c> of the account contains a privileged SID (4.7 item 1).</summary>
    public bool? Privileged { get; init; }

    /// <summary>The Deny ACE for SELF is on the account's own object (4.7 item 7).</summary>
    public bool? DenySelfAcePresent { get; init; }

    /// <summary>The first keytab entry is the account principal (4.4 entry order).</summary>
    public bool? KeytabOrderValid { get; init; }

    public DateTimeOffset? CheckedAt { get; init; }
}

/// <summary>Values of <c>result</c> in the keytab tool's <c>status.json</c> (stage B design 4.4) and of
/// <see cref="ServiceAccountStatusDto.RotationResult"/>; the GUI localizes <c>Watchdog.KeytabResult.{value}</c>.</summary>
public static class KeytabStatusResults
{
    public const string Ok = "ok";

    /// <summary>The keytab holds keys derived from the one-time password (the change was refused, e.g. minimum password age).</summary>
    public const string OtpActive = "otpActive";

    public const string ChangeFailed = "changeFailed";

    /// <summary>The password was changed but the keytab could not be written – health Error at once.</summary>
    public const string KeytabWriteFailed = "keytabWriteFailed";

    /// <summary>A DC still refuses the new key after the replication wait – not an error (<c>primary_kdc</c> covers it).</summary>
    public const string ReplicationPending = "replicationPending";

    public const string VerifyFailed = "verifyFailed";

    /// <summary>Every value, in the order of the design.</summary>
    public static IReadOnlyList<string> All { get; } = [Ok, OtpActive, ChangeFailed, KeytabWriteFailed, ReplicationPending, VerifyFailed];
}
