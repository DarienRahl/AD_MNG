using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;
using AdMng.Testing.RichHtml;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>Paste → import: the expected output of every fixture, the problems, images, links and the text suggestion.</summary>
public class PastedHtmlImporterTests
{
    private static readonly UserMessageFields Fields = new();

    private static Task<HtmlImportResult> ImportAsync(string name, FakePastedImageSource? files = null, byte[]? bitmap = null) =>
        PastedHtmlImporter.ImportAsync(
            new HtmlImportRequest
            {
                Document = CfHtmlParser.Parse(CfHtmlFixture.Build(CfHtmlFixture.Html(name), CfHtmlFixture.SourceUrl(name))),
                Files = files ?? new FakePastedImageSource(),
                ClipboardBitmap = bitmap,
                AllowedLinkDomains = ["contoso.pl"],
                Engine = Fields.Engine,
            },
            TestContext.Current.CancellationToken);

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task EveryFixture_GivesItsExpectedHtml(string name)
    {
        var result = await ImportAsync(name);
        Assert.Equal(CfHtmlFixture.Expected(name), result.Html);
        Assert.Empty(UserMessageRules.FindForbiddenHtml(result.Html));
        var ids = result.Images.Select(i => i.ContentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(result.Html, MessageHtmlSanitizer.Sanitize(result.Html, new HtmlSanitizeOptions { InlineImageIds = ids }).Html);
    }

    public static TheoryData<string> FixtureNames() => [.. CfHtmlFixture.Names];

    [Fact]
    public async Task OutlookClassic_TheDesignNoteExample()
    {
        var result = await ImportAsync("outlook-classic");
        var html = result.Html;

        // Class rules inlined, mso-* gone, numbers normalized, font family re-quoted.
        Assert.StartsWith("<p style=\"margin:0cm;font-size:12pt;font-family:'Aptos',sans-serif\">Szanowni Państwo,</p>\n", html, StringComparison.Ordinal);
        Assert.DoesNotContain("mso-", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=", html, StringComparison.Ordinal);

        // The field split by SpellE / bold runs is one field again, formatting kept.
        Assert.Contains("hasło do konta <b><span style=\"color:#C00000\">{sAMAccountName}</span></b> wygaśnie {$passwordExpires:d MMMM yyyy}", html, StringComparison.Ordinal);
        Assert.Contains("Dzień dobry {givenName}, <span style=\"background-color:yellow\">prosimy o zmianę hasła</span>", html, StringComparison.Ordinal);
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.PlaceholderRepaired && p.Detail == "{$passwordExpiresInDays}");

        // Word lists became real lists, nested, with the Safe Links address unwrapped.
        Assert.Contains("<ul style=\"margin-top:0;margin-bottom:0\"><li style=\"font-size:12pt;font-family:'Aptos',sans-serif\">Zmień hasło na stronie <a href=\"https://haslo.contoso.pl/\" style=\"color:#467886;text-decoration:underline\">haslo.contoso.pl</a>.<ul style=\"margin-top:0;margin-bottom:0;list-style-type:circle\">", html, StringComparison.Ordinal);
        Assert.Contains("<ol style=\"margin-top:0;margin-bottom:0\"><li style=\"font-size:12pt;font-family:'Aptos',sans-serif\">Naciśnij Ctrl+Alt+Del.</li>", html, StringComparison.Ordinal);
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.LinkUnwrapped && p.Detail == "haslo.contoso.pl");
        Assert.DoesNotContain("safelinks", html, StringComparison.Ordinal);

        // The bordered table keeps borders, widths and the cell shading.
        Assert.Contains("border:solid #000000 1pt", html, StringComparison.Ordinal);
        Assert.Contains("background-color:#D9F2D0", html, StringComparison.Ordinal);

        // The VML shape is gone; its <![if !vml]> fallback image and the plain image are embedded.
        Assert.Equal(2, result.Images.Count);
        Assert.Equal(["clip_image002.png", "clip_image003.jpg"], result.Images.Select(i => i.FileName));
        Assert.Contains($"<img src=\"cid:{result.Images[0].ContentId}\" alt=\"Logo – Dział IT\" width=\"120\" height=\"40\" style=\"width:1.25in;height:0.4166in\">", html, StringComparison.Ordinal);
        Assert.Equal(result.Images.Select(i => i.ContentId), MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions { InlineImageIds = result.Images.Select(i => i.ContentId).ToHashSet() }).ImageIds);

