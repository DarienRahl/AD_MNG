using AdMng.Core.Messaging.Html;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>Links of a body and the text/target mismatch.</summary>
public class HtmlLinkInspectorTests
{
    [Theory]
    [InlineData("https://evil.example/login", "haslo.contoso.pl", true)]
    [InlineData("https://evil.example/login", "https://haslo.contoso.pl/zmiana", true)]
    [InlineData("https://haslo.contoso.pl/", "haslo.contoso.pl", false)]
    [InlineData("https://www.contoso.pl/", "contoso.pl", false)]
    [InlineData("https://haslo.contoso.pl/", "Zmień hasło", false)]
    [InlineData("https://haslo.contoso.pl/", "www.haslo.contoso.pl.", false)]
    [InlineData("mailto:pomoc@contoso.pl", "pomoc@evil.example", false)]
    public void TextNamingAnotherHost_IsAMismatch(string href, string text, bool mismatch) =>
        Assert.Equal(mismatch, HtmlLinkInspector.IsTextMismatch(new HtmlLink(href, text)));

    [Fact]
    public void Links_AreParsedLikeABrowser()
    {
        var links = HtmlLinkInspector.Links("<p><a href=\" https://a.contoso.pl/x \">a</a><a>bez</a><a href=\"mailto:b@c.pl\"><b>b</b></a></p>");
        Assert.Equal([new HtmlLink("https://a.contoso.pl/x", "a"), new HtmlLink("mailto:b@c.pl", "b")], links);
    }
}
