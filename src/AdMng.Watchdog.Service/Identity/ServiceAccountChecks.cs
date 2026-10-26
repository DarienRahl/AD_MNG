using System.Globalization;
using AdMng.Core.Security;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Service.Identity;

/// <summary>What the self-checks of design 4.7 read: the account in AD, the keytab and the keytab tool's status.</summary>
public sealed record ServiceAccountFacts
{
    /// <summary><c>Watchdog:Kerberos:ServicePrincipal</c>, e.g. <c>ADMNG-WD$@CONTOSO.COM</c>.</summary>
    public required string Principal { get; init; }

    public string? AccountDn { get; init; }

    /// <summary>The account was not found by its sAMAccountName.</summary>
    public bool AccountMissing { get; init; }

    public int? UserAccountControl { get; init; }

    public int? SupportedEncryptionTypes { get; init; }

    public IReadOnlyList<string> ServicePrincipalNames { get; init; } = [];

    public string? DnsHostName { get; init; }

    public DateTimeOffset? PasswordLastSet { get; init; }

    public int? KeyVersionNumber { get; init; }

    /// <summary>tokenGroups as SID strings; null when unreadable.</summary>
    public IReadOnlyList<string>? TokenGroups { get; init; }

    /// <summary>The SIDs among <see cref="TokenGroups"/> that are privileged (computed by the monitor).</summary>
    public IReadOnlyList<string> PrivilegedSids { get; init; } = [];

    /// <summary>DACL of the own object; null when unreadable.</summary>
    public IReadOnlyList<SecurityDescriptorAce>? Dacl { get; init; }

    /// <summary>Keytab entries in file order; null when unreadable (the error is in <see cref="KeytabError"/>).</summary>
    public IReadOnlyList<KeytabEntry>? Keytab { get; init; }

    public string? KeytabError { get; init; }

    /// <summary>status.json of the keytab tool; null when missing.</summary>
    public KeytabToolStatus? ToolStatus { get; init; }

    public string? ToolStatusError { get; init; }
}

/// <summary>The result of one evaluation: the health checks <c>serviceAccount.*</c> and the Status section.</summary>
public sealed record ServiceAccountEvaluation(IReadOnlyList<HealthCheckDto> Checks, ServiceAccountStatusDto Status);

/// <summary>
/// The pure part of the self-checks of design 4.7 and the keytab health of 4.6: privileged groups, delegation bits,
/// enctypes, SPNs and <c>dNSHostName</c>, the Deny ACE for SELF, password age, keytab kvno and order, and the keytab tool's
/// status. Every check reports Ok explicitly so a fixed problem clears its health entry. Values in Args are attribute
/// names, codes, kvnos and day counts only.
/// </summary>
public static class ServiceAccountChecks
{
    public const int NotDelegated = 0x100000;
    public const int TrustedForDelegation = 0x80000;
    public const int TrustedToAuthForDelegation = 0x1000000;

    /// <summary>AES128 + AES256 (design 4.1).</summary>
    public const int RequiredEncryptionTypes = 0x18;

    /// <summary>CreateChild | DeleteChild | Self | WriteProperty – the minimum mask of the Deny ACE for SELF (4.7 item 7).</summary>
    public const int DenySelfMask = 0x2B;

    public const string SelfSid = "S-1-5-10";

    public static readonly TimeSpan PasswordAgeWarning = TimeSpan.FromDays(32);
    public static readonly TimeSpan PasswordAgeError = TimeSpan.FromDays(35);

