using System.Text;
using AdMng.Core.Messaging.Html;
using AdMng.Testing.RichHtml;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>CF_HTML: header, UTF-8 byte offsets, fragment markers, sentinels, encoding problems.</summary>
public class CfHtmlParserTests
{
    private const string Outlook = "<html><head><style>p.MsoNormal{margin:0cm}</style></head>\r\n<body lang=PL>\r\n<!--StartFragment--><p class=MsoNormal>Zażółć gęślą jaźń – {givenName}</p><!--EndFragment-->\r\n</body></html>";

    [Fact]
    public void OfficeEntry_KeepsTheContextAndTheMarkers()
    {
        var document = CfHtmlParser.Parse(CfHtmlFixture.Build(Outlook, "https://intranet.contoso.pl/wiadomosci/haslo.html"));
        Assert.Equal(Outlook, document.Html);
        Assert.Empty(document.Problems);
        Assert.Equal(new Uri("https://intranet.contoso.pl/wiadomosci/haslo.html"), document.SourceUrl);
    }

    [Fact]
    public void ByteOffsets_CountUtf8BytesNotCharacters()
    {
        // Polish letters are two bytes: offsets counted in characters would cut the fragment in the middle. The header
        // below is exactly 105 bytes long.
        const string html = "<html><body><p>ąęłńóśźż</p><b>Łódź</b></body></html>";
        var bytes = Encoding.UTF8.GetByteCount(html);
        var start = Encoding.UTF8.GetByteCount("<html><body>");
        var end = Encoding.UTF8.GetByteCount("<html><body><p>ąęłńóśźż</p><b>Łódź</b>");
        var header = "Version:1.0\r\nStartHTML:" + CfHtmlFixture.Offset(105) + "\r\nEndHTML:" + CfHtmlFixture.Offset(105 + bytes)
            + "\r\nStartFragment:" + CfHtmlFixture.Offset(105 + start) + "\r\nEndFragment:" + CfHtmlFixture.Offset(105 + end) + "\r\n";
        Assert.Equal(105, header.Length);
        var document = CfHtmlParser.Parse(CfHtmlFixture.Raw(header, html));
        Assert.Equal("<html><body><!--admng-fragment-start--><p>ąęłńóśźż</p><b>Łódź</b><!--admng-fragment-end--></body></html>", document.Html);
        Assert.Empty(document.Problems);
    }

    [Fact]
    public void WrongOffsets_FallBackToTheMarkers()
    {
        var document = CfHtmlParser.Parse(CfHtmlFixture.Raw("Version:0.9\r\nStartHTML:0000000999\r\nEndHTML:0000000100\r\nStartFragment:0000000050\r\nEndFragment:0000000060\r\n", Outlook));
        Assert.Equal(Outlook, document.Html);
        Assert.Contains(document.Problems, p => p.Kind == HtmlImportProblemKind.ClipboardOffsets);
    }

    [Fact]
    public void MissingVersion_IsInvalid()
    {
        var document = CfHtmlParser.Parse(CfHtmlFixture.Raw("StartHTML:0000000080\r\nEndHTML:0000000200\r\nStartFragment:0000000090\r\nEndFragment:0000000100\r\n", Outlook));
        Assert.Contains(document.Problems, p => p.Kind == HtmlImportProblemKind.ClipboardOffsets);
        Assert.Contains("<!--StartFragment-->", document.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void MinusOne_MeansTheFragmentOnly()
    {
        const string fragment = "<b>pogrubione</b> i <i>pochylone</i>";
        var headerLength = "Version:1.0\r\nStartHTML:-1\r\nEndHTML:-1\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n".Length;
        var bytes = Encoding.UTF8.GetByteCount(fragment);
        var header = $"Version:1.0\r\nStartHTML:-1\r\nEndHTML:-1\r\nStartFragment:{CfHtmlFixture.Offset(headerLength)}\r\nEndFragment:{CfHtmlFixture.Offset(headerLength + bytes)}\r\n";
        var document = CfHtmlParser.Parse(CfHtmlFixture.Raw(header, fragment));
        Assert.Equal("<!--admng-fragment-start-->" + fragment + "<!--admng-fragment-end-->", document.Html);
        Assert.Empty(document.Problems);
    }

    [Fact]
    public void WithoutMarkers_TheWholeBodyIsTheFragment()
    {
        var document = CfHtmlParser.FromText("<html><body class=x><p>tekst</p></body></html>");
        Assert.Equal("<html><body class=x><!--admng-fragment-start--><p>tekst</p><!--admng-fragment-end--></body></html>", document.Html);
    }

    [Fact]
    public void InvalidUtf8_IsReplacedAndReported()
    {
        // A source that wrote the ANSI code page: two invalid bytes in place of "XX" (same length, offsets stay right).
        var entry = CfHtmlFixture.Build("<html><body><!--StartFragment--><p>zaXXółć</p><!--EndFragment--></body></html>");
        var at = entry.AsSpan().IndexOf("XX"u8);
        entry[at] = 0xBF;
        entry[at + 1] = 0xBF;
        var document = CfHtmlParser.Parse(entry);
        Assert.Contains(document.Problems, p => p.Kind == HtmlImportProblemKind.ClipboardEncoding && p.Severity == AdMng.Core.Messaging.MessageIssueSeverity.Warning);
        Assert.Contains("\uFFFD", document.Html, StringComparison.Ordinal);
        Assert.Contains("ółć", document.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Bom_AndTrailingNul_AreDropped()
    {
        var html = "\uFEFF<html><body><!--StartFragment-->x<!--EndFragment--></body></html>";
        var document = CfHtmlParser.Parse(CfHtmlFixture.Build(html, trailingNul: true));
        Assert.StartsWith("<html>", document.Html, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', document.Html);
    }

    [Fact]
    public void TextMode_SkipsTheHeaderAndReadsTheSourceUrl()
    {
        var text = "Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:0000000999\r\nStartFragment:0000000140\r\nEndFragment:0000000200\r\nSourceURL:https://portal.contoso.pl/a/b.html\r\n" + Outlook;
        var document = CfHtmlParser.FromText(text);
        Assert.Equal(Outlook, document.Html);
        Assert.Equal("portal.contoso.pl", document.SourceUrl!.Host);
    }

    [Fact]
    public void SourceUrl_OnlyHttp()
    {
        var document = CfHtmlParser.Parse(CfHtmlFixture.Build(Outlook, @"file:///C:/Users/jkowalski/Desktop/a.html"));
        Assert.Null(document.SourceUrl);
    }

    [Fact]
    public void ToString_ShowsNoContent() =>
        Assert.Equal($"CfHtmlDocument({Outlook.Length} characters)", CfHtmlParser.Parse(CfHtmlFixture.Build(Outlook)).ToString());
}
