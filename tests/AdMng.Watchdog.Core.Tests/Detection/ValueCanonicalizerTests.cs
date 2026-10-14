using System.Security.Cryptography;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Core.Tests.Detection;

public class ValueCanonicalizerTests
{
    private static readonly ValueCanonicalizer Canonicalizer = new(FakeSchema.Create());

    private static AttributeValues Values(string name, params object[] values) => new(name, values);

    [Fact]
    public void DnValues_CompareByGuid_DisplayTheDn()
    {
        var guid = new Guid("a0d10000-0000-4000-8000-000000000042");
        var value = $"<GUID={guid:D}>;<SID=S-1-5-21-1-2-3-1105>;CN=Jan Kowalski,OU=IT,DC=contoso,DC=test";

        var attribute = Canonicalizer.Canonicalize(Attr.Manager, Values(Attr.Manager, value));

        Assert.NotNull(attribute);
        Assert.Equal(AttributeValueKind.Dn, attribute.Kind);
        Assert.Equal($"guid:{guid:D}", Assert.Single(attribute.Values));
        Assert.Equal("CN=Jan Kowalski,OU=IT,DC=contoso,DC=test", Assert.Single(attribute.DisplayValues));
    }

    [Fact]
    public void DnValues_RenamedReferenceKeepsTheComparisonValue()
    {
        var guid = Guid.NewGuid();
        var before = Canonicalizer.Canonicalize(Attr.Manager, Values(Attr.Manager, $"<GUID={guid:D}>;CN=Paweł Lewandowski,OU=Sprzedaż,DC=contoso,DC=test"));
        var after = Canonicalizer.Canonicalize(Attr.Manager, Values(Attr.Manager, $"<GUID={guid:D}>;CN=Paweł Lewandowski,OU=Zarząd,DC=contoso,DC=test"));

        Assert.Equal(before!.Values, after!.Values);
        Assert.NotEqual(before.DisplayValues, after.DisplayValues);
    }

    [Fact]
    public void PlainDn_WithoutExtendedForm_FallsBackToNormalizedDn()
    {
        var attribute = Canonicalizer.Canonicalize(Attr.Manager, Values(Attr.Manager, "cn=Łukasz Wiśniewski,ou=IT,dc=contoso,dc=test"));

        Assert.Equal("dn:CN=ŁUKASZ WIŚNIEWSKI,OU=IT,DC=CONTOSO,DC=TEST", Assert.Single(attribute!.Values));
    }

    [Fact]
    public void KeyCredentialLink_HashesTheBinaryPartOnly()
    {
        var oldDn = Canonicalizer.Canonicalize(WatchdogAttributes.KeyCredentialLink,
            Values(WatchdogAttributes.KeyCredentialLink, "B:8:0102AABB:<GUID=11111111-1111-1111-1111-111111111111>;CN=Jan,OU=IT,DC=contoso,DC=test"));
        var renamed = Canonicalizer.Canonicalize(WatchdogAttributes.KeyCredentialLink,
            Values(WatchdogAttributes.KeyCredentialLink, "B:8:0102aabb:CN=Jan Kowalski,OU=Informatyka,DC=contoso,DC=test"));
        var otherKey = Canonicalizer.Canonicalize(WatchdogAttributes.KeyCredentialLink,
            Values(WatchdogAttributes.KeyCredentialLink, "B:8:0102AABC:CN=Jan,OU=IT,DC=contoso,DC=test"));

        Assert.StartsWith("sha256:", Assert.Single(oldDn!.Values), StringComparison.Ordinal);
        Assert.Equal(oldDn.Values, renamed!.Values);
        Assert.NotEqual(oldDn.Values, otherKey!.Values);
        Assert.Equal(AttributeValueKind.Hash, oldDn.Kind);
    }

    [Fact]
    public void Sids_AreText_FileTimesAndNumbersInvariantDecimal()
    {
        var sid = AdConvert.SidToBytes("S-1-5-21-3623811015-3361044348-30300820-1105");

        var history = Canonicalizer.Canonicalize(Attr.SidHistory, Values(Attr.SidHistory, sid));
        var pwdLastSet = Canonicalizer.Canonicalize(Attr.PwdLastSet, Values(Attr.PwdLastSet, "0134044064000000000"));
        var uac = Canonicalizer.Canonicalize(Attr.UserAccountControl, Values(Attr.UserAccountControl, "512"));

        Assert.Equal("S-1-5-21-3623811015-3361044348-30300820-1105", Assert.Single(history!.Values));
        Assert.Equal(AttributeValueKind.Sid, history.Kind);
        Assert.Equal("134044064000000000", Assert.Single(pwdLastSet!.Values));
        Assert.Equal(AttributeValueKind.FileTime, pwdLastSet.Kind);
        Assert.Equal(AttributeValueKind.Flags, uac!.Kind);
    }

