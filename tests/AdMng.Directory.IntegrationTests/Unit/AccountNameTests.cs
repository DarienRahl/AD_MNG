namespace AdMng.Directory.IntegrationTests.Unit;

public sealed class AccountNameTests
{
    [Fact]
    public void DownLevelName_IsSplitIntoDomainAndUser()
    {
        var account = AccountName.Parse(@"CONTOSO\jan.kowalski");

        Assert.Equal(AccountNameForm.DownLevel, account.Form);
        Assert.Equal("jan.kowalski", account.User);
        Assert.Equal("CONTOSO", account.Domain);
        Assert.Equal(@"CONTOSO\jan.kowalski", account.ToString());
        Assert.Equal(@"CONTOSO\jan.kowalski", account.ToSimpleBindName("contoso.com"));
        Assert.Equal(("jan.kowalski", "CONTOSO"), account.ToNegotiateParts("contoso.com"));
    }

    [Fact]
    public void UserPrincipalName_IsKeptWhole()
    {
        var account = AccountName.Parse("  łukasz.żółć@contoso.com ");

        Assert.Equal(AccountNameForm.UserPrincipalName, account.Form);
        Assert.Equal("łukasz.żółć@contoso.com", account.User);
        Assert.Equal("contoso.com", account.Domain);
        Assert.Equal("łukasz.żółć@contoso.com", account.ToSimpleBindName("other.com"));
        Assert.Equal(("łukasz.żółć@contoso.com", (string?)null), account.ToNegotiateParts("other.com"));
    }

    [Fact]
    public void PlainName_BecomesImplicitUpnForSimpleBind()
    {
        var account = AccountName.Parse("administrator");

        Assert.Equal(AccountNameForm.Plain, account.Form);
        Assert.Null(account.Domain);
        Assert.Equal("administrator@contoso.test", account.ToSimpleBindName("contoso.test"));
        Assert.Equal("administrator", account.ToSimpleBindName(null));
        Assert.Equal(("administrator", "contoso.test"), account.ToNegotiateParts("contoso.test"));
        Assert.Equal(("administrator", (string?)null), account.ToNegotiateParts(null));
    }

    [Fact]
    public void DistinguishedName_IsRecognised()
    {
        var account = AccountName.Parse("CN=Jan Kowalski,OU=Księgowość,DC=contoso,DC=com");

        Assert.Equal(AccountNameForm.DistinguishedName, account.Form);
        Assert.Equal("CN=Jan Kowalski,OU=Księgowość,DC=contoso,DC=com", account.ToSimpleBindName("contoso.com"));
    }

    [Fact]
    public void UpnWithEqualsSign_IsNotMistakenForADn()
    {
        var account = AccountName.Parse("a=b@contoso.com");

        Assert.Equal(AccountNameForm.UserPrincipalName, account.Form);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\user")]
    [InlineData(@"DOMAIN\")]
    [InlineData(@"DOMAIN\a\b")]
    [InlineData("@contoso.com")]
    [InlineData("user@")]
    public void MalformedNames_AreRejected(string name)
    {
        Assert.Throws<ArgumentException>(() => AccountName.Parse(name));
    }

    [Fact]
    public void Null_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => AccountName.Parse(null!));
    }
}
