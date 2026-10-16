using AdMng.Core.Ldap;

namespace AdMng.Directory.IntegrationTests.Unit;

public sealed class LogPrivacyTests
{
    [Theory]
    [InlineData("(sAMAccountName=jan.kowalski)", "(sAMAccountName=…)")]
    [InlineData("(&(objectCategory=person)(department=Księgowość))", "(&(objectCategory=…)(department=…))")]
    [InlineData("(|(cn=Jan*)(!(mail=*@contoso.com)))", "(|(cn=…)(!(mail=…)))")]
    [InlineData("(userAccountControl:1.2.840.113556.1.4.803:=2)", "(userAccountControl:1.2.840.113556.1.4.803:=…)")]
    [InlineData("(whenChanged>=20261001000000.0Z)", "(whenChanged>=…)")]
    [InlineData("(sn~=Kowalsky)", "(sn~=…)")]
    [InlineData("(objectClass=*)", "(objectClass=…)")]
    public void FilterValues_AreMasked(string filter, string expected)
    {
        Assert.Equal(expected, Log.MaskFilter(filter));
    }

    [Fact]
    public void EscapedParenthesesInValues_DoNotLeak()
    {
        var filter = LdapFilter.And(LdapFilter.Equal("cn", "Kowalski (Kraków)"), LdapFilter.InChain("memberOf", "CN=GG_IT,DC=contoso,DC=com"));

        var masked = Log.MaskFilter(filter);

        Assert.Equal("(&(cn=…)(memberOf:1.2.840.113556.1.4.1941:=…))", masked);
        Assert.DoesNotContain("Kraków", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("GG_IT", masked, StringComparison.Ordinal);
    }
}
