using AdMng.Core.Messaging.Html;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>Safe Links unwrapping.</summary>
public class SafeLinksTests
{
    [Theory]
    [InlineData("https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fhaslo.contoso.pl%2F&data=05%7C02&reserved=0", "https://haslo.contoso.pl/")]
    [InlineData("https://nam12.safelinks.protection.outlook.com/?url=https%3A%2F%2Fportal.contoso.pl%2Fzmie%C5%84%3Fa%3D1%26b%3D2&data=x", "https://portal.contoso.pl/zmień?a=1&b=2")]
    [InlineData("https://safelinks.protection.outlook.com/?url=mailto%3Apomoc%40contoso.pl", "mailto:pomoc@contoso.pl")]
    public void SafeLinks_AreUnwrapped(string href, string original)
    {
        Assert.True(SafeLinks.TryUnwrap(href, out var unwrapped));
        Assert.Equal(original, unwrapped);
    }

    [Theory]
    [InlineData("https://eur01.safelinks.protection.outlook.com/?url=javascript%3Aalert(1)")]
    [InlineData("https://eur01.safelinks.protection.outlook.com/?data=x")]
    [InlineData("https://safelinks.protection.outlook.com.evil.example/?url=https%3A%2F%2Fa.pl")]
    [InlineData("https://urldefense.com/v3/__https://a.pl__;!!x")]
    [InlineData("https://haslo.contoso.pl/")]
    [InlineData("")]
    public void OtherLinks_AreNotUnwrapped(string href) => Assert.False(SafeLinks.TryUnwrap(href, out _));
}
