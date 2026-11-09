using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>
/// Pins the certificate-template flag names of <see cref="FlagNames"/> as they were before the names moved to
/// <c>AdMng.Core.Pki.PkiFlags</c> (docs/dev/p-certificate-services.md decision 8, 2.10): every single bit and two mixed
/// values of <c>msPKI-Certificate-Name-Flag</c> and <c>msPKI-Enrollment-Flag</c>, and that no other template attribute is a
/// flag attribute. The delegation must keep exactly these bits in this order (no <c>CT_FLAG_SUBJECT_ALT_REQUIRE_SPN</c>,
/// none of <c>0x2000</c>–<c>0x40000</c>; unknown bits still <c>0x…</c>).
/// </summary>
public sealed class FlagNamesCertificateGoldenTests
{
    private const string NameFlag = "msPKI-Certificate-Name-Flag";
    private const string EnrollmentFlag = "msPKI-Enrollment-Flag";

    /// <summary>Bit 0 … bit 31 of <c>msPKI-Certificate-Name-Flag</c>, as decoded before the delegation.</summary>
    private static readonly string[] NameFlagBits =
    [
        "CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT", "0x2", "0x4", "CT_FLAG_OLD_CERT_SUPPLIES_SUBJECT_AND_ALT_NAME",
        "0x10", "0x20", "0x40", "0x80",
        "0x100", "0x200", "0x400", "0x800",
        "0x1000", "0x2000", "0x4000", "0x8000",
        "CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME", "0x20000", "0x40000", "0x80000",
        "0x100000", "0x200000", "CT_FLAG_SUBJECT_ALT_REQUIRE_DOMAIN_DNS", "0x800000",
        "CT_FLAG_SUBJECT_ALT_REQUIRE_DIRECTORY_GUID", "CT_FLAG_SUBJECT_ALT_REQUIRE_UPN", "CT_FLAG_SUBJECT_ALT_REQUIRE_EMAIL", "CT_FLAG_SUBJECT_ALT_REQUIRE_DNS",
        "CT_FLAG_SUBJECT_REQUIRE_DNS_AS_CN", "CT_FLAG_SUBJECT_REQUIRE_EMAIL", "CT_FLAG_SUBJECT_REQUIRE_COMMON_NAME", "CT_FLAG_SUBJECT_REQUIRE_DIRECTORY_PATH",
    ];

    /// <summary>Bit 0 … bit 31 of <c>msPKI-Enrollment-Flag</c>, as decoded before the delegation.</summary>
    private static readonly string[] EnrollmentFlagBits =
    [
        "CT_FLAG_INCLUDE_SYMMETRIC_ALGORITHMS", "CT_FLAG_PEND_ALL_REQUESTS", "CT_FLAG_PUBLISH_TO_KRA_CONTAINER", "CT_FLAG_PUBLISH_TO_DS",
        "CT_FLAG_AUTO_ENROLLMENT_CHECK_USER_DS_CERTIFICATE", "CT_FLAG_AUTO_ENROLLMENT", "CT_FLAG_PREVIOUS_APPROVAL_VALIDATE_REENROLLMENT", "0x80",
        "CT_FLAG_USER_INTERACTION_REQUIRED", "0x200", "CT_FLAG_REMOVE_INVALID_CERTIFICATE_FROM_PERSONAL_STORE", "CT_FLAG_ALLOW_ENROLL_ON_BEHALF_OF",
        "CT_FLAG_ADD_OCSP_NOCHECK", "0x2000", "0x4000", "0x8000",
        "0x10000", "0x20000", "0x40000", "CT_FLAG_NO_SECURITY_EXTENSION",
        "0x100000", "0x200000", "0x400000", "0x800000",
        "0x1000000", "0x2000000", "0x4000000", "0x8000000",
        "0x10000000", "0x20000000", "0x40000000", "0x80000000",
    ];

