using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// Attribute display names (<c>Attr.{ldapName}</c>, Polish like the Polish ADUC) and value rendering for
/// notifications (section 9.4 display column): FILETIME and GeneralizedTime as local date-times (0 / never =
/// "nigdy"), <c>accountExpires</c> as the last valid day, booleans localized, DN values as their RDN value, flag
/// changes with their localized flag names (<c>Flag.{NAME}</c>), <c>logonHours</c> as a weekly summary, hashes and
/// binaries as counts. Values are expected to be sanitized already (<see cref="DetailLevelFilter.ForNotification"/>).
/// </summary>
public sealed class AttributeDisplay
{
    /// <summary>Every attribute with a display name in the resources (the watched attributes of section 9.2, the
    /// secret password attribute and the pseudo attributes of health and summary events).</summary>
    public static IReadOnlyList<string> KnownAttributes { get; } =
    [
        // users, service accounts, computers
        "sAMAccountName", "userPrincipalName", "displayName", "givenName", "sn", "initials", "cn", "name", "description", "mail",
        "proxyAddresses", "title", "department", "company", "manager", "physicalDeliveryOfficeName", "telephoneNumber", "mobile",
        "employeeID", "employeeNumber", "employeeType", "info", "userAccountControl", "accountExpires", "pwdLastSet", "lockoutTime",
        "primaryGroupID", "homeDirectory", "homeDrive", "scriptPath", "profilePath", "logonHours", "userWorkstations",
        "servicePrincipalName", "msDS-AllowedToDelegateTo", "msDS-AllowedToActOnBehalfOfOtherIdentity", "sIDHistory", "adminCount",
        "altSecurityIdentities", "msDS-KeyCredentialLink", "msDS-SupportedEncryptionTypes", "userCertificate",
        "msDS-SupersededManagedAccountLink", "msDS-SupersededServiceAccountState", "msDS-GroupMSAMembership",
        "msDS-ManagedAccountPrecededByLink", "msDS-DelegatedMSAState", "dNSHostName", "operatingSystem", "operatingSystemVersion",
        "location", "managedBy", "mS-DS-CreatorSID", "unicodePwd",

        // groups, OUs
        "groupType", "member", "ou", "gPLink", "gPOptions",

        // domain head, forest singletons
        "lockoutThreshold", "lockoutDuration", "lockOutObservationWindow", "maxPwdAge", "minPwdAge", "minPwdLength",
        "pwdHistoryLength", "pwdProperties", "ms-DS-MachineAccountQuota", "msDS-Behavior-Version", "fSMORoleOwner",
        "msDS-EnabledFeature", "dSHeuristics", "tombstoneLifetime",

        // PKI, GPO, trusts, PSOs
        "flags", "msPKI-Certificate-Name-Flag", "msPKI-Enrollment-Flag", "msPKI-RA-Signature", "pKIExtendedKeyUsage",
        "msPKI-Certificate-Application-Policy", "nTSecurityDescriptor", "certificateTemplates", "cACertificate", "versionNumber",
        "gPCFileSysPath", "trustDirection", "trustType", "trustAttributes", "flatName", "trustPartner",
        "msDS-PasswordSettingsPrecedence", "msDS-MinimumPasswordLength", "msDS-PasswordHistoryLength",
        "msDS-PasswordComplexityEnabled", "msDS-PasswordReversibleEncryptionEnabled", "msDS-MinimumPasswordAge",
        "msDS-MaximumPasswordAge", "msDS-LockoutThreshold", "msDS-LockoutObservationWindow", "msDS-LockoutDuration",
        "msDS-PSOAppliesTo",

        // pseudo attributes of health, summary and audit-log events
        "code", "attribution", "eventLogGap", "objects", "channel", "errorCode", "state", "SubcategoryGuid", "AuditPolicyChanges",

        // summary row of a truncated GPO content event (stage B design 8.6)
        Gpo.GpoSettingsDiffer.SummaryAttribute,
    ];

    /// <summary>Flag names with a display name (<c>Flag.{NAME}</c>): userAccountControl and groupType.</summary>
    public static IReadOnlyList<string> KnownFlags { get; } =
    [
        "SCRIPT", "ACCOUNTDISABLE", "HOMEDIR_REQUIRED", "LOCKOUT", "PASSWD_NOTREQD", "PASSWD_CANT_CHANGE",
        "ENCRYPTED_TEXT_PWD_ALLOWED", "TEMP_DUPLICATE_ACCOUNT", "NORMAL_ACCOUNT", "INTERDOMAIN_TRUST_ACCOUNT",
        "WORKSTATION_TRUST_ACCOUNT", "SERVER_TRUST_ACCOUNT", "DONT_EXPIRE_PASSWORD", "MNS_LOGON_ACCOUNT", "SMARTCARD_REQUIRED",
        "TRUSTED_FOR_DELEGATION", "NOT_DELEGATED", "USE_DES_KEY_ONLY", "DONT_REQ_PREAUTH", "PASSWORD_EXPIRED",
        "TRUSTED_TO_AUTH_FOR_DELEGATION", "PARTIAL_SECRETS_ACCOUNT", "BUILTIN_LOCAL_GROUP", "GLOBAL_GROUP", "DOMAIN_LOCAL_GROUP",
        "UNIVERSAL_GROUP", "APP_BASIC_GROUP", "APP_QUERY_GROUP", "SECURITY_ENABLED",
    ];

