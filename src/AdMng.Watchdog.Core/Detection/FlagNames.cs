using System.Globalization;
using AdMng.Core.ActiveDirectory;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Flag names of bit-mask attributes for <c>AttributeChange.FlagsAdded/FlagsRemoved</c>: userAccountControl (the LDAP
/// names, upper-snake), groupType, gPOptions, trustAttributes, pwdProperties, msDS-SupportedEncryptionTypes and the
/// certificate template flags. Unknown bits are named <c>0x…</c>.
/// </summary>
public static class FlagNames
{
    private static readonly (long Bit, string Name)[] UserAccountControlNames =
    [
        (0x0001, "SCRIPT"), (0x0002, "ACCOUNTDISABLE"), (0x0008, "HOMEDIR_REQUIRED"), (0x0010, "LOCKOUT"),
        (0x0020, "PASSWD_NOTREQD"), (0x0040, "PASSWD_CANT_CHANGE"), (0x0080, "ENCRYPTED_TEXT_PWD_ALLOWED"),
        (0x0100, "TEMP_DUPLICATE_ACCOUNT"), (0x0200, "NORMAL_ACCOUNT"), (0x0800, "INTERDOMAIN_TRUST_ACCOUNT"),
        (0x1000, "WORKSTATION_TRUST_ACCOUNT"), (0x2000, "SERVER_TRUST_ACCOUNT"), (0x10000, "DONT_EXPIRE_PASSWORD"),
        (0x20000, "MNS_LOGON_ACCOUNT"), (0x40000, "SMARTCARD_REQUIRED"), (0x80000, "TRUSTED_FOR_DELEGATION"),
        (0x100000, "NOT_DELEGATED"), (0x200000, "USE_DES_KEY_ONLY"), (0x400000, "DONT_REQ_PREAUTH"),
        (0x800000, "PASSWORD_EXPIRED"), (0x1000000, "TRUSTED_TO_AUTH_FOR_DELEGATION"), (0x4000000, "PARTIAL_SECRETS_ACCOUNT"),
    ];

    private static readonly (long Bit, string Name)[] GroupTypeNames =
    [
        (0x1, "BUILTIN_LOCAL_GROUP"), (0x2, "GLOBAL_GROUP"), (0x4, "DOMAIN_LOCAL_GROUP"), (0x8, "UNIVERSAL_GROUP"),
        (0x10, "APP_BASIC_GROUP"), (0x20, "APP_QUERY_GROUP"), (0x80000000, "SECURITY_ENABLED"),
    ];

    private static readonly (long Bit, string Name)[] GpOptionsNames = [(0x1, "BLOCK_INHERITANCE")];

    private static readonly (long Bit, string Name)[] TrustAttributeNames =
    [
        (0x1, "NON_TRANSITIVE"), (0x2, "UPLEVEL_ONLY"), (0x4, "QUARANTINED_DOMAIN"), (0x8, "FOREST_TRANSITIVE"),
        (0x10, "CROSS_ORGANIZATION"), (0x20, "WITHIN_FOREST"), (0x40, "TREAT_AS_EXTERNAL"), (0x80, "USES_RC4_ENCRYPTION"),
        (0x200, "CROSS_ORGANIZATION_NO_TGT_DELEGATION"), (0x400, "PIM_TRUST"), (0x800, "CROSS_ORGANIZATION_ENABLE_TGT_DELEGATION"),
    ];

    private static readonly (long Bit, string Name)[] PwdPropertiesNames =
    [
        (0x1, "DOMAIN_PASSWORD_COMPLEX"), (0x2, "DOMAIN_PASSWORD_NO_ANON_CHANGE"), (0x4, "DOMAIN_PASSWORD_NO_CLEAR_CHANGE"),
        (0x8, "DOMAIN_LOCKOUT_ADMINS"), (0x10, "DOMAIN_PASSWORD_STORE_CLEARTEXT"), (0x20, "DOMAIN_REFUSE_PASSWORD_CHANGE"),
    ];

    private static readonly (long Bit, string Name)[] EncryptionTypeNames =
    [
        (0x1, "DES_CBC_CRC"), (0x2, "DES_CBC_MD5"), (0x4, "RC4_HMAC_MD5"), (0x8, "AES128_CTS_HMAC_SHA1_96"),
        (0x10, "AES256_CTS_HMAC_SHA1_96"), (0x20, "AES256_CTS_HMAC_SHA1_96_SK"),
    ];

    private static readonly (long Bit, string Name)[] CertificateNameFlagNames =
    [
        (0x1, "CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT"), (0x8, "CT_FLAG_OLD_CERT_SUPPLIES_SUBJECT_AND_ALT_NAME"),
        (0x10000, "CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME"), (0x400000, "CT_FLAG_SUBJECT_ALT_REQUIRE_DOMAIN_DNS"),
        (0x1000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_DIRECTORY_GUID"), (0x2000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_UPN"),
        (0x4000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_EMAIL"), (0x8000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_DNS"),
        (0x10000000, "CT_FLAG_SUBJECT_REQUIRE_DNS_AS_CN"), (0x20000000, "CT_FLAG_SUBJECT_REQUIRE_EMAIL"),
        (0x40000000, "CT_FLAG_SUBJECT_REQUIRE_COMMON_NAME"), (0x80000000, "CT_FLAG_SUBJECT_REQUIRE_DIRECTORY_PATH"),
    ];

    private static readonly (long Bit, string Name)[] EnrollmentFlagNames =
    [
        (0x1, "CT_FLAG_INCLUDE_SYMMETRIC_ALGORITHMS"), (0x2, "CT_FLAG_PEND_ALL_REQUESTS"), (0x4, "CT_FLAG_PUBLISH_TO_KRA_CONTAINER"),
        (0x8, "CT_FLAG_PUBLISH_TO_DS"), (0x10, "CT_FLAG_AUTO_ENROLLMENT_CHECK_USER_DS_CERTIFICATE"), (0x20, "CT_FLAG_AUTO_ENROLLMENT"),
        (0x40, "CT_FLAG_PREVIOUS_APPROVAL_VALIDATE_REENROLLMENT"), (0x100, "CT_FLAG_USER_INTERACTION_REQUIRED"),
        (0x400, "CT_FLAG_REMOVE_INVALID_CERTIFICATE_FROM_PERSONAL_STORE"), (0x800, "CT_FLAG_ALLOW_ENROLL_ON_BEHALF_OF"),
        (0x1000, "CT_FLAG_ADD_OCSP_NOCHECK"), (0x80000, "CT_FLAG_NO_SECURITY_EXTENSION"),
    ];

    /// <summary>True when the attribute's changes are reported as flags.</summary>
    public static bool IsFlagAttribute(string attribute) => TableOf(attribute) is not null;

    /// <summary>Flag names of a value (unknown bits as <c>0x…</c>); empty for a non-flag attribute.</summary>
    public static IReadOnlyList<string> Decode(string attribute, long value)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (TableOf(attribute) is not { } table)
        {
            return [];
        }

        var bits = value & 0xFFFFFFFFL;
        var names = new List<string>();
        foreach (var (bit, name) in table)
        {
            if ((bits & bit) != 0)
            {
                names.Add(name);
                bits &= ~bit;
            }
        }

        for (var i = 0; i < 32 && bits != 0; i++)
        {
            var bit = 1L << i;
            if ((bits & bit) != 0)
            {
                names.Add(string.Create(CultureInfo.InvariantCulture, $"0x{bit:X}"));
                bits &= ~bit;
            }
        }

        return names;
    }

    /// <summary>Flag names set and cleared between two values (absent = 0).</summary>
    public static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) Diff(string attribute, long? oldValue, long? newValue)
    {
        var oldNames = Decode(attribute, oldValue ?? 0);
        var newNames = Decode(attribute, newValue ?? 0);
        return (newNames.Except(oldNames, StringComparer.Ordinal).ToList(), oldNames.Except(newNames, StringComparer.Ordinal).ToList());
    }

    private static (long Bit, string Name)[]? TableOf(string attribute) => attribute switch
    {
        _ when attribute.Equals(Attr.UserAccountControl, StringComparison.OrdinalIgnoreCase) => UserAccountControlNames,
        _ when attribute.Equals(Attr.GroupType, StringComparison.OrdinalIgnoreCase) => GroupTypeNames,
        _ when attribute.Equals(WatchdogAttributes.GpOptions, StringComparison.OrdinalIgnoreCase) => GpOptionsNames,
        _ when attribute.Equals(WatchdogAttributes.TrustAttributes, StringComparison.OrdinalIgnoreCase) => TrustAttributeNames,
        _ when attribute.Equals(WatchdogAttributes.PwdProperties, StringComparison.OrdinalIgnoreCase) => PwdPropertiesNames,
        _ when attribute.Equals(Attr.SupportedEncryptionTypes, StringComparison.OrdinalIgnoreCase) => EncryptionTypeNames,
        _ when attribute.Equals(WatchdogAttributes.CertificateNameFlag, StringComparison.OrdinalIgnoreCase) => CertificateNameFlagNames,
        _ when attribute.Equals(WatchdogAttributes.EnrollmentFlag, StringComparison.OrdinalIgnoreCase) => EnrollmentFlagNames,
        _ => null,
    };
}