        // Metadata stripped from the pasted files.
        Assert.Equal(MessageImageSamples.Png(12, 4, seed: 0x02), result.Images[0].Data);
        Assert.Equal(MessageImageSamples.Jpeg(20, 10), result.Images[1].Data);
    }

    [Fact]
    public async Task OutlookClassic_TextSuggestion()
    {
        var result = await ImportAsync("outlook-classic");
        Assert.Equal(
            """
            Szanowni Państwo,

            hasło do konta {sAMAccountName} wygaśnie {$passwordExpires:d MMMM yyyy} (za {$passwordExpiresInDays} dni).

            Dzień dobry {givenName}, prosimy o zmianę hasła już dziś.

            - Zmień hasło na stronie haslo.contoso.pl.
              - Hasło musi mieć co najmniej 14 znaków (ą, ę, ł też się liczą).
              - Nie używaj imienia ani nazwiska.
            - W razie pytań napisz na pomoc@contoso.pl.

            1. Naciśnij Ctrl+Alt+Del.
            2. Wybierz „Zmień hasło”.

            Dział→Telefon
            Pomoc techniczna – Łódź→+48 42 123 45 67

            [Logo – Dział IT]

            [Okno zmiany hasła]

            Pozdrawiamy
            Zespół Wsparcia IT – Żółć i Gęś sp. z o.o.
            """.ReplaceLineEndings("\n"),
            result.SuggestedText.Replace('\t', '→'));
    }

    [Fact]
    public async Task Word365_HiddenTextListsAndEncodedFieldLinks()
    {
        var result = await ImportAsync("word-365");
        Assert.DoesNotContain("Notatka ukryta", result.Html, StringComparison.Ordinal);
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.RemovedElement && p.Detail == "hidden");
        Assert.Contains("<a href=\"https://portal.contoso.pl/{sAMAccountName}\">", result.Html, StringComparison.Ordinal);
        Assert.Contains("<ol style=\"margin-top:0;margin-bottom:0;list-style-type:lower-alpha\">", result.Html, StringComparison.Ordinal);
        Assert.Contains("<u>{$passwordExpires:d MMMM yyyy}</u>", result.Html, StringComparison.Ordinal);
        Assert.Contains("Dzień dobry {givenName},", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewOutlook_BlobAndRemoteImages_AreNeverFetched()
    {
        var files = new FakePastedImageSource();
        var result = await ImportAsync("outlook-new", files);
        Assert.Empty(result.Images);
        Assert.Empty(files.Requests);
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.ImageUnreadable && p.Detail == "blob");
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.RemoteImageRemoved && p.Detail == "attachments.office.net");
        Assert.Contains("font-family:Aptos,Aptos_EmbeddedFont,Aptos_MSFontService,Calibri,Helvetica,sans-serif", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browser_DataUriImageEmbedded_SvgRefused_RelativeLinkResolved()
    {
        var result = await ImportAsync("chrome-edge");
        var image = Assert.Single(result.Images);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("image1.png", image.FileName);
        Assert.Contains("href=\"https://intranet.contoso.pl/pomoc/haslo.html#krok-1\"", result.Html, StringComparison.Ordinal);
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.ImageRejected && p.Detail == "image/svg+xml");
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.LinkRemoved && p.Detail == "#");
        Assert.Contains("przejdź na dół strony", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Firefox_LinkOutsideTheDomainsAndTextMismatch()
    {
        var result = await ImportAsync("firefox");
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.LinkOutsideAllowedDomains && p.Detail == "evil.example" && p.Severity == MessageIssueSeverity.Warning);
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.LinkTextMismatch && p.Detail == "evil.example");
        Assert.DoesNotContain(result.Problems, p => p.Kind == HtmlImportProblemKind.LinkOutsideAllowedDomains && p.Detail == "intranet.contoso.pl");
        Assert.Contains("<pre>  wcięty tekst\n    zachowany</pre>", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UncImages_NeverReachTheReader()
    {
        var files = new FakePastedImageSource();
        var result = await ImportAsync("malicious-unc-image", files);
        Assert.All(files.Requests, r => Assert.DoesNotContain("attacker", r, StringComparison.OrdinalIgnoreCase));
        Assert.Single(files.Requests);
        Assert.Equal("clip_image005.png", Assert.Single(result.Images).FileName);
        Assert.Equal(3, result.Problems.Count(p => p.Kind == HtmlImportProblemKind.ImageUnreadable && p.Detail == "network"));
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.RemovedAttribute && p.Detail == "background" && p.Severity == MessageIssueSeverity.Warning);
    }

    [Fact]
    public async Task MaliciousSamples_ReportTheirFindings()
    {
        var script = await ImportAsync("malicious-script");
        Assert.Contains(script.Problems, p => p.Kind == HtmlImportProblemKind.RemovedElement && p.Detail == "script" && p.Severity == MessageIssueSeverity.Warning);
        Assert.Equal(5, script.Problems.Count(p => p.Kind == HtmlImportProblemKind.LinkRemoved && p.Detail == "javascript"));
        Assert.Contains("Tekst po – koniec.", script.Html, StringComparison.Ordinal);

        var css = await ImportAsync("malicious-css");
        Assert.Contains(css.Problems, p => p.Kind == HtmlImportProblemKind.RemovedStyle && p.Detail == "position" && p.Severity == MessageIssueSeverity.Warning);
        Assert.Contains(css.Problems, p => p.Kind == HtmlImportProblemKind.RemovedStyle && p.Detail == "margin-top");
        Assert.DoesNotContain("ukryty", css.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("ukryte", css.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleWebImage_UsesTheClipboardBitmap()
    {
        const string html = "<html><body><!--StartFragment--><p>Zrzut: <img src=\"https://intranet.contoso.pl/zrzut.png\" alt=\"Zrzut\"></p><!--EndFragment--></body></html>";
        var bitmap = MessageImageSamples.Png(6, 6);
        var result = await PastedHtmlImporter.ImportAsync(
            new HtmlImportRequest { Document = CfHtmlParser.Parse(CfHtmlFixture.Build(html)), ClipboardBitmap = bitmap },
            TestContext.Current.CancellationToken);
        var image = Assert.Single(result.Images);
        Assert.Equal(bitmap, image.Data);
        Assert.Contains($"<img src=\"cid:{image.ContentId}\" alt=\"Zrzut\">", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImageLimits_ArePerMessageAndPerTemplate()
    {
        var images = string.Concat(Enumerable.Range(1, 12).Select(i => $"<img src=\"{MessageImageSamples.DataUri(MessageImageSamples.Png(2, 2, seed: (byte)i), "image/png")}\">"));
        var html = $"<html><body><!--StartFragment--><p>{images}</p><!--EndFragment--></body></html>";
        var result = await PastedHtmlImporter.ImportAsync(new HtmlImportRequest { Document = CfHtmlParser.Parse(CfHtmlFixture.Build(html)) }, TestContext.Current.CancellationToken);
        Assert.Equal(MessageImageLimits.MaxImagesPerMessage, result.Images.Count);
        Assert.Equal(2, result.Problems.Count(p => p.Kind == HtmlImportProblemKind.ImageRejected && p.Detail == "limit"));

        // The other language already holds 15 images: only 5 more fit the template.
        var existing = Enumerable.Range(100, 15).Select(i => MessageImage.Create(MessageImageSamples.Png(3, 3, seed: (byte)i))).ToList();
        var limited = await PastedHtmlImporter.ImportAsync(
            new HtmlImportRequest { Document = CfHtmlParser.Parse(CfHtmlFixture.Build(html)), ExistingImages = existing },
            TestContext.Current.CancellationToken);
        Assert.Equal(5, limited.Images.Count);
    }

    [Fact]
    public async Task TheSameImageTwice_IsOneImage()
    {
        var uri = MessageImageSamples.DataUri(MessageImageSamples.Gif1x1, "image/gif");
        var html = $"<html><body><!--StartFragment--><p><img src=\"{uri}\" alt=\"a\"><img src=\"{uri}\" alt=\"b\"></p><!--EndFragment--></body></html>";
        var result = await PastedHtmlImporter.ImportAsync(new HtmlImportRequest { Document = CfHtmlParser.Parse(CfHtmlFixture.Build(html)) }, TestContext.Current.CancellationToken);
        var image = Assert.Single(result.Images);
        Assert.Equal(2, result.Html.Split($"cid:{image.ContentId}").Length - 1);
    }

    [Fact]
    public async Task DataUris_AreCheckedBeforeDecoding()
    {
        var huge = "data:image/png;base64," + new string('A', 2_000_000);
        var html = $"<html><body><!--StartFragment--><p><img src=\"{huge}\"><img src=\"data:image/png;base64,***\"><img src=\"data:image/png,raw\"></p><!--EndFragment--></body></html>";
        var result = await PastedHtmlImporter.ImportAsync(new HtmlImportRequest { Document = CfHtmlParser.Parse(CfHtmlFixture.Build(html)) }, TestContext.Current.CancellationToken);
        Assert.Empty(result.Images);
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.ImageRejected && p.Detail == nameof(MessageImageError.TooLarge));
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.ImageRejected && p.Detail == nameof(MessageImageError.Malformed));
    }

    [Fact]
    public async Task ContextFonts_GoToAWrapper()
    {
        const string html = "<html><head><style>body{font-family:Calibri;font-size:11pt}</style></head><body><div style='color:#333'><!--StartFragment--><b>tekst</b> i więcej<!--EndFragment--></div></body></html>";
        var result = await PastedHtmlImporter.ImportAsync(new HtmlImportRequest { Document = CfHtmlParser.Parse(CfHtmlFixture.Build(html)) }, TestContext.Current.CancellationToken);
        Assert.Equal("<div style=\"font-size:11pt;font-family:Calibri;color:#333\"><b>tekst</b> i więcej</div>\n", result.Html);
    }

    [Fact]
    public async Task ThePartialAncestorsOfAFragment_AreKept()
    {
        // Copying from the middle of a list: the fragment starts inside the first item.
        const string html = "<html><body><ul><li>pierwszy<!--StartFragment--> koniec</li><li>drugi</li></ul><p>po<!--EndFragment--> reszta</p></body></html>";
        var result = await PastedHtmlImporter.ImportAsync(new HtmlImportRequest { Document = CfHtmlParser.Parse(CfHtmlFixture.Build(html)) }, TestContext.Current.CancellationToken);
        Assert.Equal("<ul><li> koniec</li>\n<li>drugi</li>\n</ul>\n<p>po</p>\n", result.Html);
    }

    [Fact]
    public void ContentNestedTooDeeply_IsNotImported_WithoutAWalkOverIt()
    {
        var html = "<html><body><!--StartFragment-->" + SmallStack.Nested(5_000) + "<!--EndFragment--></body></html>";
        var request = new HtmlImportRequest { Document = CfHtmlParser.Parse(CfHtmlFixture.Build(html)) };
        var result = SmallStack.Run(() => PastedHtmlImporter.ImportAsync(request, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(string.Empty, result.Html);
        Assert.Equal(string.Empty, result.SuggestedText);
        Assert.Contains(result.Problems, p => p.Kind == HtmlImportProblemKind.TooDeep && p.Severity == MessageIssueSeverity.Error && p.Detail == "256");
    }

    [Theory]
    [InlineData("{givenName}", true)]
    [InlineData("{$passwordExpires:d MMMM yyyy}", true)]
    [InlineData("{sAMAccountName|lower}", true)]
    [InlineData("{ to jest nawias }", false)]
    [InlineData("{$nieznany}", false)]
    [InlineData("{}", false)]
    [InlineData("tekst", false)]
    public void Fields_AreRecognizedByTheEngine(string candidate, bool expected) =>
        Assert.Equal(expected, PastedHtmlImporter.IsField(candidate, Fields.Engine));

    [Fact]
    public void ResultAndRead_ShowNoContent()
    {
        Assert.Equal("HtmlImportResult(10 characters, 0 images, 0 problems)", new HtmlImportResult("<p>abc</p>", [], [], "abc").ToString());
        Assert.Equal("PastedImageRead(3 B)", new PastedImageRead([1, 2, 3], "C:\\Users\\jkowalski\\a.png").ToString());
    }
}