    private readonly NotificationText _text;
    private readonly TimeFormatter _time;

    public AttributeDisplay(NotificationText text, TimeFormatter time)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(time);
        _text = text;
        _time = time;
    }

    /// <summary>The display name of an attribute; the LDAP name when there is none.</summary>
    public string Name(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return _text.Find("Attr." + attribute) ?? attribute;
    }

    /// <summary>The display name of a flag ("Konto wyłączone"); the flag name when there is none.</summary>
    public string FlagName(string flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        return _text.Find("Flag." + flag) ?? flag;
    }

    /// <summary>The rendered old values of a change (empty when hidden).</summary>
    public IReadOnlyList<string> Before(AttributeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return [.. change.OldValues.Select(v => Value(change, v))];
    }

    /// <summary>The rendered new values of a change; flag attributes add the flags set (+) and cleared (−).</summary>
    public IReadOnlyList<string> After(AttributeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var values = change.NewValues.Select(v => Value(change, v)).ToList();
        var flags = FlagDelta(change);
        if (flags.Length > 0)
        {
            if (values.Count == 0)
            {
                values.Add(flags);
            }
            else
            {
                values[^1] = values[^1] + " (" + flags + ")";
            }
        }

        return values;
    }

    /// <summary>"+Konto wyłączone, −Hasło nigdy nie wygasa" (empty when no flag changed).</summary>
    public string FlagDelta(AttributeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return string.Join(", ", change.FlagsAdded.Select(f => "+" + FlagName(f)).Concat(change.FlagsRemoved.Select(f => "−" + FlagName(f))));
    }

    /// <summary>One value rendered for display according to the change's value kind.</summary>
    public string Value(AttributeChange change, string value)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(value);
        if (string.Equals(change.Attribute, Attr.LogonHours, StringComparison.OrdinalIgnoreCase))
        {
            return LogonHoursSummary.Describe(value, _text, _time.TimeZone);
        }

        if (IsUnreadableMarker(change.Attribute, value))
        {
            return _text.Get("Value.Unreadable");
        }

        return change.ValueKind switch
        {
            AttributeValueKind.FileTime => FileTime(change.Attribute, value),
            AttributeValueKind.GeneralizedTime => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? _time.Local(time)
                : value,
            AttributeValueKind.Boolean => value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? _text.Get("Value.True")
                : value.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ? _text.Get("Value.False") : value,
            AttributeValueKind.Dn => DistinguishedName.TryParse(value, out var dn) && !dn.IsRoot ? dn.Name : value,
            AttributeValueKind.Hash or AttributeValueKind.Binary => _text.Get("Value.Binary"),
            _ when Attr.FileTimeAttributes.Contains(change.Attribute) => FileTime(change.Attribute, value),
            _ => value,
        };
    }

    /// <summary>The value of a GPO content file that could not be read or parsed (stage B design 8.4: row
    /// <c>File/&lt;path&gt;</c> = <c>unreadable</c>), shown as <c>Value.Unreadable</c> („nie udało się odczytać”).</summary>
    public static bool IsUnreadableMarker(string attribute, string value) =>
        attribute.StartsWith(Gpo.GpoSnapshotBuilder.FileMarkerPrefix, StringComparison.Ordinal)
        && string.Equals(value, Gpo.GpoSettingsDiffer.UnreadableValue, StringComparison.Ordinal);

    /// <summary>"liczba wpisów: 1 → 2" for binary and hashed values whose content is never shown.</summary>
    public string EntryCounts(AttributeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return _text.Format("Value.Entries", change.OldCount, change.NewCount);
    }

    private string FileTime(string attribute, string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileTime))
        {
            return value;
        }

        if (AdConvert.FromFileTime(fileTime) is not { } time || fileTime <= 0)
        {
            return _text.Get("Value.Never");
        }

        if (string.Equals(attribute, Attr.AccountExpires, StringComparison.OrdinalIgnoreCase))
        {
            // ADUC "End of: D" stores the local midnight that starts D+1 (10.14).
            var lastDay = DateOnly.FromDateTime(_time.ToLocal(time).DateTime.AddTicks(-1));
            return _text.Format("Value.EndOfDay", _time.Date(lastDay));
        }

        return _time.Local(time);
    }
}