    /// <summary>Evaluates the facts at <paramref name="now"/>.</summary>
    public static ServiceAccountEvaluation Evaluate(ServiceAccountFacts facts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var checks = new List<HealthCheckDto>();
        void Add(string code, HealthState state, string detail, params string[] args) =>
            checks.Add(new HealthCheckDto { Code = code, State = state, Detail = detail, Args = args, CheckedAt = now });

        var shortName = ShortName(facts.Principal);
        var keytabKvno = NewestKvno(facts.Keytab, facts.Principal);
        var fqdn = HostOf(facts.Keytab, "HTTP/") ?? HostOf(facts.Keytab, "host/");
        var replicationPending = string.Equals(facts.ToolStatus?.Result, KeytabStatusResults.ReplicationPending, StringComparison.Ordinal);

        // 1. privileged groups
        if (facts.AccountMissing)
        {
            Add(HealthCheckCodes.ServiceAccountDrift, HealthState.Error, "The service account was not found in the directory.", "accountMissing");
        }
        else if (facts.TokenGroups is null)
        {
            Add(HealthCheckCodes.ServiceAccountPrivileged, HealthState.Warning, "tokenGroups of the service account is not readable.", "tokenGroupsUnreadable");
        }
        else
        {
            Add(
                HealthCheckCodes.ServiceAccountPrivileged,
                facts.PrivilegedSids.Count > 0 ? HealthState.Error : HealthState.Ok,
                facts.PrivilegedSids.Count > 0 ? "The service account is a member of a privileged group." : "The service account is in no privileged group.",
                facts.PrivilegedSids.Count.ToString(CultureInfo.InvariantCulture));
        }

        // 2. + 7. drift (delegation, enctypes, SPNs, dNSHostName, Deny ACE for SELF)
        if (!facts.AccountMissing)
        {
            var drift = new List<string>();
            if (facts.UserAccountControl is not { } uac
                || (uac & NotDelegated) == 0 || (uac & (TrustedForDelegation | TrustedToAuthForDelegation)) != 0)
            {
                drift.Add("userAccountControl");
            }

            if (facts.SupportedEncryptionTypes != RequiredEncryptionTypes)
            {
                drift.Add("msDS-SupportedEncryptionTypes");
            }

            if (fqdn is not null)
            {
                var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HTTP/" + fqdn, "HOST/" + fqdn, "HOST/" + shortName };
                var actual = facts.ServicePrincipalNames
                    .Where(s => !s.StartsWith("RestrictedKrbHost/", StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!actual.SetEquals(expected))
                {
                    drift.Add("servicePrincipalName");
                }

                if (!string.Equals(facts.DnsHostName?.TrimEnd('.'), fqdn, StringComparison.OrdinalIgnoreCase))
                {
                    drift.Add("dNSHostName");
                }
            }

            var denySelf = facts.Dacl is { } dacl && HasDenySelfAce(dacl);
            if (facts.Dacl is not null && !denySelf)
            {
                drift.Add(HealthCheckCodes.DenySelfAceMissing);
            }

            Add(
                HealthCheckCodes.ServiceAccountDrift,
                drift.Count > 0 ? HealthState.Warning : HealthState.Ok,
                drift.Count > 0 ? "The service account differs from the required settings." : "The service account has the required settings.",
                [.. drift]);
        }

        // 3. password age
        if (facts.PasswordLastSet is { } pwdLastSet)
        {
            var age = now - pwdLastSet;
            var days = ((int)Math.Floor(age.TotalDays)).ToString(CultureInfo.InvariantCulture);
            var state = age > PasswordAgeError ? HealthState.Error : age > PasswordAgeWarning ? HealthState.Warning : HealthState.Ok;
            Add(HealthCheckCodes.ServiceAccountPasswordAge, state, $"The service account password is {days} days old.", days);
        }

        // 4. keytab kvno vs msDS-KeyVersionNumber
        if (facts.Keytab is null)
        {
            Add(HealthCheckCodes.ServiceAccountKeytabStale, HealthState.Error, "The keytab is not readable.", facts.KeytabError ?? "unreadable");
        }
        else if (keytabKvno is null)
        {
            Add(HealthCheckCodes.ServiceAccountKeytabStale, HealthState.Error, "The keytab holds no key of the service account.", "noEntry");
        }
        else if (facts.KeyVersionNumber is { } directoryKvno)
        {
            var fresh = keytabKvno == directoryKvno || (replicationPending && keytabKvno == directoryKvno - 1);
            Add(
                HealthCheckCodes.ServiceAccountKeytabStale,
                fresh ? HealthState.Ok : HealthState.Error,
                fresh ? "The keytab holds the current key." : "The keytab's newest key version differs from the account's (msDS-KeyVersionNumber).",
                keytabKvno.Value.ToString(CultureInfo.InvariantCulture),
                directoryKvno.ToString(CultureInfo.InvariantCulture));
        }

        // 5. keytab order and the keytab tool's status
        bool? orderValid = null;
        if (facts.Keytab is { Count: > 0 } entries)
        {
            orderValid = string.Equals(entries[0].Principal, facts.Principal, StringComparison.OrdinalIgnoreCase);
            Add(
                HealthCheckCodes.ServiceAccountKeytabOrder,
                orderValid.Value ? HealthState.Ok : HealthState.Error,
                orderValid.Value ? "The keytab starts with the account principal." : "The first keytab entry is not the account principal.");
        }

        checks.Add(RotationCheck(facts, now));

        var worst = checks.Count == 0 ? HealthState.Ok : checks.Max(c => c.State);
        var status = new ServiceAccountStatusDto
        {
            State = worst,
            Principal = facts.Principal,
            AccountDn = facts.AccountDn,
            KeytabKvno = keytabKvno,
            DirectoryKvno = facts.KeyVersionNumber,
            PasswordLastSetAt = facts.PasswordLastSet,
            LastRotationAt = facts.ToolStatus?.LastChange,
            LastRotationAttemptAt = facts.ToolStatus?.LastAttempt,
            NextRotationDueAt = facts.ToolStatus?.NextDue,
            RotationResult = facts.ToolStatus?.Result,
            RotationErrorCode = facts.ToolStatus?.ErrorCode,
            SupportedEncryptionTypes = facts.SupportedEncryptionTypes,
            KeytabEncryptionTypes = facts.Keytab?.Select(e => e.EnctypeName).Distinct(StringComparer.Ordinal).ToList() ?? [],
            NotDelegated = facts.UserAccountControl is { } u ? (u & NotDelegated) != 0 && (u & (TrustedForDelegation | TrustedToAuthForDelegation)) == 0 : null,
            Privileged = facts.TokenGroups is null ? null : facts.PrivilegedSids.Count > 0,
            DenySelfAcePresent = facts.Dacl is null ? null : HasDenySelfAce(facts.Dacl),
            KeytabOrderValid = orderValid,
            CheckedAt = now,
        };
        return new ServiceAccountEvaluation(checks, status);
    }

    /// <summary>An explicit (not inherited) Deny ACE for SELF on the whole object whose mask covers <see cref="DenySelfMask"/>.</summary>
    public static bool HasDenySelfAce(IEnumerable<SecurityDescriptorAce> dacl)
    {
        ArgumentNullException.ThrowIfNull(dacl);
        return dacl.Any(a =>
            (a.Type == 0x01 || (a.Type == 0x06 && a.ObjectType is null))
            && (a.Flags & 0x10) == 0
            && string.Equals(a.Sid, SelfSid, StringComparison.OrdinalIgnoreCase)
            && (a.Mask & DenySelfMask) == DenySelfMask);
    }

    /// <summary><c>serviceAccount.rotation</c> from status.json (4.6): ok / replicationPending → Ok, changeFailed → Warning
    /// (Error once pwdLastSet is older than 35 days), keytabWriteFailed / otpActive / verifyFailed → Error.</summary>
    private static HealthCheckDto RotationCheck(ServiceAccountFacts facts, DateTimeOffset now)
    {
        var status = facts.ToolStatus;
        var result = status?.Result;
        var error = status?.ErrorCode;
        var (state, detail) = result switch
        {
            null when facts.ToolStatusError is not null => (HealthState.Warning, "The keytab tool's status file is not readable."),
            null => (HealthState.Warning, "The keytab tool has no status yet (run admng-watchdog-keytab init)."),
            KeytabStatusResults.Ok => (HealthState.Ok, "The last password rotation succeeded."),
            KeytabStatusResults.ReplicationPending => (HealthState.Ok, "The new key is not yet on every domain controller (primary_kdc covers it)."),
            KeytabStatusResults.ChangeFailed when facts.PasswordLastSet is { } set && now - set > PasswordAgeError =>
                (HealthState.Error, "The password rotation keeps failing and the password is older than 35 days."),
            KeytabStatusResults.ChangeFailed => (HealthState.Warning, "The last password rotation failed; the password and the keytab are unchanged."),
            KeytabStatusResults.KeytabWriteFailed => (HealthState.Error, "The password was changed but the keytab could not be written; a new one-time password and init are needed."),
            KeytabStatusResults.OtpActive => (HealthState.Error, "The keytab still holds the one-time password's keys; the daily timer retries the change."),
            KeytabStatusResults.VerifyFailed => (HealthState.Error, "The keytab failed its verification against the KDC."),
            _ => (HealthState.Warning, "The keytab tool reports an unknown result."),
        };
        string[] args = result is null ? [facts.ToolStatusError ?? "missing"] : error is null ? [result] : [result, error];
        return new HealthCheckDto { Code = HealthCheckCodes.ServiceAccountRotation, State = state, Detail = detail, Args = args, CheckedAt = now };
    }

    private static int? NewestKvno(IReadOnlyList<KeytabEntry>? keytab, string principal)
    {
        var kvnos = keytab?.Where(e => string.Equals(e.Principal, principal, StringComparison.OrdinalIgnoreCase)).Select(e => (int)e.Kvno).ToList();
        return kvnos is { Count: > 0 } ? kvnos.Max() : null;
    }

    private static string? HostOf(IReadOnlyList<KeytabEntry>? keytab, string service)
    {
        var principal = keytab?.Select(e => e.Principal).FirstOrDefault(p => p.StartsWith(service, StringComparison.OrdinalIgnoreCase));
        if (principal is null)
        {
            return null;
        }

        var host = principal[service.Length..];
        var at = host.LastIndexOf('@');
        return at > 0 ? host[..at] : host;
    }

    /// <summary><c>ADMNG-WD$@CONTOSO.COM</c> → <c>ADMNG-WD</c>.</summary>
    public static string ShortName(string principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var at = principal.LastIndexOf('@');
        var name = at > 0 ? principal[..at] : principal;
        return name.TrimEnd('$');
    }
}
