using AdMng.Core.Messaging.Html;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>The domains offered by "Zezwól na domenę …".</summary>
public class LinkDomainSuggestionsTests
{
    [Theory]
    [InlineData("haslo.it.contoso.com.pl", new[] { "haslo.it.contoso.com.pl", "it.contoso.com.pl", "contoso.com.pl" })]
    [InlineData("portal.contoso.pl", new[] { "portal.contoso.pl", "contoso.pl" })]
    [InlineData("www.gov.pl", new[] { "www.gov.pl" })]
    [InlineData("bip.urzad.gov.pl", new[] { "bip.urzad.gov.pl", "urzad.gov.pl" })]
    [InlineData("intranet.edu.pl", new[] { "intranet.edu.pl" })]
    [InlineData("login.bbc.co.uk", new[] { "login.bbc.co.uk", "bbc.co.uk" })]
    [InlineData("Haslo.Contoso.COM.", new[] { "haslo.contoso.com", "contoso.com" })]
    [InlineData("intranet.contoso.de", new[] { "intranet.contoso.de", "contoso.de" })]
    [InlineData("localhost", new string[0])]
    [InlineData("", new string[0])]
    public void Suggestions_NeverOfferAPublicSuffix(string host, string[] expected) =>
        Assert.Equal(expected, LinkDomainSuggestions.For(host));

    [Fact]
    public void InternationalNames_ArePunycode()
    {
        var idn = new System.Globalization.IdnMapping();
        Assert.Equal([idn.GetAscii("haslo.zażółć.pl"), idn.GetAscii("zażółć.pl")], LinkDomainSuggestions.For("haslo.zażółć.pl"));
    }
}
