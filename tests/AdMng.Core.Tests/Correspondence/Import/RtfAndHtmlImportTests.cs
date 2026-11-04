using System.Text;
using AdMng.Core.Correspondence.Import;
using static AdMng.Core.Tests.Correspondence.Import.ImportTestFiles;

namespace AdMng.Core.Tests.Correspondence.Import;

public class RtfAndHtmlImportTests
{
    private static ContactImportResult Rtf(string rtf, int ansiCodePage = 1250) => ContactFileReader.Read("a.rtf", Encoding.ASCII.GetBytes(rtf), ansiCodePage);

    private static string RtfText(string rtf, int ansiCodePage = 1250) => RtfReader.Read(Encoding.ASCII.GetBytes(rtf), ansiCodePage);

    // ---- RTF -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Rtf_HexEscapesInTheAnsiCodePage()
    {
        var result = Rtf(@"{\rtf1\ansi\ansicpg1250\deff0{\fonttbl{\f0\fnil Calibri;}}{\*\generator Riched20 10.0.19041}\viewkind4\uc1"
            + @"\pard\f0\fs22 Nazwa\tab E-mail\par Urz\'b9d Gminy Bia\'b3ka\tab ug@bialka.pl\par}");
        Assert.Equal(ContactSourceFormat.Rtf, result.Format);
        var contact = Assert.Single(result.Contacts);
        Assert.Equal(("ug@bialka.pl", "Urząd Gminy Białka"), (contact.Email, contact.Name));
        Assert.Equal(["Nazwa", "E-mail"], result.Columns);
    }

    [Fact]
    public void Rtf_HexEscapesWithoutAnsicpg_UseTheGivenCodePage() =>
        Assert.Equal("Łódź", RtfText(@"{\rtf1\ansi \'a3\'f3d\'9f}").Trim());

    [Fact]
    public void Rtf_FontCharsetWinsOverTheDocumentCodePage() =>
        Assert.Equal("Łódź", RtfText(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss\fcharset238 Arial;}{\f1\fnil\fcharset0 Calibri;}}\f0 \'a3\'f3d\'9f}", 1252).Trim());

    [Fact]
    public void Rtf_DefaultFontCharset() =>
        Assert.Equal("ąę", RtfText(@"{\rtf1\ansi\ansicpg1252\deff1{\fonttbl{\f0 Arial;}{\f1\fcharset238 Calibri;}}\'b9\'ea}", 1252).Trim());

