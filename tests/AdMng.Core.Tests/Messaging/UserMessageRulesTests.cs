using AdMng.Core.Messaging;

namespace AdMng.Core.Tests.Messaging;

/// <summary>Security rules of messages: no password field, header injection, addresses, links, HTML.</summary>
public class UserMessageRulesTests
{
    [Theory]
    [InlineData("unicodePwd")]
    [InlineData("userPassword")]
    [InlineData("ms-Mcs-AdmPwd")]
    [InlineData("msLAPS-Password")]
    [InlineData("msFVE-RecoveryPassword")]
    [InlineData("msDS-UserPasswordExpiryTimeComputed")]
    [InlineData("pwdLastSet")]
    [InlineData("supplementalCredentials")]
    public void PasswordAndSecretAttributes_AreForbiddenFields(string attribute) =>
        Assert.True(UserMessageRules.IsForbiddenAttribute(attribute));

    [Theory]
    [InlineData("givenName")]
    [InlineData("sAMAccountName")]
    [InlineData("mail")]
    [InlineData("department")]
    public void OrdinaryAttributes_AreAllowed(string attribute) => Assert.False(UserMessageRules.IsForbiddenAttribute(attribute));

    [Theory]
    [InlineData("hasło", true)]
    [InlineData("nowe_haslo", true)]
    [InlineData("Password", true)]
    [InlineData("tymczasowe-pwd", true)]
    [InlineData("sekret", true)]
    [InlineData("termin", false)]
    [InlineData("powód", false)]
    public void InputsNamedLikeAPassword_AreForbidden(string name, bool forbidden) =>
        Assert.Equal(forbidden, UserMessageRules.IsForbiddenInputName(name));

    [Fact]
    public void HeaderSafe_RemovesCrLfAndControlCharacters()
    {
        Assert.Equal("Hasło wygasa  Bcc: ofiara@example.com", UserMessageRules.HeaderSafe("Hasło wygasa\r\nBcc: ofiara@example.com"));
        Assert.Equal("a b c", UserMessageRules.HeaderSafe(" a\tb\u2028c\0 "));
        Assert.Equal(string.Empty, UserMessageRules.HeaderSafe(null));
    }

    [Theory]
    [InlineData("jan.kowalski@contoso.test", true)]
    [InlineData("  zolc@sprzedaż.contoso.pl ", true)]
    [InlineData("a@b", true)]
    [InlineData("Jan <jan@contoso.test>", false)]
    [InlineData("jan@contoso.test, eve@example.com", false)]
    [InlineData("jan@contoso.test\r\nBcc: eve@example.com", false)]
    [InlineData("jan@@contoso.test", false)]
    [InlineData("@contoso.test", false)]
    [InlineData("jan@", false)]
    [InlineData("jan@contoso..test", false)]
    [InlineData("", false)]
    public void Addresses_AreParsedStrictly(string text, bool valid) => Assert.Equal(valid, UserMessageRules.TryParseAddress(text, out _));

    [Fact]
    public void PrimarySmtpAddress_IsTheUpperCaseOne()
    {
        Assert.Equal("agnieszka.zolc@contoso.test", UserMessageRules.PrimarySmtpAddress(["smtp:a.zolc@contoso.test", "SMTP:agnieszka.zolc@contoso.test"]));
        Assert.Null(UserMessageRules.PrimarySmtpAddress(["smtp:a.zolc@contoso.test", "X500:/o=Contoso"]));
    }

    [Fact]
    public void Links_OnlyToAllowedDomainsAndTheirSubdomains()
    {
        string[] allowed = ["contoso.test", "https://Intranet.Firma.pl/", "*.sprzedaż.pl"];

        Assert.True(UserMessageRules.IsLinkAllowed("https://contoso.test/haslo", allowed));
        Assert.True(UserMessageRules.IsLinkAllowed("https://sspr.contoso.test/reset?x=1", allowed));
        Assert.True(UserMessageRules.IsLinkAllowed("http://intranet.firma.pl", allowed));
        Assert.True(UserMessageRules.IsLinkAllowed("www.contoso.test/pomoc", allowed));
        Assert.True(UserMessageRules.IsLinkAllowed("https://sklep.sprzedaż.pl/", allowed));
        Assert.True(UserMessageRules.IsLinkAllowed("mailto:helpdesk@evil.example", allowed));
        Assert.False(UserMessageRules.IsLinkAllowed("https://contoso.test.evil.example/", allowed));
        Assert.False(UserMessageRules.IsLinkAllowed("https://evilcontoso.test/", allowed));
        Assert.False(UserMessageRules.IsLinkAllowed("javascript:alert(1)", allowed));
        Assert.False(UserMessageRules.IsLinkAllowed("https://contoso.test@evil.example/", allowed));
        Assert.False(UserMessageRules.IsLinkAllowed("https://contoso.test/", []));
    }

