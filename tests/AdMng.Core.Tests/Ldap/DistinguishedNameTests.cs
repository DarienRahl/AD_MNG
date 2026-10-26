using AdMng.Core.Ldap;

namespace AdMng.Core.Tests.Ldap;

public class DistinguishedNameTests
{
    [Fact]
    public void Parse_SplitsComponentsLeafFirst()
    {
        var dn = DistinguishedName.Parse("CN=Jan Kowalski,OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test");

        Assert.Equal(5, dn.Rdns.Count);
        Assert.Equal(new Rdn("CN", "Jan Kowalski"), dn.Leaf);
        Assert.Equal("Jan Kowalski", dn.Name);
        Assert.Equal("OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test", dn.Parent!.ToString());
        Assert.Equal("contoso.test", dn.DnsDomain);
    }

    [Theory]
    [InlineData(@"CN=Kowalski\, Jan,OU=Users,DC=x", "Kowalski, Jan")]
    [InlineData(@"CN=a\+b\=c\<d\>e\;f\""g,DC=x", "a+b=c<d>e;f\"g")]
    [InlineData(@"CN=\#hash,DC=x", "#hash")]
    [InlineData(@"CN=\ lead and trail\ ,DC=x", " lead and trail ")]
    [InlineData(@"CN=back\\slash,DC=x", @"back\slash")]
    [InlineData(@"OU=Ksi\C4\99gowo\C5\9B\C4\87,DC=x", "Księgowość")]
    [InlineData(@"CN=  spaced  ,DC=x", "spaced")]
    public void Parse_UnescapesValues(string text, string expectedName)
    {
        Assert.Equal(expectedName, DistinguishedName.Parse(text).Name);
    }

    [Theory]
    [InlineData("Kowalski, Jan", @"Kowalski\, Jan")]
    [InlineData("#first", @"\#first")]
    [InlineData(" both ", @"\ both\ ")]
    [InlineData("a=b+c", @"a\=b\+c")]
    [InlineData("Księgowość", "Księgowość")]
    public void EscapeValue_FollowsRfc4514(string value, string expected)
    {
        Assert.Equal(expected, DistinguishedName.EscapeValue(value));
    }

    [Theory]
    [InlineData("Kowalski, Jan")]
    [InlineData("#x")]
    [InlineData(" spaced ")]
    [InlineData(@"a\b""c<d>e;f=g+h")]
    [InlineData("Żółć")]
    public void EscapeAndParse_RoundTrips(string value)
    {
        var dn = DistinguishedName.Parse("DC=x").Child("CN", value);
        Assert.Equal(value, DistinguishedName.Parse(dn.ToString()).Name);
    }

    [Theory]
    [InlineData("CN=x,")]
    [InlineData("CN")]
    [InlineData("=x")]
    [InlineData(@"CN=x\")]
    public void TryParse_RejectsMalformed(string text)
    {
        Assert.False(DistinguishedName.TryParse(text, out _));
    }

    [Fact]
    public void Equality_IsCaseInsensitive()
    {
        var a = DistinguishedName.Parse("CN=Jan,OU=IT,DC=Contoso,DC=Test");
        var b = DistinguishedName.Parse("cn=JAN, ou=it, dc=contoso, dc=test");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(DistinguishedName.AreEqual(a.ToString(), "cn=JAN,ou=it,dc=contoso,dc=test"));
    }

    [Fact]
    public void IsDescendantOf_ChecksAncestry()
    {
        var ou = DistinguishedName.Parse("OU=IT,DC=contoso,DC=test");
        var user = DistinguishedName.Parse("CN=Jan,OU=IT,DC=contoso,DC=test");

        Assert.True(user.IsDescendantOf(ou));
        Assert.False(ou.IsDescendantOf(user));
        Assert.False(ou.IsDescendantOf(ou));
    }

    [Fact]
    public void MoveAndRename_ProduceExpectedDns()
    {
        var user = DistinguishedName.Parse("CN=Jan,OU=IT,DC=contoso,DC=test");
        var target = DistinguishedName.Parse("OU=Wyłączone,DC=contoso,DC=test");

        Assert.Equal("CN=Jan,OU=Wyłączone,DC=contoso,DC=test", user.WithParent(target).ToString());
        Assert.Equal(@"CN=Nowak\, Jan,OU=IT,DC=contoso,DC=test", user.WithLeafValue("Nowak, Jan").ToString());
    }

    [Fact]
    public void ToCanonicalName_MatchesAdsiFormat()
    {
        var dn = DistinguishedName.Parse("CN=Jan/Kowalski,OU=IT,OU=Firma,DC=contoso,DC=test");

        Assert.Equal(@"contoso.test/Firma/IT/Jan\/Kowalski", dn.ToCanonicalName());
        Assert.Equal("contoso.test/", DistinguishedName.Parse("DC=contoso,DC=test").ToCanonicalName());
    }

    [Theory]
    [InlineData("CN=Łukasz Wiśniewski,OU=IT,OU=Firma,DC=contoso,DC=test", "CN=Łukasz Wiśniewski,OU=IT,OU=Firma,{domain}")]
    [InlineData("DC=contoso,DC=test", "{domain}")]
    [InlineData(@"CN=Kowalski\, Jan,ou=Sprzedaż,dc=CONTOSO,dc=TEST", @"CN=Kowalski\, Jan,ou=Sprzedaż,{domain}")]
    [InlineData("CN=Schema,CN=Configuration,DC=contoso,DC=test", "CN=Schema,CN=Configuration,{domain}")]
    [InlineData("CN=Jan Kowalski,OU=IT,DC=fabrikam,DC=test", "CN=Jan Kowalski,OU=IT,DC=fabrikam,DC=test")]
    [InlineData("not a dn", "not a dn")]
    public void ToPortable_ReplacesTheNamingContext(string dn, string expected)
    {
        Assert.Equal(expected, DistinguishedName.ToPortable(dn, "DC=contoso,DC=test"));
    }

    [Fact]
    public void Portable_RoundTripsToAnotherDomain()
    {
        var portable = DistinguishedName.ToPortable("CN=Żaneta Szymańska,OU=Sprzedaż,DC=contoso,DC=test", "DC=contoso,DC=test");

        Assert.Equal("CN=Żaneta Szymańska,OU=Sprzedaż,DC=fabrikam,DC=test", DistinguishedName.FromPortable(portable, "DC=fabrikam,DC=test"));
        Assert.Equal("{domain}", DistinguishedName.DomainPlaceholder);
    }
}
