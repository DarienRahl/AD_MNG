using AdMng.Watchdog.Core.Formatting;

namespace AdMng.Watchdog.Core.Tests.Formatting;

public sealed class UntrustedTextTests
{
    [Theory]
    [InlineData("Jan\u202EKowalski", "Jan<U+202E>Kowalski")]
    [InlineData("\u2066ukryte\u2069", "<U+2066>ukryte<U+2069>")]
    [InlineData("a\u200Bb\u200Cc\u200Dd\uFEFFe", "a<U+200B>b<U+200C>c<U+200D>d<U+FEFF>e")]
    [InlineData("lewo\u200Eprawo\u200F\u061C", "lewo<U+200E>prawo<U+200F><U+061C>")]
    [InlineData("zero\u0000bell\u0007", "zero<U+0000>bell<U+0007>")]
    [InlineData("linia\u2028akapit\u2029", "linia<U+2028>akapit<U+2029>")]
    [InlineData("Zażółć gęślą jaźń", "Zażółć gęślą jaźń")]
    public void ControlAndFormatCharacters_BecomeVisibleEscapes(string input, string expected) =>
        Assert.Equal(expected, UntrustedText.Sanitize(input));

    [Fact]
    public void SingleLineValues_EscapeLineBreaksAndTabs_MultiLineValuesKeepThem()
    {
        Assert.Equal("a<U+000A>b<U+0009>c", UntrustedText.Sanitize("a\nb\tc"));
        Assert.Equal("a\nb\tc\nd\ne", UntrustedText.Sanitize("a\r\nb\tc\rd\ne", multiLine: true));
    }

    [Fact]
    public void UnpairedSurrogates_AreEscaped_PairedOnesKept()
    {
        Assert.Equal("x<U+D800>y", UntrustedText.Sanitize("x\uD800y"));
        Assert.Equal("ok 😀", UntrustedText.Sanitize("ok 😀"));
        Assert.Equal("tag<U+E0041>", UntrustedText.Sanitize("tag\U000E0041"));
    }

    [Theory]
    [InlineData("https://evil.example/x", "hxxps://evil[.]example/x")]
    [InlineData("HTTP://evil.example:8080/a?b=c", "hxxp://evil[.]example:8080/a?b=c")]
    [InlineData("ftp://files.evil.example/", "fxp://files[.]evil[.]example/")]
    [InlineData("zobacz [Kliknij](https://evil.example/x) teraz", "zobacz [Kliknij](hxxps://evil[.]example/x) teraz")]
    [InlineData("ldap://dc01.contoso.com", "ldap[:]//dc01[.]contoso[.]com")]
    [InlineData("www.evil.example", "www[.]evil.example")]
    [InlineData("odwiedź WWW.evil.example", "odwiedź WWW[.]evil.example")]
    public void UrlLikeTokens_AreDefanged(string input, string expected) => Assert.Equal(expected, UntrustedText.Sanitize(input));

    [Theory]
    [InlineData("jan.kowalski@contoso.com")]
    [InlineData("dc01.contoso.com")]
    [InlineData(@"\\fs01.contoso.com\home$\jan")]
    [InlineData("CN=Łukasz Wiśniewski,OU=IT,DC=contoso,DC=com")]
    [InlineData(@"CONTOSO\adm.nowak")]
    [InlineData("swww.example")]
    public void HostNamesUpnsAndPaths_StayReadable(string value) => Assert.Equal(value, UntrustedText.Sanitize(value));

    [Fact]
    public void HiddenCharactersInsideAUrl_AreEscapedBeforeDefanging()
    {
        // The escape breaks the scheme; what is left of it is defanged anyway.
        Assert.Equal("ht<U+200B>tps[:]//evil[.]example", UntrustedText.Sanitize("ht\u200Btps://evil.example"));
    }

    [Theory]
    [InlineData("https://www.evil.example/x?y=1")]
    [InlineData("ldap://dc01.contoso.com ‮ www.a.example")]
    [InlineData("ftp://a.b.c http://x.y")]
    public void Sanitize_IsIdempotent(string input)
    {
        var once = UntrustedText.Sanitize(input);

        Assert.Equal(once, UntrustedText.Sanitize(once));
        Assert.Equal(UntrustedText.ForHeader(once), UntrustedText.ForHeader(UntrustedText.ForHeader(once)));
    }

    [Fact]
    public void ForHeader_StripsLineBreaksAndControls_IntoSingleSpaces()
    {
        Assert.Equal("Temat Bcc: zly@example.com", UntrustedText.ForHeader("Temat\r\nBcc: zly@example.com"));
        Assert.Equal("a b", UntrustedText.ForHeader("  a \t\t b  "));
        Assert.Equal(string.Empty, UntrustedText.ForHeader(null));
        Assert.DoesNotContain('\n', UntrustedText.ForHeader("x\u2028y\ny"));
    }
}
