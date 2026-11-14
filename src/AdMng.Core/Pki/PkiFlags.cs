using System.Globalization;

namespace AdMng.Core.Pki;

/// <summary>The four flag attributes of a certificate template (docs/dev/p-certificate-services.md 2.2).</summary>
public enum PkiFlagAttribute
{
    /// <summary><c>msPKI-Certificate-Name-Flag</c> (MS-CRTD 2.28).</summary>
    NameFlag,

    /// <summary><c>msPKI-Enrollment-Flag</c> (MS-CRTD 2.26).</summary>
    EnrollmentFlag,

    /// <summary><c>msPKI-Private-Key-Flag</c> (MS-CRTD 2.27).</summary>
    PrivateKeyFlag,

    /// <summary>The general <c>flags</c> of a <c>pKICertificateTemplate</c> (MS-CRTD 2.4) – chosen by the class, never by attribute name.</summary>
    GeneralFlag,
}

/// <summary>One bit of a flag table: its mask, its technical name (<c>CT_FLAG_…</c>) and whether the spec reserves it.</summary>
/// <param name="Mask">The bit.</param>
/// <param name="Name">The technical name (shown in the tooltip; the sentence is a resource, <c>PkiTpl.Flag.&lt;attribute&gt;.&lt;Name&gt;</c>).</param>
/// <param name="IsReserved">Reserved and ignored by every protocol (shown as <i>przestarzała, ignorowana</i>).</param>
public readonly record struct PkiFlagEntry(long Mask, string Name, bool IsReserved)
{
    /// <summary>The mask only (a constant of the spec, no directory value).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"0x{Mask:X}{(IsReserved ? " reserved" : string.Empty)}");
}

/// <summary>A multi-bit field of a flag value, decoded as a number (<i>wersja klienta: n</i>).</summary>
/// <param name="Mask">The bits of the field.</param>
/// <param name="Shift">The right shift that yields the number.</param>
/// <param name="Name">The field's name (resource <c>PkiTpl.Field.&lt;Name&gt;</c>).</param>
public readonly record struct PkiFlagField(long Mask, int Shift, string Name)
{
    /// <summary>The mask and shift only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"0x{Mask:X}>>{Shift}");
}

/// <summary>The number a multi-bit field holds.</summary>
/// <param name="Field">The field.</param>
/// <param name="Value">Its number.</param>
public readonly record struct PkiFlagFieldValue(PkiFlagField Field, int Value)
{
    /// <summary>The field only (the number is template data).</summary>
    public override string ToString() => Field.ToString();
}

/// <summary>A decoded flag value.</summary>
/// <param name="Set">The known bits that are set, in table order.</param>
/// <param name="UnknownBits">The set bits no table entry or field names (shown as <c>0x…</c>).</param>
/// <param name="Fields">The multi-bit fields with their numbers (private key flag only).</param>
public sealed record PkiDecodedFlags(IReadOnlyList<PkiFlagEntry> Set, long UnknownBits, IReadOnlyList<PkiFlagFieldValue> Fields)
{
    /// <summary>Each bit of <see cref="UnknownBits"/> as <c>0x…</c> (upper-case hex, ascending) – what the UI shows for them.</summary>
    public IReadOnlyList<string> UnknownBitNames
    {
        get
        {
            var names = new List<string>();
            for (var i = 0; i < 64; i++)
            {
                var bit = 1L << i;
                if ((UnknownBits & bit) != 0)
                {
                    names.Add(PkiFlags.BitName(bit));
                }
            }

            return names;
        }
    }

    /// <summary>Counts only (the value is template data).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Set.Count} flags, {(UnknownBits == 0 ? "no" : "some")} unknown bits, {Fields.Count} fields");
}

/// <summary>
/// The flag tables of certificate templates (2.2; values checked against Certipy <c>constants.py</c> at commit 3ae3442,
/// MS-CRTD 2.4/2.26/2.27/2.28 and the Watchdog's <c>FlagNames</c>), the EKU catalogue and the EKU predicates the ESC rules
/// use. Pure; shared with the Watchdog (decision 8).
/// </summary>
public static class PkiFlags
{
    /// <summary>Client Authentication.</summary>
    public const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    /// <summary>Server Authentication.</summary>
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>Smart-Card Logon.</summary>
    public const string SmartCardLogon = "1.3.6.1.4.1.311.20.2.2";