    [Fact]
    public void Rtf_UnicodeEscapes_SkipTheirFallback()
    {
        Assert.Equal("Gmina Łódź", RtfText(@"{\rtf1\uc1 Gmina \u321?\u243?d\u378?}").Trim());
        Assert.Equal("Łódź", RtfText(@"{\rtf1\uc2 \u321\'a3\'a3\u243 oo d\u378\'9f\'9f}").Trim().Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Equal("Łó", RtfText(@"{\rtf1\uc0 \u321 \u243 }").Trim());
        Assert.Equal("\uFFFD", RtfText(@"{\rtf1\u-3?}"));   // negative values are UTF-16 code units
    }

    [Fact]
    public void Rtf_TableCellsAndRows()
    {
        var text = RtfText(@"{\rtf1\ansi\ansicpg1250{\fonttbl{\f0 Arial;}}\trowd\cellx2000\cellx4000\pard\intbl Nazwa\cell E-mail\cell\row"
            + @"\trowd\cellx2000\cellx4000\pard\intbl Gmina \'a3apy\cell gmina@lapy.pl\cell\row}");
        Assert.Equal("Nazwa\tE-mail\t\nGmina Łapy\tgmina@lapy.pl\t\n", text);
        var contact = Assert.Single(ContactExtractor.FromText(text).Contacts);
        Assert.Equal("Gmina Łapy", contact.Name);
    }

    [Fact]
    public void Rtf_DestinationsWithoutTextAreSkipped()
    {
        var text = RtfText(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}}{\colortbl;\red0\green0\blue0;}{\stylesheet{\s0 Normal;}}"
            + @"{\info{\title tytul@x.pl}{\author Jan Kowalski}}{\*\company firma@x.pl}{\pict\pngblip 89504e47}"
            + @"{\header naglowek@x.pl}{\pict\bin7 }}}{{{x}tekst \{ok\} \\ koniec\par}");
        Assert.Equal("tekst {ok} \\ koniec\n", text);
    }

    [Fact]
    public void Rtf_HyperlinkFieldAddsItsAddress()
    {
        var result = Rtf(@"{\rtf1\ansi\ansicpg1250 Pisz: {\field{\*\fldinst{HYPERLINK ""mailto:sekretariat@sad.gov.pl""}}{\fldrslt{\ul Sekretariat S\'b9du}}}\par"
            + @"{\field{\*\fldinst HYPERLINK ""mailto:biuro@sad.gov.pl""}{\fldrslt biuro@sad.gov.pl}}\par}");
        Assert.Equal(["sekretariat@sad.gov.pl", "biuro@sad.gov.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal("Sekretariat Sądu", result.Contacts[0].Name);
        Assert.Equal(0, result.Duplicates);
    }

    [Fact]
    public void Rtf_SpecialCharactersAndLineBreaks() =>
        Assert.Equal("a–b—c „d” e\nf\tg", RtfText(@"{\rtf1 a\endash b\emdash c \ldblquote d\rdblquote  e\line f\tab g}").Replace('“', '„'));

    [Fact]
    public void Rtf_UnbalancedOrDeepGroupsDoNotFail()
    {
        Assert.Equal("ok", RtfText("{\\rtf1 ok}}}}").Trim());
        Assert.Equal("ok", RtfText(new string('{', 20_000) + "ok" + new string('}', 20_000)).Trim());
        Assert.Equal(string.Empty, RtfText(@"{\rtf1\"));
    }

    // ---- HTML ----------------------------------------------------------------------------------------------------------

    private static ContactImportResult Html(string html, Encoding? encoding = null) =>
        ContactFileReader.Read("strona.html", (encoding ?? Encoding.UTF8).GetBytes(html), 1250);

    [Fact]
    public void Html_TableRowsAndMailtoInCells()
    {
        var result = Html("<html><body><table><tr><th>Nazwa</th><th>E-mail</th><th>Strona</th></tr>"
            + "<tr><td>Gmina Żyrardów</td><td><a href=\"mailto:gmina@zyrardow.pl\">napisz</a></td><td>www</td></tr>"
            + "<tr><td colspan=\"2\">Powiat (scalone)</td><td>-</td></tr>"
            + "<tr><td>Starostwo</td><td>starostwo@powiat.pl</td><td></td></tr></table></body></html>");
        Assert.Equal(ContactSourceFormat.Html, result.Format);
        Assert.Equal(["gmina@zyrardow.pl", "starostwo@powiat.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal(["Gmina Żyrardów", "Starostwo"], result.Contacts.Select(c => c.Name));
        Assert.Equal(1, result.InvalidAddresses);   // the merged row has no address in the e-mail column
        Assert.Equal(["Nazwa", "E-mail", "Strona"], result.Columns);
    }

    [Fact]
    public void Html_NestedTableIsATableOfItsOwn()
    {
        var result = Html("<table><tr><td>Kontakt: Dyrekcja &lt;dyrekcja@szkola.pl&gt;"
            + "<table><tr><td>Klasa</td><td>E-mail wychowawcy</td></tr><tr><td>1a</td><td>1a@szkola.pl</td></tr></table>"
            + "</td></tr></table>");
        Assert.Equal(["1a@szkola.pl", "dyrekcja@szkola.pl"], result.Contacts.Select(c => c.Email).Order());
        Assert.Equal("Dyrekcja", result.Contacts.Single(c => c.Email == "dyrekcja@szkola.pl").Name);
        Assert.Equal("1a", result.Contacts.Single(c => c.Email == "1a@szkola.pl").Fields["Klasa"]);
    }

    [Fact]
    public void Html_MailtoLinksInText_ScriptsAndHeadIgnored()
    {
        var result = Html("<html><head><title>t@naglowek.pl</title><script>var a = 'skrypt@x.pl';</script><style>p{}</style></head>"
            + "<body><p>Zapraszamy: <a href=\"mailto:Jan.Kowalski@Urzad.pl\">Jan Kowalski</a></p>"
            + "<p>Inny adres: <a href=\"mailto:biuro@urzad.pl\">biuro@urzad.pl</a></p><noscript>ns@x.pl</noscript>"
            + "<template><p>szablon@x.pl</p></template></body></html>");
        Assert.Equal(["jan.kowalski@urzad.pl", "biuro@urzad.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal("Jan Kowalski", result.Contacts[0].Name);
        Assert.Equal(0, result.Duplicates);
    }

    [Fact]
    public void Html_SourceLineBreaksAreSpaces() =>
        Assert.Equal("Urząd Skarbowy", Assert.Single(Html("<p>Urząd\nSkarbowy &lt;us@mf.gov.pl&gt;</p>").Contacts).Name);

    [Fact]
    public void Html_Windows1250WithMetaCharset()
    {
        var result = Html("<html><head><meta http-equiv=Content-Type content=\"text/html; charset=windows-1250\"></head><body>"
            + "<p>Gmina Łęczna &lt;gmina@leczna.pl&gt;</p></body></html>", Cp1250);
        Assert.Equal("Gmina Łęczna", Assert.Single(result.Contacts).Name);
    }

    [Fact]
    public void Html_NestedTooDeeply_IsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => Html(string.Concat(Enumerable.Repeat("<div>", 5000)) + "a@x.pl"));
}
