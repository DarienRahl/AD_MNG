using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class ConfigTokensTests
{
    private const string DomainSid = "S-1-5-21-1004336348-1177238915-682003330";
    private const string RootSid = "S-1-5-21-2222222222-3333333333-4444444444";

    private static readonly ConfigTokens Known = new() { DomainDn = "DC=łódź,DC=contoso,DC=test", DomainSid = DomainSid, RootDomainSid = RootSid };

    [Theory]
    [InlineData("{domain}", "DC=łódź,DC=contoso,DC=test")]
    [InlineData("OU=Domain Controllers,{domain}", "OU=Domain Controllers,DC=łódź,DC=contoso,DC=test")]
    [InlineData("  CN=AdminSDHolder,CN=System,{DOMAIN}  ", "CN=AdminSDHolder,CN=System,DC=łódź,DC=contoso,DC=test")]
    [InlineData("OU=Księgowość,DC=inna,DC=test", "OU=Księgowość,DC=inna,DC=test")]
    public void ExpandDn_ReplacesTheDomainToken(string value, string expected) =>
        Assert.Equal(expected, Known.ExpandDn(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OU=Serwery,{domain}")]
    public void ExpandDn_IsNull_WhenEmptyOrTheDomainIsUnknown(string? value) =>
        Assert.Null(ConfigTokens.Unknown.ExpandDn(value));

    [Fact]
    public void ExpandDn_KeepsLiteralDns_WhenTheDomainIsUnknown() =>
        Assert.Equal("OU=Kraków,DC=contoso,DC=test", ConfigTokens.Unknown.ExpandDn("OU=Kraków,DC=contoso,DC=test"));

    [Theory]
    [InlineData("{domainSid}-512", DomainSid + "-512")]
    [InlineData("{rootDomainSid}-519", RootSid + "-519")]
    [InlineData("S-1-5-32-544", "S-1-5-32-544")]
    [InlineData(" {DOMAINSID}-525 ", DomainSid + "-525")]
    public void ExpandSid_ProducesExactSids_WhenTheTokensAreKnown(string value, string expected)
    {
        var pattern = Known.ExpandSid(value);

        Assert.NotNull(pattern);
        Assert.Equal(expected, pattern.Value.Sid);
        Assert.True(pattern.Value.Matches(expected));
        Assert.True(pattern.Value.Matches(expected.ToLowerInvariant()));
        Assert.False(pattern.Value.Matches(expected + "1"));
    }

    [Fact]
    public void ExpandSid_MatchesTheRidWithinAnyDomain_WhenTheRootDomainSidIsUnreadable()
    {
        var tokens = Known with { RootDomainSid = null };

        var pattern = tokens.ExpandSid("{rootDomainSid}-519")!.Value;

        Assert.Null(pattern.Sid);
        Assert.Equal(519u, pattern.AnyDomainRid);
        Assert.True(pattern.Matches(RootSid + "-519"));
        Assert.True(pattern.Matches(DomainSid + "-519"));
        Assert.False(pattern.Matches(DomainSid + "-512"));
        Assert.False(pattern.Matches("S-1-5-32-519"));
        Assert.False(pattern.Matches("S-1-5-21-1-2-519"));
        Assert.False(pattern.Matches(null));
        Assert.Equal("S-1-5-21-*-519", pattern.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Domain Admins")]
    [InlineData("S-1-5")]
    [InlineData("{domainSid}-x")]
    [InlineData("{domainSid}-512-")]
    [InlineData("S-1-5-21-1-2-3-500;")]
    public void ExpandSid_RejectsValuesThatAreNoSids(string? value)
    {
        Assert.Null(Known.ExpandSid(value));
        Assert.Null(ConfigTokens.Unknown.ExpandSid(value));
    }

    [Fact]
    public void WithDomainFallback_TakesTheDomainComponentsOfTheObject()
    {
        var tokens = ConfigTokens.Unknown.WithDomainFallback("CN=Łukasz Wiśniewski,OU=Kraków,DC=contoso,DC=test");

        Assert.Equal("DC=contoso,DC=test", tokens.DomainDn);
        Assert.Same(Known, Known.WithDomainFallback("CN=x,DC=other,DC=test"));
        Assert.Same(ConfigTokens.Unknown, ConfigTokens.Unknown.WithDomainFallback("not a dn"));
    }

    [Theory]
    [InlineData("{domain}", true)]
    [InlineData("x-{rootdomainsid}", true)]
    [InlineData("{domainSid}-512", true)]
    [InlineData("DC=contoso,DC=test", false)]
    [InlineData(null, false)]
    public void ContainsToken_DetectsEveryToken(string? value, bool expected) =>
        Assert.Equal(expected, ConfigTokens.ContainsToken(value));

    [Fact]
    public void Placeholder_ExpandsEveryDefaultValueToAValidDnOrSid()
    {
        var defaults = new WatchdogConfiguration();

        Assert.All(defaults.Directory.CriticalObjects, dn => Assert.True(DistinguishedName.TryParse(ConfigTokens.Placeholder.ExpandDn(dn), out _)));
        Assert.All(defaults.PrivilegedGroups.CriticalSids.Concat(defaults.PrivilegedGroups.HighSids),
            sid => Assert.NotNull(ConfigTokens.Placeholder.ExpandSid(sid)?.Sid));
    }
}
