using System.Globalization;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Decodes the <c>OldUacValue</c> / <c>NewUacValue</c> of 4720/4738/4741/4742 as <b>SAM ACB bits</b> (section 11.3 – not
/// the LDAP <c>userAccountControl</c> table, which Microsoft's own 4720 prose wrongly applies; the Microsoft samples
/// verify the ACB interpretation) and maps them to the LDAP <c>userAccountControl</c> bits for comparison with the LDAP
/// diff (names as in the LDAP documentation: <c>ACCOUNTDISABLE</c>, <c>PASSWD_NOTREQD</c>, …).
/// </summary>
public static class SamAccountControlDecoder
{
    private static readonly (uint Acb, string AcbName, uint Ldap, string LdapName)[] Bits =
    [
        (0x1, "DISABLED", 0x2, "ACCOUNTDISABLE"),
        (0x2, "HOMDIRREQ", 0x8, "HOMEDIR_REQUIRED"),
        (0x4, "PWNOTREQ", 0x20, "PASSWD_NOTREQD"),
        (0x8, "TEMPDUP", 0x100, "TEMP_DUPLICATE_ACCOUNT"),
        (0x10, "NORMAL", 0x200, "NORMAL_ACCOUNT"),
        (0x20, "MNS", 0x20000, "MNS_LOGON_ACCOUNT"),
        (0x40, "DOMTRUST", 0x800, "INTERDOMAIN_TRUST_ACCOUNT"),
        (0x80, "WSTRUST", 0x1000, "WORKSTATION_TRUST_ACCOUNT"),
        (0x100, "SVRTRUST", 0x2000, "SERVER_TRUST_ACCOUNT"),
        (0x200, "PWNOEXP", 0x10000, "DONT_EXPIRE_PASSWORD"),
        (0x400, "AUTOLOCK", 0x10, "LOCKOUT"),
        (0x800, "ENC_TXT_PWD_ALLOWED", 0x80, "ENCRYPTED_TEXT_PWD_ALLOWED"),
        (0x1000, "SMARTCARD_REQUIRED", 0x40000, "SMARTCARD_REQUIRED"),
        (0x2000, "TRUSTED_FOR_DELEGATION", 0x80000, "TRUSTED_FOR_DELEGATION"),
        (0x4000, "NOT_DELEGATED", 0x100000, "NOT_DELEGATED"),
        (0x8000, "USE_DES_KEY_ONLY", 0x200000, "USE_DES_KEY_ONLY"),
        (0x10000, "DONT_REQUIRE_PREAUTH", 0x400000, "DONT_REQ_PREAUTH"),
        (0x20000, "PW_EXPIRED", 0x800000, "PASSWORD_EXPIRED"),
        (0x40000, "TRUSTED_TO_AUTHENTICATE_FOR_DELEGATION", 0x1000000, "TRUSTED_TO_AUTH_FOR_DELEGATION"),
        (0x80000, "NO_AUTH_DATA_REQD", 0x2000000, "NO_AUTH_DATA_REQUIRED"),
        (0x100000, "PARTIAL_SECRETS", 0x4000000, "PARTIAL_SECRETS_ACCOUNT"),
        (0x200000, "USE_AES_KEYS", 0x8000000, "USE_AES_KEYS"),
    ];

    /// <summary>Parses <c>0x15</c> (hexadecimal, as logged) or a decimal number.</summary>
    public static bool TryParse(string? value, out uint acb)
    {
        acb = 0;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out acb)
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out acb);
    }

    /// <summary>The LDAP <c>userAccountControl</c> bits of SAM ACB bits (unmapped ACB bits are dropped).</summary>
    public static uint ToLdap(uint acb)
    {
        uint ldap = 0;
        foreach (var bit in Bits)
        {
            if ((acb & bit.Acb) != 0)
            {
                ldap |= bit.Ldap;
            }
        }

        return ldap;
    }

    /// <summary>The ACB names of the set bits (<c>DISABLED</c>, <c>PWNOTREQ</c>, …).</summary>
    public static IReadOnlyList<string> AcbNames(uint acb) => [.. Bits.Where(b => (acb & b.Acb) != 0).Select(b => b.AcbName)];

    /// <summary>The LDAP names of the set LDAP <c>userAccountControl</c> bits.</summary>
    public static IReadOnlyList<string> LdapNames(uint ldap) => [.. Bits.Where(b => (ldap & b.Ldap) != 0).Select(b => b.LdapName)];

    /// <summary>The LDAP bits set and cleared between two ACB values.</summary>
    public static (uint Set, uint Cleared) LdapChanges(uint oldAcb, uint newAcb)
    {
        var oldLdap = ToLdap(oldAcb);
        var newLdap = ToLdap(newAcb);
        return (newLdap & ~oldLdap, oldLdap & ~newLdap);
    }
}
