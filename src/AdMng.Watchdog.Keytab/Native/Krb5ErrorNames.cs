using System.Globalization;

namespace AdMng.Watchdog.Keytab.Native;

/// <summary>
/// Symbolic names of the MIT krb5 error codes (com_err table <c>krb5</c>, base −1765328384) and of the kpasswd result
/// codes (RFC 3244) – what <c>status.json</c> records as <c>errorCode</c> and what the tool prints. Never the library's
/// message text (it may carry names); unknown codes become <c>krb5:&lt;code&gt;</c>.
/// </summary>
internal static class Krb5ErrorNames
{
    /// <summary><c>ERROR_TABLE_BASE_krb5</c>.</summary>
    public const int TableBase = -1765328384;

    public const int KdcErrClientPrincipalUnknown = TableBase + 6;
    public const int KdcErrServerPrincipalUnknown = TableBase + 7;
    public const int KdcErrClientRevoked = TableBase + 18;
    public const int KdcErrKeyExpired = TableBase + 23;
    public const int KdcErrPreauthFailed = TableBase + 24;
    public const int ApErrBadIntegrity = TableBase + 31;
    public const int ApErrSkew = TableBase + 37;
    public const int KdcUnreachable = TableBase + 156;
    public const int KeytabEnd = TableBase + 182;
    public const int PreauthFailed = TableBase + 210;

    /// <summary><c>KRB5_KPASSWD_SOFTERROR</c>: the KDC refused the new password by policy (e.g. minimum password age).</summary>
    public const int KpasswdSoftError = 4;

    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "KRB5KDC_ERR_NAME_EXP",
        [2] = "KRB5KDC_ERR_SERVICE_EXP",
        [6] = "KRB5KDC_ERR_C_PRINCIPAL_UNKNOWN",
        [7] = "KRB5KDC_ERR_S_PRINCIPAL_UNKNOWN",
        [8] = "KRB5KDC_ERR_PRINCIPAL_NOT_UNIQUE",
        [9] = "KRB5KDC_ERR_NULL_KEY",
        [12] = "KRB5KDC_ERR_POLICY",
        [13] = "KRB5KDC_ERR_BADOPTION",
        [14] = "KRB5KDC_ERR_ETYPE_NOSUPP",
        [16] = "KRB5KDC_ERR_PADATA_TYPE_NOSUPP",
        [18] = "KRB5KDC_ERR_CLIENT_REVOKED",
        [19] = "KRB5KDC_ERR_SERVICE_REVOKED",
        [20] = "KRB5KDC_ERR_TGT_REVOKED",
        [21] = "KRB5KDC_ERR_CLIENT_NOTYET",
        [23] = "KRB5KDC_ERR_KEY_EXP",
        [24] = "KRB5KDC_ERR_PREAUTH_FAILED",
        [25] = "KRB5KDC_ERR_PREAUTH_REQUIRED",
        [26] = "KRB5KDC_ERR_SERVER_NOMATCH",
        [29] = "KRB5KDC_ERR_SVC_UNAVAILABLE",
        [31] = "KRB5KRB_AP_ERR_BAD_INTEGRITY",
        [32] = "KRB5KRB_AP_ERR_TKT_EXPIRED",
        [37] = "KRB5KRB_AP_ERR_SKEW",
        [41] = "KRB5KRB_AP_ERR_MODIFIED",
        [44] = "KRB5KRB_AP_ERR_BADKEYVER",
        [45] = "KRB5KRB_AP_ERR_NOKEY",
        [52] = "KRB5KRB_ERR_RESPONSE_TOO_BIG",
        [60] = "KRB5KRB_ERR_GENERIC",
        [68] = "KRB5KDC_ERR_WRONG_REALM",
        [90] = "KRB5KDC_ERR_PREAUTH_EXPIRED",
        [91] = "KRB5KDC_ERR_MORE_PREAUTH_DATA_REQUIRED",
        [133] = "KRB5_PARSE_ILLCHAR",
        [134] = "KRB5_PARSE_MALFORMED",
        [135] = "KRB5_CONFIG_CANTOPEN",
        [136] = "KRB5_CONFIG_BADFORMAT",
        [141] = "KRB5_CC_NOTFOUND",
        [147] = "KRB5_KDCREP_MODIFIED",
        [148] = "KRB5_KDCREP_SKEW",
        [150] = "KRB5_PROG_ETYPE_NOSUPP",
        [154] = "KRB5_REALM_UNKNOWN",
        [155] = "KRB5_SERVICE_UNKNOWN",
        [156] = "KRB5_KDC_UNREACH",
        [178] = "KRB5_CRYPTO_INTERNAL",
        [179] = "KRB5_KT_BADNAME",
        [180] = "KRB5_KT_UNKNOWN_TYPE",
        [181] = "KRB5_KT_NOTFOUND",
        [182] = "KRB5_KT_END",
        [183] = "KRB5_KT_NOWRITE",
        [184] = "KRB5_KT_IOERR",
        [188] = "KRB5_BAD_ENCTYPE",
        [200] = "KRB5_CC_NOSUPP",
        [203] = "KRB5_NOCREDS_SUPPLIED",
        [210] = "KRB5_PREAUTH_FAILED",
        [217] = "KRB5_ERR_HOST_REALM_UNKNOWN",
        [220] = "KRB5_REALM_CANT_RESOLVE",
        [224] = "KRB5_CONFIG_NODEFREALM",
        [230] = "KRB5_KT_KVNONOTFOUND",
        [233] = "KRB5_CHPW_PWDNULL",
        [234] = "KRB5_CHPW_FAIL",
        [235] = "KRB5_KT_FORMAT",
        [236] = "KRB5_NOPERM_ETYPE",
        [237] = "KRB5_CONFIG_ETYPE_NOSUPP",
        [239] = "KRB5_EAI_FAIL",
        [240] = "KRB5_EAI_NODATA",
        [241] = "KRB5_EAI_NONAME",
        [244] = "KRB5_ERR_BAD_S2K_PARAMS",
    };

    private static readonly string[] KpasswdNames =
    [
        "KRB5_KPASSWD_SUCCESS", "KRB5_KPASSWD_MALFORMED", "KRB5_KPASSWD_HARDERROR", "KRB5_KPASSWD_AUTHERROR",
        "KRB5_KPASSWD_SOFTERROR", "KRB5_KPASSWD_ACCESSDENIED", "KRB5_KPASSWD_BAD_VERSION", "KRB5_KPASSWD_INITIAL_FLAG_NEEDED",
    ];

    /// <summary>The symbolic name of a krb5 error code (system errors as <c>errno:&lt;n&gt;</c>).</summary>
    public static string Of(int code)
    {
        if (code == 0)
        {
            return "0";
        }

        var offset = code - TableBase;
        if (offset is >= 0 and < 256 && Names.TryGetValue(offset, out var name))
        {
            return name;
        }

        return code is > 0 and < 4096
            ? "errno:" + code.ToString(CultureInfo.InvariantCulture)
            : "krb5:" + code.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The name of a kpasswd result code (RFC 3244 / MS-KILE).</summary>
    public static string OfKpasswdResult(int resultCode) =>
        resultCode >= 0 && resultCode < KpasswdNames.Length
            ? KpasswdNames[resultCode]
            : "KRB5_KPASSWD_" + resultCode.ToString(CultureInfo.InvariantCulture);
}
