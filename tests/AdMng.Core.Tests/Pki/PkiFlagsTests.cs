using AdMng.Core.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// The template flag tables, the decoder, the Watchdog subset and the EKU predicates (docs/dev/p-certificate-services.md
/// 2.2, 2.10): every bit of each table with its MS-CRTD name, reserved bits, unknown bits as <c>0x…</c>, the private-key
/// version fields as numbers, empty EKU = Any Purpose.
/// </summary>
public sealed class PkiFlagsTests
{
    private static readonly (PkiFlagAttribute Attribute, long Bit, string Name)[] AllBits =
    [
        // msPKI-Certificate-Name-Flag (MS-CRTD 2.28)
        (PkiFlagAttribute.NameFlag, 0x00000001, "CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT"),
        (PkiFlagAttribute.NameFlag, 0x00000002, "CT_FLAG_ADD_EMAIL"),
        (PkiFlagAttribute.NameFlag, 0x00000004, "CT_FLAG_ADD_OBJ_GUID"),
        (PkiFlagAttribute.NameFlag, 0x00000008, "CT_FLAG_OLD_CERT_SUPPLIES_SUBJECT_AND_ALT_NAME"),
        (PkiFlagAttribute.NameFlag, 0x00000100, "CT_FLAG_ADD_DIRECTORY_PATH"),
        (PkiFlagAttribute.NameFlag, 0x00010000, "CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME"),
        (PkiFlagAttribute.NameFlag, 0x00400000, "CT_FLAG_SUBJECT_ALT_REQUIRE_DOMAIN_DNS"),
        (PkiFlagAttribute.NameFlag, 0x00800000, "CT_FLAG_SUBJECT_ALT_REQUIRE_SPN"),
        (PkiFlagAttribute.NameFlag, 0x01000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_DIRECTORY_GUID"),
        (PkiFlagAttribute.NameFlag, 0x02000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_UPN"),
        (PkiFlagAttribute.NameFlag, 0x04000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_EMAIL"),
        (PkiFlagAttribute.NameFlag, 0x08000000, "CT_FLAG_SUBJECT_ALT_REQUIRE_DNS"),
        (PkiFlagAttribute.NameFlag, 0x10000000, "CT_FLAG_SUBJECT_REQUIRE_DNS_AS_CN"),
        (PkiFlagAttribute.NameFlag, 0x20000000, "CT_FLAG_SUBJECT_REQUIRE_EMAIL"),
        (PkiFlagAttribute.NameFlag, 0x40000000, "CT_FLAG_SUBJECT_REQUIRE_COMMON_NAME"),
        (PkiFlagAttribute.NameFlag, 0x80000000, "CT_FLAG_SUBJECT_REQUIRE_DIRECTORY_PATH"),

        // msPKI-Enrollment-Flag (MS-CRTD 2.26)
        (PkiFlagAttribute.EnrollmentFlag, 0x00000001, "CT_FLAG_INCLUDE_SYMMETRIC_ALGORITHMS"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00000002, "CT_FLAG_PEND_ALL_REQUESTS"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00000004, "CT_FLAG_PUBLISH_TO_KRA_CONTAINER"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00000008, "CT_FLAG_PUBLISH_TO_DS"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00000010, "CT_FLAG_AUTO_ENROLLMENT_CHECK_USER_DS_CERTIFICATE"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00000020, "CT_FLAG_AUTO_ENROLLMENT"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00000040, "CT_FLAG_PREVIOUS_APPROVAL_VALIDATE_REENROLLMENT"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00000100, "CT_FLAG_USER_INTERACTION_REQUIRED"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00000400, "CT_FLAG_REMOVE_INVALID_CERTIFICATE_FROM_PERSONAL_STORE"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00000800, "CT_FLAG_ALLOW_ENROLL_ON_BEHALF_OF"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00001000, "CT_FLAG_ADD_OCSP_NOCHECK"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00002000, "CT_FLAG_ENABLE_KEY_REUSE_ON_NT_TOKEN_KEYSET_STORAGE_FULL"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00004000, "CT_FLAG_NOREVOCATIONINFOINISSUEDCERTS"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00008000, "CT_FLAG_INCLUDE_BASIC_CONSTRAINTS_FOR_EE_CERTS"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00010000, "CT_FLAG_ALLOW_PREVIOUS_APPROVAL_KEYBASEDRENEWAL_VALIDATE_REENROLLMENT"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00020000, "CT_FLAG_ISSUANCE_POLICIES_FROM_REQUEST"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00040000, "CT_FLAG_SKIP_AUTO_RENEWAL"),
        (PkiFlagAttribute.EnrollmentFlag, 0x00080000, "CT_FLAG_NO_SECURITY_EXTENSION"),

        // msPKI-Private-Key-Flag (MS-CRTD 2.27)
        (PkiFlagAttribute.PrivateKeyFlag, 0x00000001, "CT_FLAG_REQUIRE_PRIVATE_KEY_ARCHIVAL"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00000010, "CT_FLAG_EXPORTABLE_KEY"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00000020, "CT_FLAG_STRONG_KEY_PROTECTION_REQUIRED"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00000040, "CT_FLAG_REQUIRE_ALTERNATE_SIGNATURE_ALGORITHM"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00000080, "CT_FLAG_REQUIRE_SAME_KEY_RENEWAL"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00000100, "CT_FLAG_USE_LEGACY_PROVIDER"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00000200, "CT_FLAG_EK_TRUST_ON_USE"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00000400, "CT_FLAG_EK_VALIDATE_CERT"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00000800, "CT_FLAG_EK_VALIDATE_KEY"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00001000, "CT_FLAG_ATTEST_PREFERRED"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00002000, "CT_FLAG_ATTEST_REQUIRED"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00004000, "CT_FLAG_ATTESTATION_WITHOUT_POLICY"),
        (PkiFlagAttribute.PrivateKeyFlag, 0x00200000, "CT_FLAG_HELLO_LOGON_KEY"),

        // flags of pKICertificateTemplate (MS-CRTD 2.4)
        (PkiFlagAttribute.GeneralFlag, 0x00000002, "CT_FLAG_ADD_EMAIL"),
        (PkiFlagAttribute.GeneralFlag, 0x00000008, "CT_FLAG_PUBLISH_TO_DS"),
        (PkiFlagAttribute.GeneralFlag, 0x00000010, "CT_FLAG_EXPORTABLE_KEY"),
        (PkiFlagAttribute.GeneralFlag, 0x00000020, "CT_FLAG_AUTO_ENROLLMENT"),
        (PkiFlagAttribute.GeneralFlag, 0x00000040, "CT_FLAG_MACHINE_TYPE"),
        (PkiFlagAttribute.GeneralFlag, 0x00000080, "CT_FLAG_IS_CA"),
        (PkiFlagAttribute.GeneralFlag, 0x00000200, "CT_FLAG_ADD_TEMPLATE_NAME"),
        (PkiFlagAttribute.GeneralFlag, 0x00000800, "CT_FLAG_IS_CROSS_CA"),
        (PkiFlagAttribute.GeneralFlag, 0x00001000, "CT_FLAG_DONOTPERSISTINDB"),
        (PkiFlagAttribute.GeneralFlag, 0x00010000, "CT_FLAG_IS_DEFAULT"),
        (PkiFlagAttribute.GeneralFlag, 0x00020000, "CT_FLAG_IS_MODIFIED"),
    ];

    public static TheoryData<PkiFlagAttribute, long, string> EveryBit()
    {
        var data = new TheoryData<PkiFlagAttribute, long, string>();
        foreach (var (attribute, bit, name) in AllBits)
        {
            data.Add(attribute, bit, name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryBit))]
    public void EveryBit_DecodesToItsName(PkiFlagAttribute attribute, long bit, string name)
    {
        var decoded = PkiFlags.Decode(attribute, bit);

        var entry = Assert.Single(decoded.Set);
        Assert.Equal(name, entry.Name);
        Assert.Equal(bit, entry.Mask);
        Assert.Equal(0, decoded.UnknownBits);
        Assert.Empty(decoded.UnknownBitNames);
    }

    [Theory]
    [InlineData(PkiFlagAttribute.NameFlag, 16)]
    [InlineData(PkiFlagAttribute.EnrollmentFlag, 18)]
    [InlineData(PkiFlagAttribute.PrivateKeyFlag, 13)]
    [InlineData(PkiFlagAttribute.GeneralFlag, 11)]
    public void EachTable_IsExactlyTheSpecBits_InAscendingOrder(PkiFlagAttribute attribute, int count)
    {
        var table = PkiFlags.Table(attribute);

        Assert.Equal(count, table.Count);
        Assert.Equal(count, AllBits.Count(row => row.Attribute == attribute));
        Assert.Equal(table.Select(e => e.Mask).Order(), table.Select(e => e.Mask));
        Assert.All(table, e => Assert.Equal(1, System.Numerics.BitOperations.PopCount((ulong)e.Mask)));
        Assert.All(table, e => Assert.StartsWith("CT_FLAG_", e.Name, StringComparison.Ordinal));
        Assert.Equal(table.Count, table.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void OnlyTheThreeObsoleteGeneralFlags_AreReserved()
    {
        Assert.Equal([0x2L, 0x8L, 0x10L], PkiFlags.Table(PkiFlagAttribute.GeneralFlag).Where(e => e.IsReserved).Select(e => e.Mask));
        Assert.All(new[] { PkiFlagAttribute.NameFlag, PkiFlagAttribute.EnrollmentFlag, PkiFlagAttribute.PrivateKeyFlag },
            attribute => Assert.DoesNotContain(PkiFlags.Table(attribute), e => e.IsReserved));

        // The default User template's general flags: IS_DEFAULT, ADD_TEMPLATE_NAME, AUTO_ENROLLMENT and three reserved bits.
        var decoded = PkiFlags.Decode(PkiFlagAttribute.GeneralFlag, 0x1023A);
        Assert.Equal(
            ["CT_FLAG_ADD_EMAIL", "CT_FLAG_PUBLISH_TO_DS", "CT_FLAG_EXPORTABLE_KEY", "CT_FLAG_AUTO_ENROLLMENT", "CT_FLAG_ADD_TEMPLATE_NAME", "CT_FLAG_IS_DEFAULT"],
            decoded.Set.Select(e => e.Name));
        Assert.Equal(3, decoded.Set.Count(e => e.IsReserved));
    }

    [Fact]
    public void TheVector_0x80000_IsNoSecurityExtension()
    {
        var decoded = PkiFlags.Decode(PkiFlagAttribute.EnrollmentFlag, 0x80000);

        Assert.Equal("CT_FLAG_NO_SECURITY_EXTENSION", Assert.Single(decoded.Set).Name);
    }

    [Fact]
    public void UnknownBits_AreKept_AndNamedInHex()
    {
        // Certipy's 0x80 / 0x200 enrollment bits are not in MS-CRTD 2.26 (2.14): unknown, like 0x100000.
        var decoded = PkiFlags.Decode(PkiFlagAttribute.EnrollmentFlag, 0x00100282);

        Assert.Equal(["CT_FLAG_PEND_ALL_REQUESTS"], decoded.Set.Select(e => e.Name));
        Assert.Equal(0x00100280, decoded.UnknownBits);
        Assert.Equal(["0x80", "0x200", "0x100000"], decoded.UnknownBitNames);
        Assert.Empty(decoded.Fields);

        var general = PkiFlags.Decode(PkiFlagAttribute.GeneralFlag, unchecked((int)0x80000905));
        Assert.Equal(["CT_FLAG_IS_CROSS_CA"], general.Set.Select(e => e.Name));
        Assert.Equal(["0x1", "0x4", "0x100", "0x80000000"], general.UnknownBitNames);
        Assert.Equal("0x80000000", PkiFlags.BitName(0x80000000));
    }

    [Fact]
    public void NegativeIntegers_DecodeLikeTheirUnsignedMask_AndHighBitsAreIgnored()
    {
        // The default User template's name flag as AD returns it (Integer syntax: -1509949440).
        var fromInt = PkiFlags.Decode(PkiFlagAttribute.NameFlag, -1509949440);
        var fromUint = PkiFlags.Decode(PkiFlagAttribute.NameFlag, 0xA6000000u);

        Assert.Equal(
            ["CT_FLAG_SUBJECT_ALT_REQUIRE_UPN", "CT_FLAG_SUBJECT_ALT_REQUIRE_EMAIL", "CT_FLAG_SUBJECT_REQUIRE_EMAIL", "CT_FLAG_SUBJECT_REQUIRE_DIRECTORY_PATH"],
            fromInt.Set.Select(e => e.Name));
        Assert.Equal(fromUint.Set, fromInt.Set);
        Assert.Equal(0, fromInt.UnknownBits);
        Assert.Equal(["CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT"], PkiFlags.Decode(PkiFlagAttribute.NameFlag, 0x7_0000_0001L).Set.Select(e => e.Name));
        Assert.Empty(PkiFlags.Decode(PkiFlagAttribute.NameFlag, 0).Set);
    }

    [Fact]
    public void PrivateKeyVersionNibbles_AreFields_NotFlags()
    {
        Assert.Equal([new PkiFlagField(0x000F0000, 16, "MinimumCaVersion"), new PkiFlagField(0x0F000000, 24, "MinimumClientVersion")], PkiFlags.PrivateKeyFields);
        Assert.Same(PkiFlags.PrivateKeyFields, PkiFlags.Fields(PkiFlagAttribute.PrivateKeyFlag));
        Assert.Empty(PkiFlags.Fields(PkiFlagAttribute.NameFlag));

        // A Windows Server 2016 template: CA 2012 R2 (5), client Windows 8.1 (5), exportable key, attestation required.
        var decoded = PkiFlags.Decode(PkiFlagAttribute.PrivateKeyFlag, 0x05052010);

        Assert.Equal(["CT_FLAG_EXPORTABLE_KEY", "CT_FLAG_ATTEST_REQUIRED"], decoded.Set.Select(e => e.Name));
        Assert.Equal(0, decoded.UnknownBits);
        Assert.Equal(new[] { (PkiFlags.MinimumCaVersion, 5), (PkiFlags.MinimumClientVersion, 5) }, decoded.Fields.Select(f => (f.Field, f.Value)));

        // A schema-1 template: both fields 0, still reported; bits above the nibbles and between them stay unknown.
        var v1 = PkiFlags.Decode(PkiFlagAttribute.PrivateKeyFlag, 0x10);
        Assert.Equal([0, 0], v1.Fields.Select(f => f.Value));
        Assert.Equal(["0x100000", "0x400000", "0x10000000"], PkiFlags.Decode(PkiFlagAttribute.PrivateKeyFlag, 0x10500000 | 0x01010000).UnknownBitNames);
        Assert.Equal([1, 1], PkiFlags.Decode(PkiFlagAttribute.PrivateKeyFlag, 0x10500000 | 0x01010000).Fields.Select(f => f.Value));
        Assert.Equal([15, 15], PkiFlags.Decode(PkiFlagAttribute.PrivateKeyFlag, 0x0F0F0000).Fields.Select(f => f.Value));
    }

    [Fact]
    public void Select_KeepsTheGivenOrder_AndRefusesABitOutsideTheTable()
    {
        var selected = PkiFlags.Select(PkiFlagAttribute.EnrollmentFlag, [0x80000, 0x1, 0x800]);

        Assert.Equal(["CT_FLAG_NO_SECURITY_EXTENSION", "CT_FLAG_INCLUDE_SYMMETRIC_ALGORITHMS", "CT_FLAG_ALLOW_ENROLL_ON_BEHALF_OF"], selected.Select(e => e.Name));
        Assert.Empty(PkiFlags.Select(PkiFlagAttribute.NameFlag, []));
        Assert.Throws<ArgumentException>(() => PkiFlags.Select(PkiFlagAttribute.EnrollmentFlag, [0x80]));
        Assert.Throws<ArgumentException>(() => PkiFlags.Select(PkiFlagAttribute.NameFlag, [0x3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PkiFlags.Table((PkiFlagAttribute)42));
    }

    [Fact]
    public void FlagRecords_PrintOnlySpecConstants()
    {
        Assert.Equal("0x10 reserved", PkiFlags.Table(PkiFlagAttribute.GeneralFlag)[2].ToString());
        Assert.Equal("0x80000", PkiFlags.Select(PkiFlagAttribute.EnrollmentFlag, [0x80000])[0].ToString());
        Assert.Equal("0xF000000>>24", PkiFlags.MinimumClientVersion.ToString());
        Assert.Equal("2 flags, no unknown bits, 2 fields", PkiFlags.Decode(PkiFlagAttribute.PrivateKeyFlag, 0x05052010).ToString());
    }

    [Fact]
    public void KnownEku_NamesThePredicateOids_AndTheDefaultTemplatePurposes()
    {
        Assert.Equal("Client Authentication", PkiFlags.KnownEku["1.3.6.1.5.5.7.3.2"]);
        Assert.Equal("Server Authentication", PkiFlags.KnownEku["1.3.6.1.5.5.7.3.1"]);
        Assert.Equal("Smart Card Logon", PkiFlags.KnownEku["1.3.6.1.4.1.311.20.2.2"]);
        Assert.Equal("PKINIT Client Authentication", PkiFlags.KnownEku["1.3.6.1.5.2.3.4"]);
        Assert.Equal("KDC Authentication", PkiFlags.KnownEku["1.3.6.1.5.2.3.5"]);
        Assert.Equal("Certificate Request Agent", PkiFlags.KnownEku["1.3.6.1.4.1.311.20.2.1"]);
        Assert.Equal("Any Purpose", PkiFlags.KnownEku["2.5.29.37.0"]);
        Assert.Equal("Secure Email", PkiFlags.KnownEku["1.3.6.1.5.5.7.3.4"]);
        Assert.Equal("Encrypting File System", PkiFlags.KnownEku["1.3.6.1.4.1.311.10.3.4"]);
        Assert.Equal("Key Recovery Agent", PkiFlags.KnownEku["1.3.6.1.4.1.311.21.6"]);
        Assert.All(PkiFlags.KnownEku.Keys, oid => Assert.Matches(@"^[0-2](\.(0|[1-9][0-9]*))+$", oid));
        Assert.Equal(PkiFlags.KnownEku.Count, PkiFlags.KnownEku.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AnEmptyEkuSet_IsAnyPurpose_AndSatisfiesEveryPredicate()
    {
        string[] none = [];

        Assert.True(PkiFlags.IsAnyPurpose(none));
        Assert.True(PkiFlags.IsClientAuth(none));
        Assert.True(PkiFlags.IsServerAuth(none));
        Assert.True(PkiFlags.IsEnrollmentAgent(none));

        string[] anyPurpose = [PkiFlags.AnyPurpose];
        Assert.True(PkiFlags.IsAnyPurpose(anyPurpose));
        Assert.True(PkiFlags.IsClientAuth(anyPurpose));
        Assert.True(PkiFlags.IsServerAuth(anyPurpose));
        Assert.True(PkiFlags.IsEnrollmentAgent(anyPurpose));
    }

    [Theory]
    [InlineData("1.3.6.1.5.5.7.3.2", false, true, false, false)]
    [InlineData("1.3.6.1.4.1.311.20.2.2", false, true, false, false)]
    [InlineData("1.3.6.1.5.2.3.4", false, true, false, false)]
    [InlineData("1.3.6.1.5.5.7.3.1", false, false, true, false)]
    [InlineData("1.3.6.1.4.1.311.20.2.1", false, false, false, true)]
    [InlineData("1.3.6.1.5.2.3.5", false, false, false, false)]
    [InlineData("1.3.6.1.5.5.7.3.4", false, false, false, false)]
    [InlineData("1.3.6.1.5.5.7.3.21", false, false, false, false)]
    [InlineData("2.5.29.37", false, false, false, false)]
    [InlineData(" 2.5.29.37.0 ", true, true, true, true)]
    public void EachPurpose_SatisfiesItsPredicatesOnly(string oid, bool anyPurpose, bool clientAuth, bool serverAuth, bool enrollmentAgent)
    {
        string[] ekus = [oid];

        Assert.Equal(anyPurpose, PkiFlags.IsAnyPurpose(ekus));
        Assert.Equal(clientAuth, PkiFlags.IsClientAuth(ekus));
        Assert.Equal(serverAuth, PkiFlags.IsServerAuth(ekus));
        Assert.Equal(enrollmentAgent, PkiFlags.IsEnrollmentAgent(ekus));
    }

    [Fact]
    public void Predicates_LookAtTheWholeSet()
    {
        // The default Kerberos Authentication template: client and server auth, smart-card logon, KDC authentication.
        string[] kerberos = [PkiFlags.ClientAuthentication, PkiFlags.ServerAuthentication, PkiFlags.SmartCardLogon, PkiFlags.KdcAuthentication];

        Assert.False(PkiFlags.IsAnyPurpose(kerberos));
        Assert.True(PkiFlags.IsClientAuth(kerberos));
        Assert.True(PkiFlags.IsServerAuth(kerberos));
        Assert.False(PkiFlags.IsEnrollmentAgent(kerberos));
        Assert.Throws<ArgumentNullException>(() => PkiFlags.IsClientAuth(null!));
    }

    [Fact]
    public void EffectiveEku_AndEkus_CanDisagree_OnTheSamePredicate()
    {
        // A schema-2 template whose pKIExtendedKeyUsage still says Secure Email but whose application policies (what the CA
        // issues) say Client Authentication: the ESC rules evaluate both (2.2), so this one is client-auth via EffectiveEku.
        var template = new CertificateTemplate
        {
            Dn = "CN=Poczta-Zmieniona,CN=Certificate Templates,CN=Public Key Services,CN=Services,CN=Configuration,DC=contoso,DC=test",
            Cn = "Poczta-Zmieniona",
            SchemaVersion = 2,
            Ekus = ["1.3.6.1.5.5.7.3.4"],
            ApplicationPolicies = [PkiFlags.ClientAuthentication],
        };

        Assert.False(PkiFlags.IsClientAuth(template.Ekus));
        Assert.True(PkiFlags.IsClientAuth(template.EffectiveEku));

        // Schema 1: the CA issues pKIExtendedKeyUsage, the application policies do not count.
        var v1 = template with { SchemaVersion = 1 };
        Assert.Same(v1.Ekus, v1.EffectiveEku);
        Assert.False(PkiFlags.IsClientAuth(v1.EffectiveEku));

        // Schema 2 without application policies falls back to pKIExtendedKeyUsage; an empty one there is Any Purpose.
        var noPolicies = template with { ApplicationPolicies = [], Ekus = [] };
        Assert.True(PkiFlags.IsAnyPurpose(noPolicies.EffectiveEku));
    }
}
