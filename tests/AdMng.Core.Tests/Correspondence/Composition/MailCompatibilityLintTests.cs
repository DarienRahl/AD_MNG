using AdMng.Core.Correspondence.Composition;
using static AdMng.Core.Tests.Correspondence.Composition.CompositionTestData;

namespace AdMng.Core.Tests.Correspondence.Composition;

public class MailCompatibilityLintTests
{
    private static IReadOnlyList<MailLintKind> Kinds(string html, long bytes = 0) =>
        [.. MailCompatibilityLint.Check(html, bytes).Select(f => f.Kind)];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<p>Zwykły tekst, <b>pogrubiony</b>, <a href=\"https://gmina.pl\">gmina.pl</a>.</p>")]
    [InlineData("<p>Tekst o position:absolute i background-image: url(x) i width:900px nie jest stylem.</p>")]
    [InlineData("<table width=\"100%\" style=\"max-width:900px;width:100%\"><tr><td width=\"700\">x</td></tr></table>")]
    [InlineData("<p>&lt;script&gt; to tekst</p>")]
    public void CleanHtml_HasNoFindings(string? html) => Assert.Empty(MailCompatibilityLint.Check(html));

    [Fact]
    public void EveryKind_InEnumOrder()
    {
        var html = "<html><head><style>p{color:red}</style></head><body>"
            + "<p style=\"background:#fff url(https://x.pl/t.png)\">a</p>"
            + "<img src=\"data:image/png;base64,AAAA\"><img src=\"https://cdn.example.org/a.png\">"
            + "<div style=\"position: Fixed; top:0\">b</div><table width=\"800\"><tr><td>c</td></tr></table>"
            + "<a href=\"https://evil.example/\">bank.pl</a><script>x()</script></body></html>";
        var findings = MailCompatibilityLint.Check(html, CorrespondenceLimits.MessageWarningBytes + 1);
        Assert.Equal(
            [MailLintKind.Script, MailLintKind.RemoteImage, MailLintKind.DataImage, MailLintKind.AbsolutePosition, MailLintKind.BackgroundImage,
             MailLintKind.TooWide, MailLintKind.StyleBlock, MailLintKind.LinkTextMismatch, MailLintKind.LargeMessage],
            findings.Select(f => f.Kind));
        Assert.Equal("800", findings.Single(f => f.Kind == MailLintKind.TooWide).Detail);
        Assert.Equal("evil.example", findings.Single(f => f.Kind == MailLintKind.LinkTextMismatch).Detail);
        Assert.Equal((CorrespondenceLimits.MessageWarningBytes + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            findings.Single(f => f.Kind == MailLintKind.LargeMessage).Detail);
    }

    [Theory]
    [InlineData("<img width=\"701\" src=\"cid:x\">", "701")]
    [InlineData("<div style=\"width:900px\">x</div>", "900")]
    [InlineData("<div style=\"min-width: 750px\">x</div>", "750")]
    [InlineData("<div style=\"width:600pt\">x</div>", "800")]
    [InlineData("<table><tr><td width=\"1200px\">x</td></tr></table>", "1200")]
    public void TooWide_ReportsPixels(string html, string pixels)
    {
        var finding = Assert.Single(MailCompatibilityLint.Check(html));
        Assert.Equal((MailLintKind.TooWide, pixels), (finding.Kind, finding.Detail));
    }

    [Theory]
    [InlineData("<div style=\"width:700px\">x</div>")]
    [InlineData("<div style=\"width:50em\">x</div>")]
    [InlineData("<div style=\"max-width:2000px\">x</div>")]
    [InlineData("<img width=\"auto\">")]
    public void NotTooWide(string html) => Assert.DoesNotContain(MailLintKind.TooWide, Kinds(html));

    [Fact]
    public void BackgroundAttribute() => Assert.Equal([MailLintKind.BackgroundImage], Kinds("<table background=\"tlo.png\"><tr><td>x</td></tr></table>"));

    [Fact]
    public void ProtocolRelativeImage_IsRemote() => Assert.Equal([MailLintKind.RemoteImage], Kinds("<img src=\"//cdn.example.org/a.png\">"));

    [Fact]
    public void LinkMismatch_OncePerHost()
    {
        var findings = MailCompatibilityLint.Check("<a href=\"https://a.example/1\">bank.pl</a> <a href=\"https://a.example/2\">bank.pl</a> <a href=\"https://b.example/\">www.bank.pl</a>");
        Assert.Equal(["a.example", "b.example"], findings.Select(f => f.Detail));
    }

    [Fact]
    public void LargeMessage_OnlyAboveTheLimit()
    {
        Assert.Empty(MailCompatibilityLint.Check("<p>x</p>", CorrespondenceLimits.MessageWarningBytes));
        Assert.Equal([MailLintKind.LargeMessage], Kinds(null!, CorrespondenceLimits.MessageWarningBytes + 1));
    }

    [Fact]
    public void RenderedMessages_HaveNoFindings()
    {
        var image = Image(0x60);
        var rendered = CorrespondenceRenderer.Render(new CorrespondenceDraft
        {
            BodyHtml = $"<p style=\"position:absolute\">{{{{nazwa}}}}<script>x</script><img src=\"cid:{image.ContentId}\" width=\"200\"></p>",
            Images = ById(image),
        }, Unit(), Columns);
        Assert.Empty(MailCompatibilityLint.Check(rendered.HtmlDocument, rendered.ImageBytes));
    }
}
