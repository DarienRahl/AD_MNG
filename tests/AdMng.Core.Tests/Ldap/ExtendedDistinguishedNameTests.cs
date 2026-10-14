using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;

namespace AdMng.Core.Tests.Ldap;

public class ExtendedDistinguishedNameTests
{
    private const string UserDn = "CN=Łukasz Wiśniewski,OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test";
    private const string UserSid = "S-1-5-21-1004336348-1177238915-682003330-1105";
    private static readonly Guid UserGuid = Guid.Parse("9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f");

    [Fact]
    public void TryParse_ReadsGuidSidAndDn()
    {
        var text = $"<GUID={UserGuid:D}>;<SID={UserSid}>;{UserDn}";

        Assert.True(ExtendedDistinguishedName.TryParse(text, out var value));

        Assert.Equal(UserGuid, value.Guid);
        Assert.Equal(UserSid, value.Sid);
        Assert.Equal(UserDn, value.Dn);
    }

    [Fact]
    public void TryParse_GuidOnly_ForObjectsWithoutSid()
    {
        Assert.True(ExtendedDistinguishedName.TryParse($"<GUID={UserGuid:D}>;OU=Sprzedaż,DC=contoso,DC=test", out var value));

        Assert.Equal(UserGuid, value.Guid);
        Assert.Null(value.Sid);
        Assert.Equal("OU=Sprzedaż,DC=contoso,DC=test", value.Dn);
    }

    [Fact]
    public void TryParse_PlainDn_HasNoGuidOrSid()
    {
        Assert.True(ExtendedDistinguishedName.TryParse(UserDn, out var value));

        Assert.Null(value.Guid);
        Assert.Null(value.Sid);
        Assert.Equal(UserDn, value.Dn);
    }

    [Fact]
    public void TryParse_AcceptsTheBinaryHexForm()
    {
        var guidHex = Convert.ToHexString(UserGuid.ToByteArray());
        var sidHex = Convert.ToHexString(AdConvert.SidToBytes(UserSid));

        Assert.True(ExtendedDistinguishedName.TryParse($"<GUID={guidHex}>;<SID={sidHex}>;{UserDn}", out var value));

        Assert.Equal(UserGuid, value.Guid);
        Assert.Equal(UserSid, value.Sid);
        Assert.Equal(UserDn, value.Dn);
    }

    [Fact]
    public void TryParse_KeepsEscapedSeparatorsInTheDn()
    {
        const string dn = @"CN=Nowak\; Zofia \<Żak\>,OU=Księgowość,DC=contoso,DC=test";

        Assert.True(ExtendedDistinguishedName.TryParse($"<GUID={UserGuid:D}>;{dn}", out var value));

        Assert.Equal(dn, value.Dn);
        Assert.Equal("Nowak; Zofia <Żak>", DistinguishedName.Parse(value.Dn).Name);
    }

    [Fact]
    public void TryParse_AcceptsSidBeforeGuid()
    {
        Assert.True(ExtendedDistinguishedName.TryParse($"<SID={UserSid}>;<GUID={UserGuid:D}>;{UserDn}", out var value));

        Assert.Equal(UserGuid, value.Guid);
        Assert.Equal(UserSid, value.Sid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<GUID=not-a-guid>;CN=x,DC=contoso,DC=test")]
    [InlineData("<GUID=9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f;CN=x,DC=contoso,DC=test")]
    [InlineData("<GUID=9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f>CN=x,DC=contoso,DC=test")]
    [InlineData("<GUID=9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f>;<GUID=9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f>;CN=x,DC=x")]
    [InlineData("<WKGUID=aa312825768811d1aded00c04fd8d5cd,DC=contoso,DC=test>;CN=x,DC=x")]
    [InlineData("<SID=S-1-5-21-abc>;CN=x,DC=contoso,DC=test")]
    [InlineData("<SID=0102>;CN=x,DC=contoso,DC=test")]
    [InlineData("<>;CN=x,DC=contoso,DC=test")]
    [InlineData("to nie jest DN")]
    public void TryParse_RejectsMalformedValues_WithoutThrowing(string? text)
    {
        Assert.False(ExtendedDistinguishedName.TryParse(text, out var value));
        Assert.Equal(default, value);
    }

    [Fact]
    public void ToString_FormatsGuidThenSidThenDn_AndRoundTrips()
    {
        var value = new ExtendedDistinguishedName(UserGuid, UserSid, UserDn);

        var text = value.ToString();

        Assert.Equal($"<GUID={UserGuid:D}>;<SID={UserSid}>;{UserDn}", text);
        Assert.True(ExtendedDistinguishedName.TryParse(text, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void ToString_WithoutComponents_IsThePlainDn()
    {
        Assert.Equal(UserDn, new ExtendedDistinguishedName(null, null, UserDn).ToString());
        Assert.Equal($"<GUID={UserGuid:D}>;{UserDn}", new ExtendedDistinguishedName(UserGuid, null, UserDn).ToString());
    }
}
