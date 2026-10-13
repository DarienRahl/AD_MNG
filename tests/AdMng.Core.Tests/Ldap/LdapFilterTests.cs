using AdMng.Core.Ldap;

namespace AdMng.Core.Tests.Ldap;

public class LdapFilterTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a*b", @"a\2ab")]
    [InlineData("(x)", @"\28x\29")]
    [InlineData(@"back\slash", @"back\5cslash")]
    [InlineData("Żółć", "Żółć")]
    public void Escape_EscapesSpecialCharacters(string value, string expected)
    {
        Assert.Equal(expected, LdapFilter.Escape(value));
    }

    [Fact]
    public void Builders_EscapeUserInput()
    {
        Assert.Equal(@"(sAMAccountName=jan\2a)", LdapFilter.Equal("sAMAccountName", "jan*"));
        Assert.Equal(@"(cn=Kow\28\29*)", LdapFilter.StartsWith("cn", "Kow()"));
        Assert.Equal(@"(anr=Jan Kow)", LdapFilter.Anr("Jan Kow"));
    }

    [Fact]
    public void Combine_FlattensSingleAndSkipsEmpty()
    {
        Assert.Equal("(a=1)", LdapFilter.And("(a=1)", null, " "));
        Assert.Equal("(&(a=1)(b=2))", LdapFilter.And("(a=1)", "b=2"));
        Assert.Equal("(|(a=1)(b=2))", LdapFilter.Or("(a=1)", "(b=2)"));
        Assert.Equal("(!(a=1))", LdapFilter.Not("a=1"));
    }

    [Fact]
    public void MatchingRules_UseAdOids()
    {
        Assert.Equal("(userAccountControl:1.2.840.113556.1.4.803:=2)", LdapFilter.BitAnd("userAccountControl", 2));
        Assert.Equal("(memberOf:1.2.840.113556.1.4.1941:=CN=G\\28x\\29,DC=x)", LdapFilter.InChain("memberOf", "CN=G(x),DC=x"));
    }

    [Fact]
    public void EqualGuid_UsesBinaryEscapingInAdByteOrder()
    {
        var guid = new Guid("01020304-0506-0708-090a-0b0c0d0e0f10");
        Assert.Equal(@"(objectGUID=\04\03\02\01\06\05\08\07\09\0a\0b\0c\0d\0e\0f\10)", LdapFilter.EqualGuid("objectGUID", guid));
    }

    [Fact]
    public void InvalidAttributeName_Throws()
    {
        Assert.Throws<ArgumentException>(() => LdapFilter.Equal("cn)(x=*", "v"));
    }

    [Theory]
    [InlineData("(objectClass=user)", null)]
    [InlineData("objectClass=user", null)]
    [InlineData("(&(a=1)(|(b=2)(c=3)))", null)]
    [InlineData(@"(cn=a\2a)", null)]
    [InlineData("(&(a=1)", "Unbalanced parentheses.")]
    [InlineData("(a=1))", "Unexpected text after position 4.")]
    [InlineData("()", "Empty component at position 0.")]
    [InlineData(@"(cn=a\zz)", "Invalid escape sequence at position 5.")]
    [InlineData("", "Filter is empty.")]
    public void Validate_ReportsSyntaxProblems(string filter, string? expected)
    {
        Assert.Equal(expected, LdapFilter.Validate(filter));
    }
}
