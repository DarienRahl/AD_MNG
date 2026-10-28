using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AdMng.App.Correspondence;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;
using AdMng.Testing.RichHtml;

namespace AdMng.App.UiTests.Correspondence;

/// <summary>
/// The HTML round trip of the Korespondencja editor (U5, design note 9.2): for every construct the editor can hold,
/// <c>ToHtml(ToDocument(ToHtml(document)))</c> equals <c>ToHtml(document)</c> and the HTML is what a mail client needs
/// (inline styles of the sanitized subset); HTML from elsewhere (headings, legacy font, Outlook's paste) converges after
/// one pass; scripts, dangerous links and unknown images never get through; white space collapses as in a browser.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class FlowDocumentHtmlTests
{
    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(14, 6, seed: 0x75), "logo.png");
    private static readonly MessageImage Photo = MessageImage.Create(MessageImageSamples.Jpeg(20, 10), "zdjecie.jpg");

    public static TheoryData<string> Constructs => new()
    {
        "paragraphs", "alignment", "bold", "italic", "underline", "strike", "underline-strike", "colour", "highlight", "size",
        "font", "generic-font", "mixed", "nested-spans", "link", "mail-link", "field-link", "line-break", "trailing-break",
        "spaces", "leading-space", "tab", "bullets", "numbering", "list-types", "list-none", "nested-list", "list-paragraphs",
        "table", "image", "block-image", "rule", "section", "fields",
    };

    private static IReadOnlyDictionary<string, MessageImage> Images => new Dictionary<string, MessageImage>
    {
        [Logo.ContentId] = Logo,
        [Photo.ContentId] = Photo,
    };

    [Theory]
    [MemberData(nameof(Constructs))]
    public Task RoundTrip_IsStable_ForEveryConstruct(string construct) => WpfTestHost.RunAsync(() =>
    {
        var (document, expected) = Build(construct);

        var html = AssertStable(document);

        foreach (var part in expected)
        {
            Assert.Contains(part, html, StringComparison.Ordinal);
        }

        Assert.Empty(MessageHtmlSanitizer.Validate(html, Options()));
        return Task.CompletedTask;
    });

    [Fact]
    public Task AnEmptyDocument_IsAnEmptyBody() => WpfTestHost.RunAsync(() =>
    {
        Assert.Equal(string.Empty, ToHtml(Document(new Paragraph())));
        Assert.Equal(string.Empty, ToHtml(Document(P(new Run("   \u00A0 ")), new Paragraph(), P(new LineBreak()))));
        Assert.Equal(string.Empty, ToHtml(FlowDocumentHtml.NewDocument()));

        foreach (var html in new[] { string.Empty, "   ", "<p></p>", "<script>alert(1)</script>" })
        {
            var document = FlowDocumentHtml.ToDocument(html, Images);
            Assert.IsType<Paragraph>(Assert.Single(document.Blocks));
            Assert.Equal(string.Empty, ToHtml(document));
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task NewDocument_HasTheMessageDefaults() => WpfTestHost.RunAsync(() =>
    {
        var document = FlowDocumentHtml.ToDocument("<p>Treść</p>", Images);

        Assert.Equal(FlowDocumentHtml.FontFamilyName, document.FontFamily.Source);
        Assert.Equal(FlowDocumentHtml.DefaultFontSize, document.FontSize, 3);
        Assert.Equal(Colors.Black, ((SolidColorBrush)document.Foreground).Color);
        Assert.Equal(TextAlignment.Left, document.TextAlignment);

        // Body text in the defaults carries no style of its own.
        Assert.Equal("<p style=\"margin:0 0 8pt 0\">Treść</p>\n", ToHtml(document));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("<h1>Tytuł</h1>", "<span style=\"font-weight:bold;font-size:22pt\">Tytuł</span>")]
    [InlineData("<h3>Śródtytuł</h3>", "<span style=\"font-weight:bold;font-size:13pt\">Śródtytuł</span>")]
    [InlineData("<p><font color=\"red\" face=\"Arial\" size=\"5\">Duży</font></p>", "<span style=\"font-size:18pt;font-family:'Arial';color:#FF0000\">Duży</span>")]
    [InlineData("<p><b>B</b><strong>S</strong><i>I</i><em>E</em><u>U</u><s>X</s><strike>Y</strike></p>", "<span style=\"font-weight:bold\">BS</span><span style=\"font-style:italic\">IE</span><span style=\"text-decoration:underline\">U</span><span style=\"text-decoration:line-through\">XY</span>")]
    [InlineData("<table><tr><th>Dział</th><td>Telefon</td></tr></table>", "<p style=\"margin:0 0 8pt 0;text-align:center\"><span style=\"font-weight:bold\">Dział</span></p>")]
    [InlineData("<pre>a  b\nc</pre>", "<span style=\"font-family:'Consolas'\">a &nbsp;b</span><br>\n<span style=\"font-family:'Consolas'\">c</span>")]
    [InlineData("<blockquote>Cytat</blockquote><center>Środek</center>", "<p style=\"margin:0 0 8pt 0;text-align:center\">Środek</p>")]
    [InlineData("<p style=\"text-align:right\">Prawo</p><div align=\"center\">Div</div>", "<p style=\"margin:0 0 8pt 0;text-align:right\">Prawo</p>")]
    [InlineData("<p><span style=\"font:italic bold 12pt 'Segoe UI',sans-serif\">Skrót</span></p>", "<span style=\"font-style:italic;font-weight:bold;font-size:12pt;font-family:'Segoe UI',sans-serif\">Skrót</span>")]
    [InlineData("<p><span style=\"color:rgb(192,0,0);background:yellow\">RGB</span></p>", "<span style=\"color:#C00000;background-color:#FFFF00\">RGB</span>")]
    [InlineData("<p><span style=\"font-size:16px\">Piksele</span> <span style=\"font-size:150%\">Procent</span></p>", "<span style=\"font-size:12pt\">Piksele</span> <span style=\"font-size:16.5pt\">Procent</span>")]
    [InlineData("<ul style=\"list-style-type:square\"><li>Kwadrat</li></ul><ol style=\"list-style-type:upper-roman\"><li>Rzymska</li></ol>", "<ol type=\"I\"")]
    [InlineData("<p>Nieznany <custom-tag>znacznik</custom-tag> i <abbr>skrót</abbr></p>", "Nieznany znacznik i skrót")]
    [InlineData("<p><code>kod</code> <kbd>Ctrl</kbd></p>", "<span style=\"font-family:'Consolas'\">kod</span>")]
    public Task HtmlFromElsewhere_ConvergesAfterOnePass(string source, string expected) => WpfTestHost.RunAsync(() =>
    {
        var html = ToHtml(FlowDocumentHtml.ToDocument(source, Images));

        Assert.Contains(expected, html, StringComparison.Ordinal);
        Assert.Equal(html, ToHtml(FlowDocumentHtml.ToDocument(html, Images)));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("<p>Przed<script>alert(1)</script>po</p>", "Przedpo", "script")]
    [InlineData("<p><a href=\"javascript:alert(1)\">kliknij</a></p>", "kliknij", "javascript")]
    [InlineData("<p><a href=\"file:///C:/Windows\">plik</a></p>", "plik", "file:")]
    [InlineData("<p><img src=\"https://evil.example/x.png\">zdalny</p>", "zdalny", "evil.example")]
    [InlineData("<p><img src=\"cid:img-0000000000000000@admng\">brak</p>", "brak", "img-0000000000000000")]
    [InlineData("<p onclick=\"alert(1)\" style=\"position:absolute\">styl</p>", "styl", "onclick")]
    [InlineData("<p><span style=\"display:none\">ukryte</span>widoczne</p>", "widoczne", "ukryte")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe><p>ramka</p>", "ramka", "iframe")]
    public Task DangerousContent_NeverGetsThrough(string source, string kept, string removed) => WpfTestHost.RunAsync(() =>
    {
        var html = ToHtml(FlowDocumentHtml.ToDocument(source, Images));

        Assert.Contains(kept, html, StringComparison.Ordinal);
        Assert.DoesNotContain(removed, html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a", html, StringComparison.Ordinal);
        Assert.Empty(MessageHtmlSanitizer.Validate(html, Options()));
        return Task.CompletedTask;
    });

    [Fact]
    public Task HtmlNestedTooDeeply_GivesAnEmptyDocument_WithoutAStackOverflow() => WpfTestHost.RunAsync(() =>
    {
        var deep = string.Concat(Enumerable.Repeat("<span>", 5000)) + "głęboko" + string.Concat(Enumerable.Repeat("</span>", 5000));

        var document = FlowDocumentHtml.ToDocument("<p>" + deep + "</p>", Images);

        Assert.Equal(string.Empty, ToHtml(document));
        return Task.CompletedTask;
    });

    [Fact]
    public Task WhiteSpace_CollapsesAsInABrowser() => WpfTestHost.RunAsync(() =>
    {
        var document = FlowDocumentHtml.ToDocument("<p>  Zażółć\n   gęślą  <b> jaźń </b>  </p>\n\n<p>a<br>\n   b</p><p>&nbsp;&nbsp;wcięcie</p><p>&nbsp;</p>", Images);

        var paragraphs = document.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(4, paragraphs.Count);
        Assert.Equal("Zażółć gęślą jaźń", Text(paragraphs[0]));
        Assert.Equal("a\nb", Text(paragraphs[1]));
        Assert.Equal("\u00A0\u00A0wcięcie", Text(paragraphs[2]));
        Assert.Empty(paragraphs[3].Inlines);   // MailerWPiA's empty line: an empty paragraph again
        return Task.CompletedTask;
    });

    [Fact]
    public Task Images_AreTaggedSizedAndNamed_UnknownOnesDropped() => WpfTestHost.RunAsync(() =>
    {
        var source = $"<p><img src=\"cid:{Logo.ContentId.ToUpperInvariant()}\" alt=\"Logo – Dział IT\" width=\"120\" height=\"40\">"
            + $"<img src=\"cid:{Photo.ContentId}\" style=\"width:0.5in\">"
            + $"<img src=\"cid:{Logo.ContentId}\">"
            + "<img src=\"cid:img-ffffffffffffffff@admng\"></p>";

        var document = FlowDocumentHtml.ToDocument(source, Images);

        var images = Descendants<InlineUIContainer>(document).Select(c => (Image)c.Child).ToList();
        Assert.Equal(3, images.Count);
        Assert.Equal(Logo.ContentId, FlowDocumentHtml.ContentIdOf(images[0]));
        Assert.Equal((120d, 40d), (images[0].Width, images[0].Height));
        Assert.Equal("Logo – Dział IT", AutomationProperties.GetName(images[0]));
        Assert.NotNull(images[0].Source);
        Assert.Equal(Photo.ContentId, FlowDocumentHtml.ContentIdOf(images[1]));
        Assert.Equal((48d, 24d), (images[1].Width, images[1].Height));   // 0.5 in, the height in proportion
        Assert.Equal(((double)Logo.Width, (double)Logo.Height), (images[2].Width, images[2].Height));

        var html = ToHtml(document);
        Assert.Contains($"<img src=\"cid:{Logo.ContentId}\" alt=\"Logo – Dział IT\" width=\"120\" height=\"40\" style=\"max-width:100%;height:auto\">", html, StringComparison.Ordinal);
        Assert.Contains($"<img src=\"cid:{Photo.ContentId}\" width=\"48\" height=\"24\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ffffffffffffffff", html, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AnImageWithoutContentId_IsLeftOut() => WpfTestHost.RunAsync(() =>
    {
        var document = Document(P(new Run("Przed "), new InlineUIContainer(FlowDocumentHtml.CreateImage(Logo)), new Run(" po")));

        var html = FlowDocumentHtml.ToHtml(document, _ => null);

        Assert.Equal("<p style=\"margin:0 0 8pt 0\">Przed &nbsp;po</p>\n", html);
        Assert.DoesNotContain("img", html, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task OutlookPaste_ThroughTheU1cImporter_KeepsItsConstructs_AndConverges() => WpfTestHost.RunAsync(async () =>
    {
        var imported = await PastedHtmlImporter.ImportAsync(
            new HtmlImportRequest
            {
                Document = CfHtmlParser.Parse(CfHtmlFixture.Build(CfHtmlFixture.Html("outlook-classic"))),
                Files = new FakePastedImageSource(),
            },
            TestContext.Current.CancellationToken);
        var images = imported.Images.ToDictionary(i => i.ContentId, StringComparer.Ordinal);
        Assert.Equal(2, images.Count);

        var document = FlowDocumentHtml.ToDocument(imported.Html, images);
        var html = FlowDocumentHtml.ToHtml(document, FlowDocumentHtml.ContentIdOf);

        foreach (var part in new[]
        {
            "Szanowni Państwo,", "<span style=\"font-weight:bold;font-size:12pt;font-family:'Aptos',sans-serif;color:#C00000\">{sAMAccountName}</span>",
            "background-color:#FFFF00", "<ul style=\"margin:0 0 8pt 0\">", "<ul type=\"circle\" style=\"margin:0\">",
            "<ol style=\"margin:0 0 8pt 0\">", "<a href=\"https://haslo.contoso.pl/\">", "<a href=\"mailto:pomoc@contoso.pl\">",
            "Wybierz „Zmień hasło”.", "<table cellpadding=\"5\" cellspacing=\"0\"", "background-color:#D9F2D0", "Pomoc techniczna – Łódź",
            "alt=\"Logo – Dział IT\" width=\"120\" height=\"40\"", "Pozdrawiamy</span><br>", "Zespół Wsparcia IT – Żółć i Gęś sp. z o.o.",
        })
        {
            Assert.Contains(part, html, StringComparison.Ordinal);
        }

        Assert.All(images.Keys, id => Assert.Contains("cid:" + id, html, StringComparison.Ordinal));
        Assert.Equal(html, ToHtml(FlowDocumentHtml.ToDocument(html, images)));
        Assert.Empty(MessageHtmlSanitizer.Validate(html, Options(images.Keys)));
    });

    [Theory]
    [InlineData("#C00000", 0xC0, 0x00, 0x00, 0xFF)]
    [InlineData("#abc", 0xAA, 0xBB, 0xCC, 0xFF)]
    [InlineData("rgb(1, 2, 3)", 1, 2, 3, 0xFF)]
    [InlineData("rgba(255,0,0,0.5)", 255, 0, 0, 128)]
    [InlineData("rgb(100%,0%,50%)", 255, 0, 128, 0xFF)]
    [InlineData("Yellow", 255, 255, 0, 0xFF)]
    [InlineData("darkgrey", 0xA9, 0xA9, 0xA9, 0xFF)]
    [InlineData("windowtext", 0, 0, 0, 0xFF)]
    [InlineData("rebeccapurple", 0x66, 0x33, 0x99, 0xFF)]
    public void ParseColor_ReadsCssColours(string value, int r, int g, int b, int a) =>
        Assert.Equal(Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b), FlowDocumentHtml.ParseColor(value));

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#gggggg")]
    [InlineData("rgb(1,2)")]
    [InlineData("nie-kolor")]
    [InlineData("url(x)")]
    public void ParseColor_RefusesTheRest(string value) => Assert.Null(FlowDocumentHtml.ParseColor(value));

    [Theory]
    [InlineData("12pt", 16)]
    [InlineData("16px", 16)]
    [InlineData("2em", 29.333)]
    [InlineData("150%", 22)]
    [InlineData("1in", 96)]
    [InlineData("small", 13)]
    [InlineData("0.1px", 4)]
    [InlineData("9999px", 400)]
    public void FontSizePixels_ReadsCssSizes(string value, double expected) =>
        Assert.Equal(expected, FlowDocumentHtml.FontSizePixels(value, FlowDocumentHtml.DefaultFontSize)!.Value, 3);

    [Theory]
    [InlineData("auto")]
    [InlineData("-3pt")]
    [InlineData("duży")]
    public void FontSizePixels_RefusesTheRest(string value) => Assert.Null(FlowDocumentHtml.FontSizePixels(value, FlowDocumentHtml.DefaultFontSize));

    private static (FlowDocument Document, string[] Expected) Build(string construct)
    {
        const string Margin = "<p style=\"margin:0 0 8pt 0\">";
        switch (construct)
        {
            case "paragraphs":
                return (Document(P(new Run("Szanowni Państwo,")), new Paragraph(), P(new Run("Zażółć gęślą jaźń."))),
                    [Margin + "Szanowni Państwo,</p>", Margin + "&nbsp;</p>", Margin + "Zażółć gęślą jaźń.</p>"]);
            case "alignment":
                return (Document(Aligned("Lewo", TextAlignment.Left), Aligned("Środek", TextAlignment.Center), Aligned("Prawo", TextAlignment.Right), Aligned("Wyjustowany", TextAlignment.Justify)),
                    [Margin + "Lewo</p>", "<p style=\"margin:0 0 8pt 0;text-align:center\">Środek</p>", "text-align:right\">Prawo", "text-align:justify\">Wyjustowany"]);
            case "bold":
                return (Document(P(new Run("Zwykły "), new Bold(new Run("pogrubiony")), new Run(" i "), new Run("też") { FontWeight = FontWeights.Bold })),
                    ["Zwykły <span style=\"font-weight:bold\">pogrubiony</span> i <span style=\"font-weight:bold\">też</span>"]);
            case "italic":
                return (Document(P(new Italic(new Run("kursywa")))), ["<span style=\"font-style:italic\">kursywa</span>"]);
            case "underline":
                return (Document(P(new Underline(new Run("podkreślony")))), ["<span style=\"text-decoration:underline\">podkreślony</span>"]);
            case "strike":
                return (Document(P(new Run("przekreślony") { TextDecorations = TextDecorations.Strikethrough })), ["<span style=\"text-decoration:line-through\">przekreślony</span>"]);
            case "underline-strike":
                var both = new TextDecorationCollection(TextDecorations.Underline.Concat(TextDecorations.Strikethrough));
                return (Document(P(new Run("oba") { TextDecorations = both })), ["<span style=\"text-decoration:underline line-through\">oba</span>"]);
            case "colour":
                return (Document(P(new Run("czerwony") { Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0, 0)) }, new Run(" czarny") { Foreground = Brushes.Black })),
                    ["<span style=\"color:#C00000\">czerwony</span> czarny"]);
            case "highlight":
                return (Document(P(new Run("zakreślony") { Background = Brushes.Yellow }, new Run(" biały") { Background = Brushes.White })),
                    ["<span style=\"background-color:#FFFF00\">zakreślony</span> biały"]);
            case "size":
                return (Document(P(new Run("duży") { FontSize = 14 * 96.0 / 72 }, new Run(" prawie") { FontSize = 11.4 * 96.0 / 72 })),
                    ["<span style=\"font-size:14pt\">duży</span> prawie"]);
            case "font":
                return (Document(P(new Run("szeryfowy") { FontFamily = new FontFamily("Times New Roman") }, new Run(" Calibri") { FontFamily = new FontFamily("Calibri Light") })),
                    ["<span style=\"font-family:'Times New Roman'\">szeryfowy</span> Calibri"]);
            case "generic-font":
                return (Document(P(new Run("Aptos") { FontFamily = new FontFamily("Aptos, sans-serif") })), ["<span style=\"font-family:'Aptos',sans-serif\">Aptos</span>"]);
            case "mixed":
                return (Document(P(new Run("wszystko")
                {
                    FontWeight = FontWeights.Bold,
                    FontStyle = FontStyles.Italic,
                    TextDecorations = TextDecorations.Underline,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79)),
                    Background = new SolidColorBrush(Color.FromRgb(0xD9, 0xF2, 0xD0)),
                    FontSize = 9 * 96.0 / 72,
                    FontFamily = new FontFamily("Georgia"),
                })),
                    ["<span style=\"font-style:italic;font-weight:bold;font-size:9pt;font-family:'Georgia';color:#1F4E79;background-color:#D9F2D0;text-decoration:underline\">wszystko</span>"]);
            case "nested-spans":
                return (Document(P(new Span(new Bold(new Italic(new Run("zagnieżdżony")))))), ["<span style=\"font-style:italic;font-weight:bold\">zagnieżdżony</span>"]);
            case "link":
                return (Document(P(new Run("Zobacz "), Link("https://www.contoso.pl/haslo?x=1&y=2", new Run("stronę")), new Run("."))),
                    ["Zobacz <a href=\"https://www.contoso.pl/haslo?x=1&amp;y=2\">stronę</a>."]);
            case "mail-link":
                return (Document(P(Link("mailto:pomoc@contoso.pl", new Run("pomoc@contoso.pl")))), ["<a href=\"mailto:pomoc@contoso.pl\">pomoc@contoso.pl</a>"]);
            case "field-link":
                return (Document(P(Link("{{Strona sprawy}}", new Run("sprawa"), new Run(" pilna") { FontWeight = FontWeights.Bold }))),
                    ["<a href=\"{{Strona sprawy}}\">sprawa<span style=\"font-weight:bold\"> pilna</span></a>"]);
            case "line-break":
                return (Document(P(new Run("Pierwsza linia"), new LineBreak(), new Run("Druga linia"))), ["Pierwsza linia<br>Druga linia"]);
            case "trailing-break":
                return (Document(P(new Run("Koniec"), new LineBreak()), P(new LineBreak())), ["Koniec<br>\n<br>\n</p>", Margin + "<br>\n<br>\n</p>"]);
            case "spaces":
                return (Document(P(new Run("a   b"), new Run("c  "), new Run("d"))), ["a &nbsp;&nbsp;bc &nbsp;d"]);
            case "leading-space":
                return (Document(P(new Run(" wcięcie"), new LineBreak(), new Run("  dwa"))), [Margin + "&nbsp;wcięcie<br>&nbsp; dwa</p>"]);
            case "tab":
                return (Document(P(new Run("x\ty"))), ["x &nbsp;&nbsp;&nbsp;y"]);
            case "bullets":
                return (Document(List(TextMarkerStyle.Disc, "Pierwszy", "Drugi")), ["<ul style=\"margin:0 0 8pt 0\"><li>Pierwszy</li>\n<li>Drugi</li>\n</ul>"]);
            case "numbering":
                return (Document(List(TextMarkerStyle.Decimal, "Jeden", "Dwa")), ["<ol style=\"margin:0 0 8pt 0\"><li>Jeden</li>"]);
            case "list-types":
                var latin = List(TextMarkerStyle.LowerLatin, "a)", "b)");
                latin.StartIndex = 3;
                return (Document(latin, List(TextMarkerStyle.UpperRoman, "I"), List(TextMarkerStyle.Circle, "o"), List(TextMarkerStyle.Square, "■")),
                    ["<ol start=\"3\" type=\"a\" style=\"margin:0 0 8pt 0\">", "<ol type=\"I\"", "<ul type=\"circle\"", "<ul type=\"square\""]);
            case "list-none":
                return (Document(List(TextMarkerStyle.None, "bez znacznika")), ["<ul style=\"margin:0 0 8pt 0;list-style-type:none\">"]);
            case "nested-list":
                var outer = List(TextMarkerStyle.Disc, "Poziom 1");
                outer.ListItems.FirstListItem!.Blocks.Add(List(TextMarkerStyle.Circle, "Poziom 2"));
                return (Document(outer), ["<li>Poziom 1<ul type=\"circle\" style=\"margin:0\"><li>Poziom 2</li>"]);
            case "list-paragraphs":
                var item = new ListItem(P(new Run("Akapit pierwszy")));
                item.Blocks.Add(new Paragraph());
                item.Blocks.Add(Aligned("Akapit trzeci", TextAlignment.Center));
                var several = new List { MarkerStyle = TextMarkerStyle.Disc };
                several.ListItems.Add(item);
                return (Document(several), ["<li><div>Akapit pierwszy</div>\n<div>&nbsp;</div>\n<div style=\"text-align:center\">Akapit trzeci</div>\n</li>"]);
            case "table":
                return (Document(Table()), ["<table cellpadding=\"5\" cellspacing=\"0\" style=\"margin:0 0 8pt 0;border-collapse:collapse\">", "colspan=\"2\"", "rowspan=\"2\"",
                    "border:1px solid #BFBFBF", "background-color:#D9F2D0", "Pomoc techniczna – Łódź", "+48 42 123 45 67"]);
            case "image":
                return (Document(P(new Run("Logo: "), new InlineUIContainer(FlowDocumentHtml.CreateImage(Logo, 28, 12, "Logo – Dział IT")), new Run(" koniec"))),
                    [$"Logo: <img src=\"cid:{Logo.ContentId}\" alt=\"Logo – Dział IT\" width=\"28\" height=\"12\" style=\"max-width:100%;height:auto\"> koniec"]);
            case "block-image":
                return (Document(P(new Run("Nad")), new BlockUIContainer(FlowDocumentHtml.CreateImage(Photo)), P(new Run("Pod"))),
                    [Margin + $"<img src=\"cid:{Photo.ContentId}\" width=\"20\" height=\"10\" style=\"max-width:100%;height:auto\"></p>"]);
            case "rule":
                return (Document(P(new Run("Nad")), new BlockUIContainer(new Border { Height = 1, Tag = FlowDocumentHtml.HorizontalRuleTag }), P(new Run("Pod"))),
                    ["Nad</p>\n<hr>\n" + Margin + "Pod</p>"]);
            case "section":
                return (Document(new Section(P(new Run("W sekcji")))), [Margin + "W sekcji</p>"]);
            case "fields":
                return (Document(P(new Run("{{Zwrot osobowy}}, w sprawie {{Nr sprawy|brak numeru}} – {{nazwa}}"))),
                    ["{{Zwrot osobowy}}, w sprawie {{Nr sprawy|brak numeru}} – {{nazwa}}"]);
            default:
                throw new ArgumentOutOfRangeException(nameof(construct), construct, null);
        }
    }

    private static string AssertStable(FlowDocument document)
    {
        var html = ToHtml(document);
        Assert.NotEqual(string.Empty, html);
        var again = ToHtml(FlowDocumentHtml.ToDocument(html, Images));
        Assert.Equal(html, again);
        return html;
    }

    private static string ToHtml(FlowDocument document) => FlowDocumentHtml.ToHtml(document, FlowDocumentHtml.ContentIdOf);

    private static HtmlSanitizeOptions Options(IEnumerable<string>? imageIds = null) => new()
    {
        Mode = HtmlSanitizeMode.Fragment,
        InlineImageIds = new HashSet<string>(imageIds ?? Images.Keys, StringComparer.OrdinalIgnoreCase),
        AllowFieldUrls = true,
    };

    private static FlowDocument Document(params Block[] blocks)
    {
        var document = FlowDocumentHtml.NewDocument();
        foreach (var block in blocks)
        {
            document.Blocks.Add(block);
        }

        return document;
    }

    private static Paragraph P(params Inline[] inlines)
    {
        var paragraph = new Paragraph();
        foreach (var inline in inlines)
        {
            paragraph.Inlines.Add(inline);
        }

        return paragraph;
    }

    private static Paragraph Aligned(string text, TextAlignment alignment) => new(new Run(text)) { TextAlignment = alignment };

    private static Hyperlink Link(string address, params Inline[] inlines)
    {
        var link = new Hyperlink { NavigateUri = new Uri(address, UriKind.RelativeOrAbsolute), Foreground = FlowDocumentHtml.LinkBrush };
        foreach (var inline in inlines)
        {
            link.Inlines.Add(inline);
        }

        return link;
    }

    private static List List(TextMarkerStyle marker, params string[] items)
    {
        var list = new List { MarkerStyle = marker };
        foreach (var text in items)
        {
            list.ListItems.Add(new ListItem(new Paragraph(new Run(text))));
        }

        return list;
    }

    private static Table Table()
    {
        var table = new Table();
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        var header = new TableRow();
        header.Cells.Add(new TableCell(new Paragraph(new Bold(new Run("Dział i telefon")))) { ColumnSpan = 2, Background = new SolidColorBrush(Color.FromRgb(0xD9, 0xF2, 0xD0)) });
        group.Rows.Add(header);
        var first = new TableRow();
        first.Cells.Add(new TableCell(new Paragraph(new Run("Pomoc techniczna – Łódź"))) { RowSpan = 2 });
        first.Cells.Add(new TableCell(new Paragraph(new Run("+48 42 123 45 67"))));
        group.Rows.Add(first);
        var second = new TableRow();
        second.Cells.Add(new TableCell(new Paragraph()));
        group.Rows.Add(second);
        return table;
    }

    /// <summary>The text of a paragraph, a line break as "\n".</summary>
    private static string Text(Paragraph paragraph) => Text(paragraph.Inlines);

    private static string Text(InlineCollection inlines) => string.Concat(inlines.Select(inline => inline switch
    {
        Run run => run.Text,
        LineBreak => "\n",
        Span span => Text(span.Inlines),
        _ => string.Empty,
    }));

    private static IEnumerable<T> Descendants<T>(FlowDocument document)
        where T : DependencyObject
    {
        var stack = new Stack<object>(document.Blocks.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T match)
            {
                yield return match;
            }

            IEnumerable<object> children = node switch
            {
                Paragraph p => p.Inlines,
                Span s => s.Inlines,
                List l => l.ListItems,
                ListItem i => i.Blocks,
                Section s => s.Blocks,
                Table t => t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells),
                TableCell c => c.Blocks,
                _ => [],
            };
            foreach (var child in children.Reverse())
            {
                stack.Push(child);
            }
        }
    }
}