    [Fact]
    public void GeneralizedTime_IsIso8601Utc_BooleansUpperCase()
    {
        var time = Canonicalizer.Canonicalize(Attr.WhenCreated, Values(Attr.WhenCreated, "20261014150644.0Z"));
        var flag = Canonicalizer.Canonicalize(Attr.ShowInAdvancedViewOnly, Values(Attr.ShowInAdvancedViewOnly, "true"));

        Assert.Equal("2026-10-14T15:06:44Z", Assert.Single(time!.Values));
        Assert.Equal("TRUE", Assert.Single(flag!.Values));
    }

    [Fact]
    public void MultiValued_IsSortedAndDistinct_StringsKeepCase()
    {
        var attribute = Canonicalizer.Canonicalize(Attr.ProxyAddresses,
            Values(Attr.ProxyAddresses, "smtp:jan@contoso.test", "SMTP:Jan.Kowalski@contoso.test", "smtp:jan@contoso.test"));

        Assert.Equal(["SMTP:Jan.Kowalski@contoso.test", "smtp:jan@contoso.test"], attribute!.Values);
    }

    [Fact]
    public void LogonHours_IsLowerCaseHex_CertificatesHashWithThumbprintDisplay()
    {
        var hours = new byte[21];
        hours[1] = 0xFF;
        var certificate = new byte[] { 0x30, 0x82, 0x01, 0x0A };

        var logonHours = Canonicalizer.Canonicalize(Attr.LogonHours, Values(Attr.LogonHours, hours));
        var cert = Canonicalizer.Canonicalize(WatchdogAttributes.UserCertificate, Values(WatchdogAttributes.UserCertificate, certificate));

        Assert.Equal("00ff" + new string('0', 38), Assert.Single(logonHours!.Values));
        Assert.Equal(ValueCanonicalizer.Hash(certificate), Assert.Single(cert!.Values));
        Assert.Equal(Convert.ToHexString(SHA1.HashData(certificate)), Assert.Single(cert.DisplayValues));
    }

    [Fact]
    public void SecurityDescriptor_IsSummarised()
    {
        var builder = new FakeDirectoryBuilder();
        var sd = builder.DefaultSecurityDescriptor();

        var attribute = Canonicalizer.Canonicalize(Attr.NtSecurityDescriptor, Values(Attr.NtSecurityDescriptor, sd));

        Assert.StartsWith("sha256:", attribute!.Values[0], StringComparison.Ordinal);
        Assert.Contains($"owner:{builder.DomainSid}-512", attribute.Values);
        Assert.Equal(2, attribute.Values.Count(v => v.StartsWith("ace:", StringComparison.Ordinal)));
    }

    [Fact]
    public void MemberReference_KeysByGuidThenSidThenDn()
    {
        var guid = Guid.NewGuid();

        var byGuid = ValueCanonicalizer.ToMemberReference($"<GUID={guid:D}>;<SID=S-1-5-21-1-2-3-1105>;CN=Jan,DC=contoso,DC=test");
        var bySid = ValueCanonicalizer.ToMemberReference("<SID=S-1-5-21-9-9-9-1105>;CN=S-1-5-21-9-9-9-1105,CN=ForeignSecurityPrincipals,DC=contoso,DC=test");
        var byDn = ValueCanonicalizer.ToMemberReference("CN=Jan,DC=contoso,DC=test");

        Assert.Equal(guid.ToString("N"), byGuid.Key);
        Assert.Equal("S-1-5-21-1-2-3-1105", byGuid.Sid);
        Assert.Equal("sid:S-1-5-21-9-9-9-1105", bySid.Key);
        Assert.Equal("dn:CN=JAN,DC=CONTOSO,DC=TEST", byDn.Key);
    }

    [Fact]
    public void NoValues_IsNull()
    {
        Assert.Null(Canonicalizer.Canonicalize(Attr.Department, null));
        Assert.Null(Canonicalizer.Canonicalize(Attr.Department, Values(Attr.Department)));
    }
}