    public static TheoryData<int> Bits()
    {
        var data = new TheoryData<int>();
        for (var bit = 0; bit < 32; bit++)
        {
            data.Add(bit);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Bits))]
    public void EveryNameFlagBit_DecodesAsBefore(int bit)
    {
        Assert.Equal([NameFlagBits[bit]], FlagNames.Decode(NameFlag, 1L << bit));
        Assert.Equal([NameFlagBits[bit]], FlagNames.Decode("MSPKI-CERTIFICATE-NAME-FLAG", 1L << bit));
    }

    [Theory]
    [MemberData(nameof(Bits))]
    public void EveryEnrollmentFlagBit_DecodesAsBefore(int bit)
    {
        Assert.Equal([EnrollmentFlagBits[bit]], FlagNames.Decode(EnrollmentFlag, 1L << bit));
        Assert.Equal([EnrollmentFlagBits[bit]], FlagNames.Decode("mspki-enrollment-flag", 1L << bit));
    }

    [Fact]
    public void MixedNameFlags_KeepTableOrder_ThenUnknownBitsAscending()
    {
        // The default User template, read as the negative Integer AD returns.
        Assert.Equal(
            ["CT_FLAG_SUBJECT_ALT_REQUIRE_UPN", "CT_FLAG_SUBJECT_ALT_REQUIRE_EMAIL", "CT_FLAG_SUBJECT_REQUIRE_EMAIL", "CT_FLAG_SUBJECT_REQUIRE_DIRECTORY_PATH"],
            FlagNames.Decode(NameFlag, unchecked((int)0xA6000000)));
        Assert.Equal(
            ["CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT", "CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME", "0x2", "0x100", "0x800000"],
            FlagNames.Decode(NameFlag, 0x00810103));
    }

    [Fact]
    public void MixedEnrollmentFlags_KeepTableOrder_ThenUnknownBitsAscending()
    {
        Assert.Equal(
            ["CT_FLAG_INCLUDE_SYMMETRIC_ALGORITHMS", "CT_FLAG_PUBLISH_TO_DS", "CT_FLAG_AUTO_ENROLLMENT"],
            FlagNames.Decode(EnrollmentFlag, 0x29));
        Assert.Equal(
            ["CT_FLAG_PEND_ALL_REQUESTS", "CT_FLAG_NO_SECURITY_EXTENSION", "0x80", "0x200", "0x20000"],
            FlagNames.Decode(EnrollmentFlag, 0x000A0282));
    }

    [Fact]
    public void CertificateFlags_IgnoreBitsAbove32_AndDiffAsBefore()
    {
        Assert.Equal([], FlagNames.Decode(NameFlag, 0));
        Assert.Equal(["CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT"], FlagNames.Decode(NameFlag, 0x1_0000_0001L));

        var (added, removed) = FlagNames.Diff(EnrollmentFlag, 0x2, 0x80000);
        Assert.Equal(["CT_FLAG_NO_SECURITY_EXTENSION"], added);
        Assert.Equal(["CT_FLAG_PEND_ALL_REQUESTS"], removed);

        (added, removed) = FlagNames.Diff(NameFlag, null, 0x1);
        Assert.Equal(["CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT"], added);
        Assert.Empty(removed);
    }

    [Theory]
    [InlineData(NameFlag)]
    [InlineData(EnrollmentFlag)]
    [InlineData("MSPKI-ENROLLMENT-FLAG")]
    public void TheTwoCertificateFlagAttributes_AreFlagAttributes(string attribute) =>
        Assert.True(FlagNames.IsFlagAttribute(attribute));

    [Theory]
    [InlineData("msPKI-Private-Key-Flag")]
    [InlineData("flags")]
    [InlineData("msPKI-RA-Signature")]
    [InlineData("msPKI-Template-Schema-Version")]
    [InlineData("msPKI-Template-Minor-Revision")]
    [InlineData("revision")]
    [InlineData("pKIExtendedKeyUsage")]
    [InlineData("msPKI-Certificate-Application-Policy")]
    [InlineData("msPKI-RA-Application-Policies")]
    [InlineData("msPKI-Minimal-Key-Size")]
    [InlineData("pKIKeyUsage")]
    [InlineData("pKIExpirationPeriod")]
    public void NoOtherTemplateAttribute_IsAFlagAttribute(string attribute)
    {
        Assert.False(FlagNames.IsFlagAttribute(attribute));
        Assert.Equal([], FlagNames.Decode(attribute, 0x10));
    }
}