    /// <summary>PKINIT Client Authentication.</summary>
    public const string PkinitClientAuthentication = "1.3.6.1.5.2.3.4";

    /// <summary>KDC Authentication.</summary>
    public const string KdcAuthentication = "1.3.6.1.5.2.3.5";

    /// <summary>Certificate Request Agent (Enrollment Agent).</summary>
    public const string CertificateRequestAgent = "1.3.6.1.4.1.311.20.2.1";

    /// <summary>Any Purpose.</summary>
    public const string AnyPurpose = "2.5.29.37.0";

    /// <summary>
    /// The EKU / application-policy OIDs the predicates and the UI name (OID → the English name Windows shows): the seven of
    /// the predicates and the other purposes of the Windows default templates.
    /// </summary>
    public static IReadOnlyDictionary<string, string> KnownEku { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ClientAuthentication] = "Client Authentication",
        [ServerAuthentication] = "Server Authentication",
        [SmartCardLogon] = "Smart Card Logon",
        [PkinitClientAuthentication] = "PKINIT Client Authentication",
        [KdcAuthentication] = "KDC Authentication",
        [CertificateRequestAgent] = "Certificate Request Agent",
        [AnyPurpose] = "Any Purpose",
        ["1.3.6.1.5.5.7.3.3"] = "Code Signing",
        ["1.3.6.1.5.5.7.3.4"] = "Secure Email",
        ["1.3.6.1.5.5.7.3.5"] = "IP security end system",
        ["1.3.6.1.5.5.7.3.6"] = "IP security tunnel termination",
        ["1.3.6.1.5.5.7.3.7"] = "IP security user",
        ["1.3.6.1.5.5.7.3.8"] = "Time Stamping",
        ["1.3.6.1.5.5.7.3.9"] = "OCSP Signing",
        ["1.3.6.1.5.5.8.2.2"] = "IP security IKE intermediate",
        ["1.3.6.1.4.1.311.10.3.1"] = "Microsoft Trust List Signing",
        ["1.3.6.1.4.1.311.10.3.4"] = "Encrypting File System",
        ["1.3.6.1.4.1.311.10.3.4.1"] = "File Recovery",
        ["1.3.6.1.4.1.311.10.3.11"] = "Key Recovery",
        ["1.3.6.1.4.1.311.10.3.12"] = "Document Signing",
        ["1.3.6.1.4.1.311.10.3.13"] = "Lifetime Signing",
        ["1.3.6.1.4.1.311.21.5"] = "Private Key Archival",
        ["1.3.6.1.4.1.311.21.6"] = "Key Recovery Agent",
        ["1.3.6.1.4.1.311.21.19"] = "Directory Service Email Replication",
        ["1.3.6.1.4.1.311.54.1.2"] = "Remote Desktop Authentication",
    };

    /// <summary>The minimum CA version of <c>msPKI-Private-Key-Flag</c> (<c>CTPRIVATEKEY_FLAG_SERVERVERSION_MASK</c>, bits 16–19).</summary>
    public static PkiFlagField MinimumCaVersion { get; } = new(0x000F0000, 16, nameof(MinimumCaVersion));

    /// <summary>The minimum client version of <c>msPKI-Private-Key-Flag</c> (<c>CTPRIVATEKEY_FLAG_CLIENTVERSION_MASK</c>, bits 24–27).</summary>
    public static PkiFlagField MinimumClientVersion { get; } = new(0x0F000000, 24, nameof(MinimumClientVersion));

    // msPKI-Certificate-Name-Flag (MS-CRTD 2.28, Certipy CertificateNameFlag; 0x2, 0x4 and 0x100 sit where the v1 general
    // flags keep them).
    private static readonly PkiFlagEntry[] NameFlags =
    [
        new(0x00000001, "CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT", false),
        new(0x00000002, "CT_FLAG_ADD_EMAIL", false),
        new(0x00000004, "CT_FLAG_ADD_OBJ_GUID", false),
        new(0x00000008, "CT_FLAG_OLD_CERT_SUPPLIES_SUBJECT_AND_ALT_NAME", false),
        new(0x00000100, "CT_FLAG_ADD_DIRECTORY_PATH", false),
        new(0x00010000, "CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME", false),
        new(0x00400000, "CT_FLAG_SUBJECT_ALT_REQUIRE_DOMAIN_DNS", false),
        new(0x00800000, "CT_FLAG_SUBJECT_ALT_REQUIRE_SPN", false),
        new(0x01000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_DIRECTORY_GUID", false),
        new(0x02000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_UPN", false),
        new(0x04000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_EMAIL", false),
        new(0x08000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_DNS", false),
        new(0x10000000, "CT_FLAG_SUBJECT_REQUIRE_DNS_AS_CN", false),
        new(0x20000000, "CT_FLAG_SUBJECT_REQUIRE_EMAIL", false),
        new(0x40000000, "CT_FLAG_SUBJECT_REQUIRE_COMMON_NAME", false),
        new(0x80000000, "CT_FLAG_SUBJECT_REQUIRE_DIRECTORY_PATH", false),
    ];

    // msPKI-Enrollment-Flag (MS-CRTD 2.26). Certipy's 0x80 (CT_FLAG_DOMAIN_AUTHENTICATION_NOT_REQUIRED) and 0x200
    // (CT_FLAG_ADD_TEMPLATE_NAME) are not in the spec's table (the SDK's X509EnrollmentFlags has them): they decode as
    // unknown bits (2.2, 2.14).
    private static readonly PkiFlagEntry[] EnrollmentFlags =
    [
        new(0x00000001, "CT_FLAG_INCLUDE_SYMMETRIC_ALGORITHMS", false),
        new(0x00000002, "CT_FLAG_PEND_ALL_REQUESTS", false),
        new(0x00000004, "CT_FLAG_PUBLISH_TO_KRA_CONTAINER", false),
        new(0x00000008, "CT_FLAG_PUBLISH_TO_DS", false),
        new(0x00000010, "CT_FLAG_AUTO_ENROLLMENT_CHECK_USER_DS_CERTIFICATE", false),
        new(0x00000020, "CT_FLAG_AUTO_ENROLLMENT", false),
        new(0x00000040, "CT_FLAG_PREVIOUS_APPROVAL_VALIDATE_REENROLLMENT", false),
        new(0x00000100, "CT_FLAG_USER_INTERACTION_REQUIRED", false),
        new(0x00000400, "CT_FLAG_REMOVE_INVALID_CERTIFICATE_FROM_PERSONAL_STORE", false),
        new(0x00000800, "CT_FLAG_ALLOW_ENROLL_ON_BEHALF_OF", false),
        new(0x00001000, "CT_FLAG_ADD_OCSP_NOCHECK", false),
        new(0x00002000, "CT_FLAG_ENABLE_KEY_REUSE_ON_NT_TOKEN_KEYSET_STORAGE_FULL", false),
        new(0x00004000, "CT_FLAG_NOREVOCATIONINFOINISSUEDCERTS", false),
        new(0x00008000, "CT_FLAG_INCLUDE_BASIC_CONSTRAINTS_FOR_EE_CERTS", false),
        new(0x00010000, "CT_FLAG_ALLOW_PREVIOUS_APPROVAL_KEYBASEDRENEWAL_VALIDATE_REENROLLMENT", false),
        new(0x00020000, "CT_FLAG_ISSUANCE_POLICIES_FROM_REQUEST", false),
        new(0x00040000, "CT_FLAG_SKIP_AUTO_RENEWAL", false),
        new(0x00080000, "CT_FLAG_NO_SECURITY_EXTENSION", false),
    ];

    // msPKI-Private-Key-Flag (MS-CRTD 2.27). CT_FLAG_ATTEST_NONE is 0 (no bit: neither 0x1000 nor 0x2000 set); the two
    // version nibbles are the fields MinimumCaVersion and MinimumClientVersion.
    private static readonly PkiFlagEntry[] PrivateKeyFlags =
    [
        new(0x00000001, "CT_FLAG_REQUIRE_PRIVATE_KEY_ARCHIVAL", false),
        new(0x00000010, "CT_FLAG_EXPORTABLE_KEY", false),
        new(0x00000020, "CT_FLAG_STRONG_KEY_PROTECTION_REQUIRED", false),
        new(0x00000040, "CT_FLAG_REQUIRE_ALTERNATE_SIGNATURE_ALGORITHM", false),
        new(0x00000080, "CT_FLAG_REQUIRE_SAME_KEY_RENEWAL", false),
        new(0x00000100, "CT_FLAG_USE_LEGACY_PROVIDER", false),
        new(0x00000200, "CT_FLAG_EK_TRUST_ON_USE", false),
        new(0x00000400, "CT_FLAG_EK_VALIDATE_CERT", false),
        new(0x00000800, "CT_FLAG_EK_VALIDATE_KEY", false),
        new(0x00001000, "CT_FLAG_ATTEST_PREFERRED", false),
        new(0x00002000, "CT_FLAG_ATTEST_REQUIRED", false),
        new(0x00004000, "CT_FLAG_ATTESTATION_WITHOUT_POLICY", false),
        new(0x00200000, "CT_FLAG_HELLO_LOGON_KEY", false),
    ];

    // The general flags of a pKICertificateTemplate (MS-CRTD 2.4). 0x2, 0x8 and 0x10 are reserved and ignored by every
    // protocol; CT_FLAG_DONOTPERSISTINDB is 0x1000 (the spec's old 0x400 was an error, corrected in 2022).
    private static readonly PkiFlagEntry[] GeneralFlags =
    [
        new(0x00000002, "CT_FLAG_ADD_EMAIL", true),
        new(0x00000008, "CT_FLAG_PUBLISH_TO_DS", true),
        new(0x00000010, "CT_FLAG_EXPORTABLE_KEY", true),
        new(0x00000020, "CT_FLAG_AUTO_ENROLLMENT", false),
        new(0x00000040, "CT_FLAG_MACHINE_TYPE", false),
        new(0x00000080, "CT_FLAG_IS_CA", false),
        new(0x00000200, "CT_FLAG_ADD_TEMPLATE_NAME", false),
        new(0x00000800, "CT_FLAG_IS_CROSS_CA", false),
        new(0x00001000, "CT_FLAG_DONOTPERSISTINDB", false),
        new(0x00010000, "CT_FLAG_IS_DEFAULT", false),
        new(0x00020000, "CT_FLAG_IS_MODIFIED", false),
    ];

    private static readonly PkiFlagField[] PrivateKeyFieldList = [MinimumCaVersion, MinimumClientVersion];

    /// <summary>The bits of a table, in the order of 2.2 (reserved bits included).</summary>
    public static IReadOnlyList<PkiFlagEntry> Table(PkiFlagAttribute attribute) => attribute switch
    {
        PkiFlagAttribute.NameFlag => NameFlags,
        PkiFlagAttribute.EnrollmentFlag => EnrollmentFlags,
        PkiFlagAttribute.PrivateKeyFlag => PrivateKeyFlags,
        PkiFlagAttribute.GeneralFlag => GeneralFlags,
        _ => throw new ArgumentOutOfRangeException(nameof(attribute), attribute, "Not a template flag attribute."),
    };

    /// <summary>The multi-bit fields of <c>msPKI-Private-Key-Flag</c> (minimum CA version, shift 16; minimum client version, shift 24).</summary>
    public static IReadOnlyList<PkiFlagField> PrivateKeyFields => PrivateKeyFieldList;

    /// <summary>The multi-bit fields of an attribute (only <see cref="PkiFlagAttribute.PrivateKeyFlag"/> has any).</summary>
    public static IReadOnlyList<PkiFlagField> Fields(PkiFlagAttribute attribute) =>
        attribute == PkiFlagAttribute.PrivateKeyFlag ? PrivateKeyFieldList : [];

    /// <summary>
    /// Decodes a value: the known bits that are set (table order), the set bits no entry or field names, and – private key
    /// flag only – both fields with their numbers (also 0). Only the low 32 bits count, so the negative <c>Integer</c> AD
    /// returns and its <c>uint</c> decode alike.
    /// </summary>
    public static PkiDecodedFlags Decode(PkiFlagAttribute attribute, long value)
    {
        var table = Table(attribute);
        var low = value & 0xFFFFFFFFL;
        var unknown = low;
        var set = new List<PkiFlagEntry>();
        foreach (var entry in table)
        {
            if ((low & entry.Mask) != 0)
            {
                set.Add(entry);
                unknown &= ~entry.Mask;
            }
        }

        var fields = new List<PkiFlagFieldValue>();
        foreach (var field in Fields(attribute))
        {
            fields.Add(new PkiFlagFieldValue(field, (int)((low & field.Mask) >> field.Shift)));
            unknown &= ~field.Mask;
        }

        return new PkiDecodedFlags(set, unknown, fields);
    }

    /// <summary>
    /// The entries of <paramref name="bits"/> in the given order (the Watchdog's <c>FlagNames</c> subset, decision 8: today's
    /// bits in today's order, behaviour-neutral).
    /// </summary>
    /// <exception cref="ArgumentException">A bit is not an entry of the table (a programming error).</exception>
    public static IReadOnlyList<PkiFlagEntry> Select(PkiFlagAttribute attribute, IReadOnlyList<long> bits)
    {
        ArgumentNullException.ThrowIfNull(bits);
        var table = Table(attribute);
        var selected = new PkiFlagEntry[bits.Count];
        for (var i = 0; i < bits.Count; i++)
        {
            selected[i] = Find(table, bits[i]) ?? throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"{BitName(bits[i])} is not a bit of the {attribute} table."), nameof(bits));
        }

        return selected;
    }

    /// <summary>The name an unknown bit is shown with: <c>0x</c> and upper-case hex (as the Watchdog's <c>FlagNames</c>).</summary>
    public static string BitName(long bit) => string.Create(CultureInfo.InvariantCulture, $"0x{bit:X}");

    /// <summary>Any Purpose, or no EKU at all (an empty set means Any Purpose, as Certipy).</summary>
    public static bool IsAnyPurpose(IReadOnlyCollection<string> ekus)
    {
        ArgumentNullException.ThrowIfNull(ekus);
        return ekus.Count == 0 || Has(ekus, AnyPurpose);
    }

    /// <summary>Client Authentication, Smart-Card Logon, PKINIT Client Authentication or <see cref="IsAnyPurpose"/>.</summary>
    public static bool IsClientAuth(IReadOnlyCollection<string> ekus) =>
        IsAnyPurpose(ekus) || Has(ekus, ClientAuthentication) || Has(ekus, SmartCardLogon) || Has(ekus, PkinitClientAuthentication);

    /// <summary>Server Authentication or <see cref="IsAnyPurpose"/>.</summary>
    public static bool IsServerAuth(IReadOnlyCollection<string> ekus) => IsAnyPurpose(ekus) || Has(ekus, ServerAuthentication);

    /// <summary>Certificate Request Agent or <see cref="IsAnyPurpose"/>.</summary>
    public static bool IsEnrollmentAgent(IReadOnlyCollection<string> ekus) => IsAnyPurpose(ekus) || Has(ekus, CertificateRequestAgent);

    private static PkiFlagEntry? Find(IReadOnlyList<PkiFlagEntry> table, long mask)
    {
        foreach (var entry in table)
        {
            if (entry.Mask == mask)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The OID is in the set (ordinal; surrounding white space ignored).</summary>
    private static bool Has(IReadOnlyCollection<string> ekus, string oid)
    {
        foreach (var eku in ekus)
        {
            if (eku is not null && eku.AsSpan().Trim().Equals(oid, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