    [Fact]
    public void FindLinks_InTextAndHtmlAttributes()
    {
        var links = UserMessageRules.FindLinks("Zobacz https://sspr.contoso.test/reset. Albo www.evil.example, <a href=\"https://x.example/a?b=1&amp;c=2\">tu</a> <img src='cid:logo'>");

        Assert.Equal(["https://sspr.contoso.test/reset", "www.evil.example", "https://x.example/a?b=1&c=2", "cid:logo"], links);
    }

    [Fact]
    public void Html_RejectsActiveContent()
    {
        Assert.Empty(UserMessageRules.FindForbiddenHtml("<p style=\"color:red\">Dzień <b>dobry</b></p><a href=\"https://contoso.test\">x</a>"));
        Assert.Equal(["<script>"], UserMessageRules.FindForbiddenHtml("<p>a</p><script>alert(1)</script>"));
        Assert.Equal(["onerror="], UserMessageRules.FindForbiddenHtml("<img src=\"x\" onerror=\"alert(1)\">"));
        Assert.Equal(["javascript:"], UserMessageRules.FindForbiddenHtml("<a href=\"java&#x09;script:alert(1)\">x</a>"));
        Assert.Contains("<iframe>", UserMessageRules.FindForbiddenHtml("<IFRAME src=https://contoso.test>"));
        Assert.Contains("<form>", UserMessageRules.FindForbiddenHtml("<form action=\"https://contoso.test\"><input name=p></form>"));
    }

    [Theory]
    [InlineData("<a href=javascript:alert(1)>x</a>", "javascript:")]
    [InlineData("<a href = ' JavaScript:alert(1)'>x</a>", "javascript:")]
    [InlineData("<a href=\"&#x20;&#106;avascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=`vbscript:x`>x</a>", "vbscript:")]
    [InlineData("<img src=\"data:image/png;base64,AAAA\">", "data:")]
    [InlineData("<p style=\"background:url( 'data:image/png;base64,AAAA')\">x</p>", "data:")]
    [InlineData("<a href=\"file:///c:/hasla.txt\">x</a>", "file:")]
    public void Html_RejectsForbiddenSchemes_WhereAUrlStarts(string html, string scheme) =>
        Assert.Equal([scheme], UserMessageRules.FindForbiddenHtml(html));

    [Fact]
    public void Html_WordsOfTheText_AreNotUrlSchemes()
    {
        // "Data:" is the Polish "Date:" – plain text, not a data: URL (the same for "Profile:" and file:).
        Assert.Empty(UserMessageRules.FindForbiddenHtml("<p>Data: 30 listopada 2026</p><p>Nowa data: jutro</p><p>Profile: Jan</p>"));
        Assert.Empty(UserMessageRules.FindForbiddenHtml(UserMessageRules.TextToHtml("Data: 30 listopada 2026\nProfile: Jan")));
    }

    [Fact]
    public void TextToHtml_EncodesEverything_AndKeepsParagraphs()
    {
        var html = UserMessageRules.TextToHtml("Dzień dobry <Łukasz>,\n\nlinia 1\nlinia & 2\r\n\r\nKoniec");

        Assert.Equal("<p>Dzień dobry &lt;Łukasz&gt;,</p>\n<p>linia 1<br>\nlinia &amp; 2</p>\n<p>Koniec</p>", html);
    }

    [Fact]
    public void Documents_AlwaysCarryTheFooter()
    {
        var footer = BuiltInMessageTemplates.Footer(MessageLanguage.Polish);

        Assert.Contains("Nigdy nie prosimy o podanie hasła", footer, StringComparison.Ordinal);
        Assert.EndsWith("\n-- \n" + footer + "\n", UserMessageRules.TextDocument("Treść\n\n", footer), StringComparison.Ordinal);
        var html = UserMessageRules.HtmlDocument("<p>Treść</p>", footer, MessageLanguage.Polish);
        Assert.Contains("<html lang=\"pl\">", html, StringComparison.Ordinal);
        Assert.Contains("Nigdy nie prosimy o podanie hasła", html, StringComparison.Ordinal);
        var own = UserMessageRules.HtmlDocument("<html><body><p>x</p></body></html>", "We never ask", MessageLanguage.English);
        Assert.StartsWith("<html><body><p>x</p><hr ", own, StringComparison.Ordinal);
        Assert.EndsWith("We never ask</p>\n</body></html>", own, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MessageLanguage.Polish)]
    [InlineData(MessageLanguage.English)]
    public void GeneratedDocuments_PassTheHtmlCheck(MessageLanguage language)
    {
        // AdMng Watchdog checks the whole HTML body of a message with the same rules (U1b): the document AD Manager wraps
        // around every message must not trip them, or every message sent through the service is rejected.
        var html = UserMessageRules.HtmlDocument(UserMessageRules.TextToHtml("Dzień dobry Łukasz,\n\nhasło wygasa jutro."), BuiltInMessageTemplates.Footer(language), language);

        Assert.Empty(UserMessageRules.FindForbiddenHtml(html));
        Assert.Contains("\n<body style=\"font-family:", html, StringComparison.Ordinal);
    }
}
