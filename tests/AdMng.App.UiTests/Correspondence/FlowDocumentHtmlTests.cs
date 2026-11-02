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

    // ---- the layout of the HTML: the editor's own body (keepLayout) -----------------------------------------------------

    [Theory]
    [MemberData(nameof(Constructs))]
    public Task KeptLayout_OfTheEditorsOwnHtml_IsTheSameHtml(string construct) => WpfTestHost.RunAsync(() =>
    {
        // Bodies the editor wrote before (grid tables, 8 pt spacing) read back with their layout give the same HTML.
        var html = ToHtml(Build(construct).Document);

        Assert.Equal(html, ToHtml(FlowDocumentHtml.ToDocument(html, Images, keepLayout: true)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheMailboxTemplate_KeepsItsLook_ThroughTheEditor() => WpfTestHost.RunAsync(() =>
    {
        var golden = TemplateHtmlFixture.MailboxStillNeeded;

        var html = ToHtml(FlowDocumentHtml.ToDocument(golden, Images, keepLayout: true));

        // Semantically the same: cell borders, backgrounds and paddings, no border on the outer table, paragraph and list
        // margins (a div's margin now on its paragraph), Word's spacing, the list, the link's style, colours and bold.
        Assert.Equal(HtmlLayoutFacts.Of(golden), HtmlLayoutFacts.Of(html));
        foreach (var part in new[]
        {
            "<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;max-width:495pt\">",
            "<td style=\"padding:10.5pt 13.5pt 10.5pt 13.5pt;border:none;border-left:3pt solid #1B365D;background-color:#F4F7FB\">",
            "<td style=\"padding:9pt 13.5pt 9pt 13.5pt;border:none;border-left:3pt solid #D97706;background-color:#FFFBEB\">",
            "<p style=\"margin:0 0 10.5pt 0\"><span style=\"font-weight:bold\">Szanowni Państwo,</span></p>",
            "<p style=\"mso-margin-top-alt:12pt;margin:0 0 12pt 0\">",
            "<a href=\"https://hd.uni.lodz.pl\" style=\"color:blue;text-decoration:underline\"><span style=\"font-weight:bold;color:#0056B3\">hd.uni.lodz.pl</span></a>",
            "<li style=\"mso-margin-top-alt:auto;margin:0 0 3pt 0\">", "<span style=\"font-weight:bold\">{{Termin}}</span>",
            "<p style=\"margin:12pt 0 4.5pt 0\">Z góry dziękujemy za współpracę.</p>", "{{podpis}}",
        })
        {
            Assert.Contains(part, html, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Hex(FlowDocumentHtml.CellBorderColor), html, StringComparison.Ordinal);   // no grid
        Assert.Equal(html, ToHtml(FlowDocumentHtml.ToDocument(html, Images, keepLayout: true)));
        Assert.Empty(MessageHtmlSanitizer.Validate(html, Options()));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheMailboxTemplate_InTheEditor_HasItsBordersPaddingsAndMargins() => WpfTestHost.RunAsync(() =>
    {
        var document = FlowDocumentHtml.ToDocument(TemplateHtmlFixture.MailboxStillNeeded, Images, keepLayout: true);

        var blocks = document.Blocks.ToList();
        Assert.Equal(3, blocks.Count);
        var layout = Assert.IsType<Table>(blocks[0]);
        Assert.Equal(0, layout.CellSpacing);
        AssertThickness(new Thickness(0), layout.Margin);   // none of its own: 0, not a WPF table's auto margin
        var outer = Assert.Single(Cells(layout));
        AssertThickness(new Thickness(0), outer.BorderThickness);
        AssertThickness(new Thickness(0), outer.Padding);

        // Three paragraphs (each div's bottom margin on its paragraph), the two boxes, two paragraphs.
        var inside = outer.Blocks.ToList();
        Assert.Equal(7, inside.Count);
        AssertThickness(new Thickness(0, 0, 0, 14), inside[0].Margin);
        AssertThickness(new Thickness(0, 0, 0, 12), inside[1].Margin);
        AssertThickness(new Thickness(0, 0, 0, 14), inside[2].Margin);
        AssertThickness(new Thickness(0, 16, 0, 6), inside[5].Margin);
        AssertThickness(new Thickness(0, 0, 0, 16), inside[6].Margin);

        AssertThickness(new Thickness(0), inside[3].Margin);
        AssertThickness(new Thickness(0), inside[4].Margin);
        var blue = Assert.Single(Cells(Assert.IsType<Table>(inside[3])));
        AssertThickness(new Thickness(4, 0, 0, 0), blue.BorderThickness);
        Assert.Equal(Color.FromRgb(0x1B, 0x36, 0x5D), ((SolidColorBrush)blue.BorderBrush).Color);
        AssertThickness(new Thickness(18, 14, 18, 14), blue.Padding);
        Assert.Equal(Color.FromRgb(0xF4, 0xF7, 0xFB), ((SolidColorBrush)blue.Background).Color);
        var box = blue.Blocks.ToList();
        Assert.Equal(3, box.Count);
        AssertThickness(new Thickness(0, 0, 0, 16), box[0].Margin);   // the paragraph's 12 pt over the div's 7.5 pt
        AssertThickness(new Thickness(0, 0, 0, 16), box[1].Margin);
        var list = Assert.IsType<List>(box[2]);
        Assert.Equal(TextMarkerStyle.Decimal, list.MarkerStyle);
        AssertThickness(new Thickness(0), list.Margin);
        AssertThickness(new Thickness(0, 0, 0, 4), list.ListItems.FirstListItem!.Margin);
        AssertThickness(new Thickness(0), list.ListItems.LastListItem!.Margin);

        var yellow = Assert.Single(Cells(Assert.IsType<Table>(inside[4])));
        AssertThickness(new Thickness(4, 0, 0, 0), yellow.BorderThickness);
        Assert.Equal(Color.FromRgb(0xD9, 0x77, 0x06), ((SolidColorBrush)yellow.BorderBrush).Color);
        AssertThickness(new Thickness(18, 12, 18, 12), yellow.Padding);
        Assert.Equal(Color.FromRgb(0xFF, 0xFB, 0xEB), ((SolidColorBrush)yellow.Background).Color);

        // The link keeps its own colour and underline; its text its bold and colour, without an underline of its own.
        var link = Assert.Single(Descendants<Hyperlink>(document));
        Assert.Equal("https://hd.uni.lodz.pl", link.NavigateUri.OriginalString);
        Assert.Equal(Colors.Blue, ((SolidColorBrush)link.Foreground).Color);
        var linkText = Assert.IsType<Run>(Assert.Single(link.Inlines));
        Assert.Equal(FontWeights.Bold, linkText.FontWeight);
        Assert.Equal(Color.FromRgb(0x00, 0x56, 0xB3), ((SolidColorBrush)linkText.Foreground).Color);
        Assert.Equal(DependencyProperty.UnsetValue, linkText.ReadLocalValue(Inline.TextDecorationsProperty));

        var team = Assert.IsType<Paragraph>(blocks[1]);
        AssertThickness(new Thickness(0), team.Margin);
        var teamText = Assert.IsType<Run>(Assert.Single(team.Inlines));
        Assert.Equal("Arial, sans-serif", teamText.FontFamily.Source);
        Assert.Equal(Color.FromRgb(0xCF, 0x0E, 0x3E), ((SolidColorBrush)teamText.Foreground).Color);

        // The signature field's paragraph has no margin of its own (the message's spacing; the renderer replaces it).
        Assert.Equal("{{podpis}}", Text(Assert.IsType<Paragraph>(blocks[2])));
        Assert.Equal(DependencyProperty.UnsetValue, blocks[2].ReadLocalValue(Block.MarginProperty));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("border:none;border-left:3pt solid #1B365D;border-bottom:1px dashed red", 4, 0, 0, 1, "#1B365D")]
    [InlineData("border:1px solid #CCCCCC;border-top:none", 1, 0, 1, 1, "#CCCCCC")]
    [InlineData("border-width:2px;border-style:solid;border-color:#123456", 2, 2, 2, 2, "#123456")]
    [InlineData("border-top:thick double #00FF00;border-right:thin solid #0000FF", 0, 5, 1, 0, "#00FF00")]
    [InlineData("border-left-width:6px;border-left-style:solid;border-left-color:#AA0000", 6, 0, 0, 0, "#AA0000")]
    public Task KeptLayout_CellBorders_PerSide_InTheColourOfTheWidest(string css, double left, double top, double right, double bottom, string color) =>
        WpfTestHost.RunAsync(() =>
        {
            var html = $"<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\"><tr><td style=\"{css}\"><p style=\"margin:0\">Komórka</p></td></tr></table>";

            var cell = Assert.Single(Cells(Assert.IsType<Table>(Assert.Single(FlowDocumentHtml.ToDocument(html, Images, keepLayout: true).Blocks))));

            AssertThickness(new Thickness(left, top, right, bottom), cell.BorderThickness);
            Assert.Equal(color, Hex(((SolidColorBrush)cell.BorderBrush).Color));
            var written = ToHtml(FlowDocumentHtml.ToDocument(html, Images, keepLayout: true));
            Assert.Equal(HtmlLayoutFacts.Of(html), HtmlLayoutFacts.Of(written));
            return Task.CompletedTask;
        });

    [Fact]
    public Task KeptLayout_TablesWithoutBorders_AndGridTables() => WpfTestHost.RunAsync(() =>
    {
        // border="0" and no cell border: none, padding from cellpadding (else the browser's 1 px), no margin of its own
        // (shown as 0, not a WPF table's auto margin; none written back).
        var document = FlowDocumentHtml.ToDocument(
            "<table border=\"0\" cellpadding=\"4\"><tr><td>a</td><td style=\"padding:0.5cm 6px\">b</td></tr></table><table border=\"0\"><tr><td>c</td></tr></table>",
            Images, keepLayout: true);
        var tables = document.Blocks.Select(Assert.IsType<Table>).ToList();
        Assert.Equal(2, tables.Count);
        var borderless = tables[0];
        var cells = Cells(borderless).ToList();
        AssertThickness(new Thickness(0), cells[0].BorderThickness);
        AssertThickness(new Thickness(4), cells[0].Padding);
        AssertThickness(new Thickness(6, 96 / 2.54 / 2, 6, 96 / 2.54 / 2), cells[1].Padding);
        AssertThickness(new Thickness(0), borderless.Margin);
        Assert.Equal(2, borderless.CellSpacing);   // without cellspacing and border-collapse: a browser's 2 px
        AssertThickness(new Thickness(0), tables[1].Margin);
        AssertThickness(new Thickness(1), Assert.Single(Cells(tables[1])).Padding);
        var html = ToHtml(document);
        Assert.Contains("<table border=\"0\" cellpadding=\"4\"><tbody><tr><td><p style=\"margin:0 0 8pt 0\">a</p>", html, StringComparison.Ordinal);
        Assert.Contains("<td style=\"padding:0.5cm 6px\">", html, StringComparison.Ordinal);
        Assert.Contains("<table border=\"0\"><tbody><tr><td><p style=\"margin:0 0 8pt 0\">c</p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("border:", html, StringComparison.Ordinal);

        // A table with borders of its own keeps them; one without any border keeps MailerWPiA's grid, written as shown.
        var grid = ToHtml(FlowDocumentHtml.ToDocument("<table><tr><td>Dział</td><td>Telefon</td></tr></table>", Images, keepLayout: true));
        Assert.Contains("<table cellpadding=\"5\" cellspacing=\"0\" style=\"margin:0 0 8pt 0;border-collapse:collapse\">", grid, StringComparison.Ordinal);
        Assert.Equal(2, Count(grid, "<td style=\"border:1px solid #BFBFBF\">"));
        var gridCell = Cells(Assert.IsType<Table>(Assert.Single(FlowDocumentHtml.ToDocument(grid, Images, keepLayout: true).Blocks))).First();
        AssertThickness(new Thickness(1), gridCell.BorderThickness);
        AssertThickness(new Thickness(5), gridCell.Padding);
        return Task.CompletedTask;
    });

    [Fact]
    public Task PastedTables_KeepTheGrid_AsBefore() => WpfTestHost.RunAsync(() =>
    {
        // A paste is read as MailerWPiA read it: whatever the table says, a grid with the message's spacing.
        const string Html = "<table border=\"0\" cellpadding=\"0\"><tr><td style=\"border:none;border-left:3pt solid #1B365D;padding:9pt\">Komórka</td></tr></table>";

        var html = ToHtml(FlowDocumentHtml.ToDocument(Html, Images));

        Assert.Contains("<table cellpadding=\"5\" cellspacing=\"0\" style=\"margin:0 0 8pt 0;border-collapse:collapse\">", html, StringComparison.Ordinal);
        Assert.Contains("<td style=\"border:1px solid #BFBFBF;vertical-align:top\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("border-left", html, StringComparison.Ordinal);
        Assert.DoesNotContain("9pt", html, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task KeptLayout_DivMargins_StackOntoTheBlocksInside() => WpfTestHost.RunAsync(() =>
    {
        // The grouped template: the div's top margin on its first paragraph, its bottom margin on the last one.
        const string Html = "<div style=\"margin-top:6pt;margin-bottom:10.5pt\"><p style=\"margin:0\">Pierwszy</p><p style=\"margin:0\">{{lista}}</p>"
            + "<p style=\"margin:0\">Ostatni</p></div><div style=\"margin-bottom:4pt;line-height:1.5\">Tekst bez akapitu</div>";

        var document = FlowDocumentHtml.ToDocument(Html, Images, keepLayout: true);

        var paragraphs = document.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(4, paragraphs.Count);
        AssertThickness(new Thickness(0, 8, 0, 0), paragraphs[0].Margin);
        AssertThickness(new Thickness(0), paragraphs[1].Margin);
        AssertThickness(new Thickness(0, 0, 0, 14), paragraphs[2].Margin);
        AssertThickness(new Thickness(0, 0, 0, 4 * 96.0 / 72), paragraphs[3].Margin);   // text right in the div: the div's margin
        var html = ToHtml(document);
        Assert.Contains("<p style=\"margin:6pt 0 0 0\">Pierwszy</p>\n<p style=\"margin:0\">{{lista}}</p>\n<p style=\"margin:0 0 10.5pt 0\">Ostatni</p>", html, StringComparison.Ordinal);
        Assert.Contains("<p style=\"margin:0 0 4pt 0;line-height:1.5\">Tekst bez akapitu</p>", html, StringComparison.Ordinal);
        Assert.Equal(HtmlLayoutFacts.Of(Html).Where(f => !f.Contains("Tekst bez akapitu", StringComparison.Ordinal)),
            HtmlLayoutFacts.Of(html).Where(f => !f.Contains("Tekst bez akapitu", StringComparison.Ordinal)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task KeptLayout_ListAndItemMargins_ASingleParagraphsOwnGoToItsItem() => WpfTestHost.RunAsync(() =>
    {
        const string Html = "<ul style=\"margin:0 0 6pt 0\"><li style=\"margin-bottom:2pt\">Pierwszy</li><li><p style=\"margin:0 0 4pt 0\">Drugi</p></li></ul>"
            + "<ol style=\"margin-bottom:0\"><li style=\"mso-margin-top-alt:auto;margin:0\">Trzeci</li></ol>";

        var document = FlowDocumentHtml.ToDocument(Html, Images, keepLayout: true);

        var lists = document.Blocks.OfType<List>().ToList();
        AssertThickness(new Thickness(0, 0, 0, 8), lists[0].Margin);
        AssertThickness(new Thickness(0, 0, 0, 2 * 96.0 / 72), lists[0].ListItems.FirstListItem!.Margin);
        AssertThickness(new Thickness(0, 0, 0, 4 * 96.0 / 72), lists[0].ListItems.LastListItem!.Margin);
        AssertThickness(new Thickness(0), ((Paragraph)lists[0].ListItems.LastListItem!.Blocks.FirstBlock).Margin);
        AssertThickness(new Thickness(0), lists[1].Margin);
        Assert.Equal(
            "<ul style=\"margin:0 0 6pt 0\"><li style=\"margin:0 0 2pt 0\">Pierwszy</li>\n<li style=\"margin:0 0 4pt 0\">Drugi</li>\n</ul>\n"
            + "<ol style=\"margin:0\"><li style=\"mso-margin-top-alt:auto;margin:0\">Trzeci</li>\n</ol>\n",
            ToHtml(document));
        return Task.CompletedTask;
    });

    [Fact]
    public Task KeptLayout_ALinksOwnStyle_StaysOnTheLink() => WpfTestHost.RunAsync(() =>
    {
        const string Html = "<p>Zobacz <a href=\"https://www.contoso.pl/\" style=\"color:#C00000;text-decoration:none\">stronę</a>.</p>";

        var document = FlowDocumentHtml.ToDocument(Html, Images, keepLayout: true);

        var link = Assert.Single(Descendants<Hyperlink>(document));
        Assert.Equal(Color.FromRgb(0xC0, 0x00, 0x00), ((SolidColorBrush)link.Foreground).Color);
        Assert.Empty(link.TextDecorations);
        Assert.Equal(DependencyProperty.UnsetValue, Assert.IsType<Run>(Assert.Single(link.Inlines)).ReadLocalValue(TextElement.ForegroundProperty));
        Assert.Equal("<p style=\"margin:0 0 8pt 0\">Zobacz <a href=\"https://www.contoso.pl/\" style=\"color:#C00000;text-decoration:none\">stronę</a>.</p>\n", ToHtml(document));

        // A paste gives the colour to the text, as before.
        Assert.Contains("<a href=\"https://www.contoso.pl/\"><span style=\"color:#C00000\">stronę</span></a>", ToHtml(FlowDocumentHtml.ToDocument(Html, Images)), StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task KeptLayout_TheCssTheEditorCannotShow_IsWrittenBack() => WpfTestHost.RunAsync(() =>
    {
        const string Html = "<p style=\"line-height:1.4;text-indent:12pt;mso-margin-bottom-alt:auto\">Akapit</p>"
            + "<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"600\" align=\"center\" style=\"max-width:495pt;table-layout:fixed\">"
            + "<tr><td width=\"50%\" valign=\"top\" style=\"vertical-align:top;height:20px;background-color:#FFFFFF\">Komórka</td></tr></table>";

        var html = ToHtml(FlowDocumentHtml.ToDocument(Html, Images, keepLayout: true));

        foreach (var part in new[]
        {
            "mso-margin-bottom-alt:auto", "line-height:1.4", "text-indent:12pt", "width=\"600\"", "align=\"center\"", "max-width:495pt",
            "table-layout:fixed", "width=\"50%\"", "valign=\"top\"", "vertical-align:top", "height:20px", "background-color:#FFFFFF",
        })
        {
            Assert.Contains(part, html, StringComparison.Ordinal);
        }

        Assert.Equal(html, ToHtml(FlowDocumentHtml.ToDocument(html, Images, keepLayout: true)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task KeptLayout_EmptyParagraphsAndLineBreaks_KeepTheirSpacing() => WpfTestHost.RunAsync(() =>
    {
        const string Html = "<p style=\"margin:0\">Pierwsza linia<br>druga linia</p><p style=\"margin:0\">&nbsp;</p><p style=\"margin:0 0 12pt 0\">Dalej</p>";

        var html = ToHtml(FlowDocumentHtml.ToDocument(Html, Images, keepLayout: true));

        Assert.Equal("<p style=\"margin:0\">Pierwsza linia<br>druga linia</p>\n<p style=\"margin:0\">&nbsp;</p>\n<p style=\"margin:0 0 12pt 0\">Dalej</p>\n", html);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheBaseFont_OfTheDocument_IsWrittenWithoutAFontOfItsOwn() => WpfTestHost.RunAsync(() =>
    {
        const string Html = "<p><span style=\"font-family:Aptos,sans-serif;font-size:12pt\">bazowy</span> <span style=\"font-family:Arial\">inny</span> "
            + "<span style=\"font-size:14pt\">większy</span></p>";
        var document = FlowDocumentHtml.ToDocument(Html, Images, keepLayout: true);

        // The message's default (Calibri 11 pt): every font is written.
        Assert.Contains("<span style=\"font-size:12pt;font-family:'Aptos',sans-serif\">bazowy</span>", ToHtml(document), StringComparison.Ordinal);

        // The base font Aptos 12 pt (as the editor sets it): text in it is written without one.
        document.FontFamily = FlowDocumentHtml.EditorFontFamily("Aptos,sans-serif");
        document.FontSize = 12 * 96.0 / 72;
        Assert.Equal("<p style=\"margin:0 0 8pt 0\">bazowy <span style=\"font-family:'Arial'\">inny</span> <span style=\"font-size:14pt\">większy</span></p>\n", ToHtml(document));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("Calibri,'Segoe UI',Arial,sans-serif", "Calibri")]
    [InlineData("'Aptos', sans-serif", "Aptos")]
    [InlineData("\"Times New Roman\",serif", "Times New Roman")]
    [InlineData("sans-serif", "Arial")]
    [InlineData("monospace", "Consolas")]
    [InlineData("<b>,Georgia", "Georgia")]
    [InlineData("", "Calibri")]
    [InlineData(null, "Calibri")]
    public void EditorFontFamily_IsTheFirstUsableFamily(string? css, string expected) => Assert.Equal(expected, FlowDocumentHtml.EditorFamilyName(css));

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

    private static IEnumerable<TableCell> Cells(Table table) => table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells);

    private static void AssertThickness(Thickness expected, Thickness actual)
    {
        Assert.Equal(expected.Left, actual.Left, 2);
        Assert.Equal(expected.Top, actual.Top, 2);
        Assert.Equal(expected.Right, actual.Right, 2);
        Assert.Equal(expected.Bottom, actual.Bottom, 2);
    }

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static int Count(string text, string part) => (text.Length - text.Replace(part, string.Empty, StringComparison.Ordinal).Length) / part.Length;

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
