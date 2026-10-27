using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Testing.RichHtml;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>The allow-lists of section 4.3, both modes, Validate = the security subset, idempotence, stability and the
/// cheap first gate over every fixture.</summary>
public class MessageHtmlSanitizerTests
{
    private const string Image = "img-0123456789abcdef@admng";

    private static readonly HtmlSanitizeOptions Fragment = new() { InlineImageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Image } };

    private static string Clean(string html) => MessageHtmlSanitizer.Sanitize(html, Fragment).Html.TrimEnd('\n');

    private static IReadOnlyList<HtmlChange> Changes(string html) => MessageHtmlSanitizer.Sanitize(html, Fragment).Changes;

    // ---- elements -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<p>a</p>")]
    [InlineData("<div align=\"center\">a</div>")]
    [InlineData("<h1>Nagłówek</h1>")]
    [InlineData("<h6 align=\"right\">x</h6>")]
    [InlineData("<blockquote>cytat</blockquote>")]
    [InlineData("<p><b>b</b> <strong>s</strong> <i>i</i> <em>e</em> <u>u</u> <s>s</s> <strike>s</strike> <del>d</del> <ins>i</ins> <mark>m</mark> <small>s</small> <big>b</big> <sub>2</sub> <sup>3</sup> <code>c</code> <kbd>k</kbd> <samp>s</samp> <tt>t</tt> <cite>c</cite> <dfn>d</dfn> <abbr title=\"skrót\">a</abbr> <q>q</q> <bdi>b</bdi> <bdo dir=\"rtl\">b</bdo></p>")]
    [InlineData("<p>a<br>b<wbr>c</p>")]
    [InlineData("<hr>")]
    [InlineData("<pre>  kod\n  wcięty</pre>")]
    [InlineData("<center>środek</center>")]
    [InlineData("<p><font face=\"Arial, sans-serif\" size=\"3\" color=\"#FF0000\">f</font></p>")]
    [InlineData("<dl><dt>t</dt>\n<dd>d</dd>\n</dl>")]
    public void KeptElements_StayAsTheyAre(string html) => Assert.Equal(html, Clean(html));

    [Fact]
    public void Lists_KeepTheirAttributes()
    {
        Assert.Equal("<ol start=\"3\" type=\"a\" reversed><li value=\"5\">x</li>\n</ol>", Clean("<ol start=3 type=a reversed><li value=5>x</li></ol>"));
        Assert.Equal("<ul type=\"square\"><li type=\"circle\">x</li>\n</ul>", Clean("<ul type=square><li type=circle>x</li></ul>"));
    }

    [Fact]
    public void Tables_KeepTheirAttributes()
    {
        const string html = "<table border=\"1\" cellpadding=\"2\" cellspacing=\"0\" width=\"100%\" align=\"center\" bgcolor=\"#EEEEEE\"><caption align=\"top\">Tytuł</caption>\n"
            + "<colgroup span=\"2\" width=\"50\"><col span=\"1\" width=\"20\">\n</colgroup>\n"
            + "<thead align=\"left\" valign=\"top\" bgcolor=\"white\"><tr><th colspan=\"2\" scope=\"col\">Nagłówek</th>\n</tr>\n</thead>\n"
            + "<tbody><tr align=\"right\" valign=\"middle\" bgcolor=\"#FFF\"><td colspan=\"1\" rowspan=\"2\" width=\"30\" height=\"10\" align=\"center\" valign=\"top\" bgcolor=\"yellow\" nowrap>ą</td>\n</tr>\n</tbody>\n"
            + "<tfoot><tr><td>stopka</td>\n</tr>\n</tfoot>\n</table>";
        Assert.Equal(html, Clean(html));
    }

    [Theory]
    [InlineData("script", true)]
    [InlineData("iframe", true)]
    [InlineData("object", true)]
    [InlineData("applet", true)]
    [InlineData("form", true)]
    [InlineData("button", true)]
    [InlineData("select", true)]
    [InlineData("textarea", true)]
    [InlineData("audio", true)]
    [InlineData("video", true)]
    [InlineData("canvas", true)]
    [InlineData("template", true)]
    [InlineData("noscript", false)]
    [InlineData("title", false)]
    [InlineData("xmp", false)]
    public void RemovedElements_GoWithTheirContent(string element, bool security)
    {
        var html = $"<p>przed</p><{element}>treść <b>pogrubiona</b></{element}><p>po</p>";
        var result = MessageHtmlSanitizer.Sanitize(html, Fragment);
        Assert.DoesNotContain("treść", result.Html, StringComparison.Ordinal);
        Assert.Contains("przed", result.Html, StringComparison.Ordinal);
        Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.RemovedElement && c.Detail == element && c.IsSecurity == security);
    }

    [Theory]
    [InlineData("<embed src=\"https://evil.example/x.swf\">", "embed")]
    [InlineData("<svg><text>s</text><script>alert(1)</script></svg>", "svg")]
    [InlineData("<math><mi>x</mi></math>", "math")]
    public void ForeignContentAndEmbeds_AreRemoved(string html, string name)
    {
        var result = MessageHtmlSanitizer.Sanitize("<p>a</p>" + html, Fragment);
        Assert.Equal("<p>a</p>", result.Html.TrimEnd('\n'));
        Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.RemovedElement && c.Detail == name && c.IsSecurity);
    }

    [Fact]
    public void UnknownElements_AreUnwrapped()
    {
        Assert.Equal("<p>a</p>\n<p>b</p>", Clean("<section><p>a</p><article><p>b</p></article></section>"));
        Assert.Equal("<p>x y</p>", Clean("<p><label>x</label> <custom-tag>y</custom-tag></p>"));
        Assert.Contains(Changes("<section>a</section>"), c => c.Kind == HtmlChangeKind.UnwrappedElement && c.Detail == "section" && !c.IsSecurity);
    }

    [Fact]
    public void HandlersOnUnwrappedElements_AreStillFindings() =>
        Assert.Contains(Changes("<details ontoggle=alert(1)>x</details>"), c => c.Kind == HtmlChangeKind.RemovedAttribute && c.Detail == "ontoggle" && c.IsSecurity);

    [Fact]
    public void Comments_AreRemoved() => Assert.Equal("<p>ab</p>", Clean("<p>a<!-- komentarz -->b</p><!--[if mso]><p>x</p><![endif]-->"));

    // ---- attributes ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Attributes_OutsideTheAllowList_AreDropped()
    {
        var result = MessageHtmlSanitizer.Sanitize("<p class=\"MsoNormal\" id=\"x\" name=\"n\" data-x=\"1\" target=\"_blank\" onclick=\"alert(1)\" title=\"t\" lang=\"pl\" dir=\"ltr\">a</p>", Fragment);
        Assert.Equal("<p title=\"t\" lang=\"pl\" dir=\"ltr\">a</p>", result.Html.TrimEnd('\n'));
        Assert.Contains(result.Changes, c => c.Detail == "onclick" && c.IsSecurity);
        Assert.Contains(result.Changes, c => c.Detail == "class" && !c.IsSecurity);
    }

    [Theory]
    [InlineData("<table><tr><td background=\"https://tracker.example/t.gif\">x</td></tr></table>", "background")]
    [InlineData("<img src=\"cid:img-0123456789abcdef@admng\" srcset=\"https://a/b.png 2x\">", "srcset")]
    [InlineData("<table background=\"\\\\server\\share\\a.png\"><tr><td>x</td></tr></table>", "background")]
    public void UrlAttributes_AreSecurityFindings(string html, string attribute) =>
        Assert.Contains(Changes(html), c => c.Kind == HtmlChangeKind.RemovedAttribute && c.Detail == attribute && c.IsSecurity);

    [Theory]
    [InlineData("<table><tr><td width=\"abc\">x</td></tr></table>", "width")]
    [InlineData("<table><tr><td colspan=\"123456\">x</td></tr></table>", "colspan")]
    [InlineData("<table><tr><td bgcolor=\"url(x)\">x</td></tr></table>", "bgcolor")]
    [InlineData("<p align=\"middle\">x</p>", "align")]
    [InlineData("<font size=\"9\">x</font>", "size")]
    [InlineData("<p lang=\"pl pl\">x</p>", "lang")]
    [InlineData("<p title=\"javascript:alert(1)\">x</p>", "title")]
    [InlineData("<img src=\"cid:img-0123456789abcdef@admng\" alt=\"&lt;script&gt;\">", "alt")]
    public void InvalidValues_DropTheAttribute(string html, string attribute) =>
        Assert.Contains(Changes(html), c => c.Kind == HtmlChangeKind.RemovedAttribute && c.Detail == attribute);

    // ---- CSS ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("color:#C00000", "color:#C00000")]
    [InlineData("color:RGB(1, 2, 3)", "color:rgb(1,2,3)")]
    [InlineData("color:rgba(1,2,3,.5)", "color:rgba(1,2,3,0.5)")]
    [InlineData("color:windowtext", "color:#000000")]
    [InlineData("background-color:window", "background-color:#FFFFFF")]
    [InlineData("color:Red", "color:red")]
    [InlineData("font-size:12.0pt", "font-size:12pt")]
    [InlineData("font-family:\"Aptos\",sans-serif", "font-family:'Aptos',sans-serif")]
    [InlineData("font-family:Segoe UI, Arial", "font-family:Segoe UI,Arial")]
    [InlineData("font:bold 10pt/1.2 Arial", "font:bold 10pt/1.2 Arial")]
    [InlineData("line-height:115%", "line-height:115%")]
    [InlineData("line-height:1.5", "line-height:1.5")]
    [InlineData("text-indent:-18.0pt", "text-indent:-18pt")]
    [InlineData("letter-spacing:-.5pt", "letter-spacing:-0.5pt")]
    [InlineData("margin:0cm 0cm 8.0pt", "margin:0cm 0cm 8pt")]
    [InlineData("margin:0 auto", "margin:0 auto")]
    [InlineData("padding:0cm 5.4pt 0cm 5.4pt", "padding:0cm 5.4pt 0cm 5.4pt")]
    [InlineData("border:solid windowtext 1.0pt", "border:solid #000000 1pt")]
    [InlineData("border-top:none", "border-top:none")]
    [InlineData("border-collapse:collapse", "border-collapse:collapse")]
    [InlineData("width:113.25pt", "width:113.25pt")]
    [InlineData("height:.4166in", "height:0.4166in")]
    [InlineData("text-decoration:underline", "text-decoration:underline")]
    [InlineData("vertical-align:middle", "vertical-align:middle")]
    [InlineData("white-space:nowrap", "white-space:nowrap")]
    [InlineData("list-style-type:lower-alpha", "list-style-type:lower-alpha")]
    [InlineData("word-wrap:break-word", "word-wrap:break-word")]
    [InlineData("color:red !important", "color:red")]
    [InlineData("mso-highlight:yellow", "background-color:yellow")]
    [InlineData("background:#D9F2D0", "background-color:#D9F2D0")]
    public void AllowedDeclarations_AreNormalized(string style, string expected) =>
        Assert.Equal($"<span style=\"{expected}\">x</span>", Clean($"<span style='{style}'>x</span>"));

    [Theory]
    [InlineData("mso-ligatures:standardcontextual", false)]
    [InlineData("-webkit-text-stroke-width:0px", false)]
    [InlineData("float:left", false)]
    [InlineData("position:relative", false)]
    [InlineData("z-index:5", false)]
    [InlineData("max-height:10px", false)]
    [InlineData("overflow:hidden", false)]
    [InlineData("cursor:pointer", false)]
    [InlineData("color:var(--x)", false)]
    [InlineData("width:calc(100% - 2px)", false)]
    [InlineData("color:\\72 ed", false)]
    [InlineData("color:red/*x*/", false)]
    [InlineData("color:auto", false)]
    [InlineData("position:fixed", true)]
    [InlineData("position:absolute", true)]
    [InlineData("visibility:hidden", true)]
    [InlineData("opacity:0", true)]
    [InlineData("transform:scale(0)", true)]
    [InlineData("clip-path:inset(50%)", true)]
    [InlineData("content:\"x\"", true)]
    [InlineData("background-image:url(https://tracker.example/a.gif)", true)]
    [InlineData("background:url(x.gif)", true)]
    [InlineData("width:expression(alert(1))", true)]
    [InlineData("behavior:url(x.htc)", true)]
    [InlineData("-moz-binding:url(x.xml)", true)]
    [InlineData("margin-top:-500px", true)]
    [InlineData("margin:0 0 0 -10px", true)]
    [InlineData("font-size:0", true)]
    public void DroppedDeclarations(string style, bool security)
    {
        var result = MessageHtmlSanitizer.Sanitize($"<p style='{style}'>x</p>", Fragment);
        Assert.Equal("<p>x</p>", result.Html.TrimEnd('\n'));
        Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.RemovedStyle && c.IsSecurity == security);
    }

    [Theory]
    [InlineData("text-indent:-9999px", "text-indent:-1in")]
    [InlineData("text-indent:-500em", "text-indent:-1in")]
    [InlineData("text-indent:-100%", "text-indent:-1in")]
    [InlineData("letter-spacing:-100px", "letter-spacing:-2px")]
    [InlineData("letter-spacing:-1em", "letter-spacing:-2px")]
    public void TextMovedOrSqueezedOutOfSight_IsClamped(string style, string expected)
    {
        var result = MessageHtmlSanitizer.Sanitize($"<p style='{style}'>Zażółć gęślą jaźń</p>", Fragment);
        Assert.Equal($"<p style=\"{expected}\">Zażółć gęślą jaźń</p>", result.Html.TrimEnd('\n'));
        Assert.DoesNotContain(result.Changes, c => c.IsSecurity);
    }

    [Theory]
    [InlineData("<span style='font-size:0.1px'>ukryte</span>")]
    [InlineData("<span style='font-size:2pt'>ukryte</span>")]
    [InlineData("<span style='font-size:0.1em'>ukryte</span>")]
    [InlineData("<span style='font:1px Arial'>ukryte</span>")]
    [InlineData("<div style='font-size:1px;line-height:0'><span>ukryte</span></div>")]
    public void TextTooSmallToRead_IsASecurityFinding(string html)
    {
        var result = MessageHtmlSanitizer.Sanitize("<p>widoczne " + html + "</p>", Fragment);
        Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.RemovedStyle && c.Detail == "font-size" && c.IsSecurity);
        Assert.Contains(MessageHtmlSanitizer.Validate("<p>widoczne " + html + "</p>", Fragment), f => f.Kind == HtmlChangeKind.RemovedStyle);
    }

    [Theory]
    [InlineData("<p style=\"font-size:1pt\">&nbsp;</p>")]
    [InlineData("<div style=\"font-size:1pt\"><p style=\"font-size:11pt\">Tekst</p>\n</div>")]
    [InlineData("<span style=\"font-size:xx-small\">mały</span>")]
    public void SmallFontsWithoutHiddenText_Stay(string html)
    {
        var result = MessageHtmlSanitizer.Sanitize(html, Fragment);
        Assert.Equal(html, result.Html.TrimEnd('\n'));
        Assert.DoesNotContain(result.Changes, c => c.IsSecurity);
    }

    [Theory]
    [InlineData("<p style='display:none'>ukryte</p>")]
    [InlineData("<p style='mso-hide:all'>ukryte</p>")]
    [InlineData("<style>.u{display:none}</style><p class=u>ukryte</p>")]
    [InlineData("<div style='DISPLAY: NONE !important'><p>ukryte</p></div>")]
    public void HiddenContent_IsRemovedWithItsText(string html)
    {
        var result = MessageHtmlSanitizer.Sanitize("<p>widoczne</p>" + html, Fragment);
        Assert.Equal("<p>widoczne</p>", result.Html.TrimEnd('\n'));
        Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.HiddenContent && c.IsSecurity);
    }

    [Fact]
    public void StyleElements_AreInlinedThenRemoved_DangerousOnesAreFindings()
    {
        var result = MessageHtmlSanitizer.Sanitize("<style>p{color:red}</style><p>a</p>", Fragment);
        Assert.Equal("<p style=\"color:red\">a</p>", result.Html.TrimEnd('\n'));
        Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.RemovedElement && c.Detail == "style" && !c.IsSecurity);
        Assert.Contains(Changes("<style>@import url(https://evil.example/x.css);</style><p>a</p>"), c => c.Detail == "style" && c.IsSecurity);
    }

    // ---- URLs ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://haslo.contoso.pl/zmiana?u=1&x=2")]
    [InlineData("http://intranet.contoso.pl")]
    [InlineData("mailto:pomoc@contoso.pl?subject=Hasło")]
    [InlineData("tel:+48421234567")]
    public void AllowedLinks_AreKept(string href)
    {
        var result = MessageHtmlSanitizer.Sanitize($"<a href=\"{System.Net.WebUtility.HtmlEncode(href)}\">x</a>", Fragment);
        Assert.Equal([href], result.Links);
    }

    [Theory]
    [InlineData("javascript:alert(1)", "javascript", true)]
    [InlineData("jav&#x09;ascript:alert(1)", "javascript", true)]
    [InlineData("&#106;avascript:alert(1)", "javascript", true)]
    [InlineData(" &#14;javascript:alert(1)", "javascript", true)]
    [InlineData("JAVASCRIPT:alert(1)", "javascript", true)]
    [InlineData("vbscript:msgbox(1)", "vbscript", true)]
    [InlineData("data:text/html,x", "data", true)]
    [InlineData("file:///C:/x.html", "file", true)]
    [InlineData("blob:https://a/b", "blob", true)]
    [InlineData("ftp://ftp.contoso.pl/a", "ftp", false)]
    [InlineData("cid:abc", "cid", false)]
    [InlineData("ms-word:ofe|u|x", "ms-word", false)]
    [InlineData("onenote:x", "onenote", false)]
    [InlineData("#kotwica", "#", false)]
    [InlineData("/pomoc/haslo.html", "relative", false)]
    [InlineData("//evil.example/x", "relative", false)]
    [InlineData("https://", "https", false)]
    public void OtherLinks_AreUnwrapped_TextKept(string href, string detail, bool security)
    {
        var result = MessageHtmlSanitizer.Sanitize($"<p><a href=\"{href}\">tekst łącza</a></p>", Fragment);
        Assert.Equal("<p>tekst łącza</p>", result.Html.TrimEnd('\n'));
        Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.LinkRemoved && c.Detail == detail && c.IsSecurity == security);
    }

    [Fact]
    public void FieldLinks_OnlyInTemplateMode()
    {
        var template = MessageHtmlSanitizer.Sanitize("<a href=\"{$input:link}\">x</a>", Fragment with { AllowFieldUrls = true });
        Assert.Equal("<a href=\"{$input:link}\">x</a>", template.Html);
        Assert.Equal("x", MessageHtmlSanitizer.Sanitize("<a href=\"{$input:link}\">x</a>", Fragment).Html);
    }

    [Theory]
    [InlineData("https://tracker.example/pixel.gif", HtmlChangeKind.RemoteImage, "tracker.example")]
    [InlineData("cid:img-ffffffffffffffff@admng", HtmlChangeKind.MissingImage, "img-ffffffffffffffff@admng")]
    [InlineData("{$input:obraz}", HtmlChangeKind.FieldInImageSource, null)]
    [InlineData("data:image/png;base64,AAAA", HtmlChangeKind.ImageRejected, "data")]
    [InlineData("file:///C:/a.png", HtmlChangeKind.ImageRejected, "file")]
    [InlineData("obraz.png", HtmlChangeKind.ImageRejected, "relative")]
    public void ImageSources_OtherThanKnownCids_AreSecurityFindings(string src, HtmlChangeKind kind, string? detail)
    {
        foreach (var options in new[] { Fragment, Fragment with { AllowFieldUrls = true } })
        {
            var result = MessageHtmlSanitizer.Sanitize($"<p>a<img src=\"{src}\" alt=\"x\">b</p>", options);
            Assert.Equal("<p>ab</p>", result.Html.TrimEnd('\n'));
            Assert.Contains(result.Changes, c => c.Kind == kind && c.Detail == detail && c.IsSecurity);
        }
    }

    [Fact]
    public void KnownCidImages_AreKept_AndReported()
    {
        var result = MessageHtmlSanitizer.Sanitize("<p><img src=\"CID:IMG-0123456789ABCDEF@ADMNG\" alt=\"Logo\" width=\"120\" height=\"40\" style=\"width:1.25in\"></p>", Fragment);
        Assert.Equal($"<p><img src=\"cid:{Image}\" alt=\"Logo\" width=\"120\" height=\"40\" style=\"width:1.25in\"></p>", result.Html.TrimEnd('\n'));
        Assert.Equal([Image], result.ImageIds);
    }

    // ---- cleanup and serialization --------------------------------------------------------------------------------------

    [Fact]
    public void Cleanup_UnwrapsMergesAndDropsEmptyInlineElements()
    {
        Assert.Equal("<p>a b <b><span style=\"color:red\">{sAMAccountName}</span></b></p>",
            Clean("<p><span>a</span> <font>b</font> <b><span style='color:red'>{</span></b><b><span class=SpellE><span style='color:red'>sAMAccountName</span></span></b><b><span style='color:red'>}</span></b><i></i><a name=_Hlk1></a></p>"));
    }

    [Fact]
    public void Text_IsEscapedMinimally_PolishLettersStayLiteral() =>
        Assert.Equal("<p>&lt;b&gt; &amp; „cytat” zażółć&nbsp;gęślą</p>", Clean("<p>&lt;b&gt; &amp; „cytat” zażółć&nbsp;gęślą</p>"));

    [Fact]
    public void Fields_AreNeverAltered()
    {
        const string html = "<p>{givenName|lower} {$passwordExpires:d MMMM yyyy}  {$input:termin} {{dosłownie}}</p>";
        Assert.Equal(html, Clean(html));
    }

    [Fact]
    public void DocumentMode_KeepsHtmlHeadAndBody()
    {
        var footer = UserMessageRules.HtmlDocument("<p>Treść {givenName}</p>", "Wiadomość automatyczna działu IT.", MessageLanguage.Polish);
        var result = MessageHtmlSanitizer.Sanitize(footer, new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Document });
        Assert.DoesNotContain(result.Changes, c => c.IsSecurity);
        Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"pl\">\n<head></head>\n<body style=\"font-size:11pt;font-family:'Segoe UI',Arial,sans-serif;color:#1b1b1b\">\n", result.Html, StringComparison.Ordinal);
        Assert.Contains("Wiadomość automatyczna działu IT.", result.Html, StringComparison.Ordinal);
        Assert.Empty(MessageHtmlSanitizer.Validate(footer, new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Document }));
    }

    [Fact]
    public void DocumentMode_EmptiesTheHead()
    {
        var result = MessageHtmlSanitizer.Sanitize("<html><head><title>T</title><meta http-equiv=refresh content=0><style>p{color:red}</style></head><body><p>a</p></body></html>",
            new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Document });
        Assert.Equal("<!DOCTYPE html>\n<html>\n<head></head>\n<body>\n<p style=\"color:red\">a</p>\n</body>\n</html>\n", result.Html);
        Assert.Contains(result.Changes, c => c.Detail == "meta" && c.IsSecurity);
        Assert.Contains(result.Changes, c => c.Detail == "title" && !c.IsSecurity);
    }

    // ---- nesting --------------------------------------------------------------------------------------------------------

    [Fact]
    public void HtmlNestedTooDeeply_IsRefusedAsAWhole_WithoutAWalkOverIt()
    {
        // 20 000 levels overflowed a thread-pool thread's stack in the recursive walks (the process ended); on this small
        // stack a few thousand would.
        var html = SmallStack.Nested(5_000);
        foreach (var mode in new[] { HtmlSanitizeMode.Fragment, HtmlSanitizeMode.Document })
        {
            var options = Fragment with { Mode = mode };
            var result = SmallStack.Run(() => MessageHtmlSanitizer.Sanitize(html, options));
            Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.TooDeep && c.IsSecurity && c.Detail == "256");
            Assert.DoesNotContain("Zażółć", result.Html, StringComparison.Ordinal);
            Assert.Contains(SmallStack.Run(() => MessageHtmlSanitizer.Validate(html, options)), f => f.Kind == HtmlChangeKind.TooDeep);
        }
    }

    [Fact]
    public void HtmlWithinTheNestingLimit_IsKept()
    {
        // html > body > 250 div: 252 levels.
        var result = MessageHtmlSanitizer.Sanitize(SmallStack.Nested(250), Fragment);
        Assert.DoesNotContain(result.Changes, c => c.Kind == HtmlChangeKind.TooDeep);
        Assert.Contains("Zażółć gęślą jaźń", result.Html, StringComparison.Ordinal);
    }

    // ---- Validate and the guarantees ------------------------------------------------------------------------------------

    [Fact]
    public void Validate_IsTheSecuritySubsetOfSanitize()
    {
        const string html = "<p class=x style='mso-x:1;position:fixed'>a<script>x</script><img src=https://t.example/p.gif><a href='javascript:x'>j</a></p><section>s</section>";
        var changes = MessageHtmlSanitizer.Sanitize(html, Fragment).Changes;
        var findings = MessageHtmlSanitizer.Validate(html, Fragment);
        Assert.Equal(changes.Where(c => c.IsSecurity).Select(c => new HtmlFinding(c.Kind, c.Detail)).Distinct(), findings);
        Assert.Contains(findings, f => f.Kind == HtmlChangeKind.RemoteImage);
        Assert.DoesNotContain(findings, f => f.Detail is "class" or "mso-x" or "section");
    }

    [Theory]
    [InlineData("<p>Termin (data: 5.11.2026)</p>")]
    [InlineData("<p>Kliknij \"Plik: Zapisz\" – File: Save</p>")]
    [InlineData("<p>Status online = tak</p>")]
    public void PolishTextThatLooksLikeMarkup_IsKept_AndPassesTheFirstGate(string html)
    {
        var result = MessageHtmlSanitizer.Sanitize(html, Fragment);
        Assert.Equal(html, result.Html.TrimEnd('\n'));
        Assert.Empty(result.Changes);
        Assert.Empty(UserMessageRules.FindForbiddenHtml(result.Html));
    }

    [Fact]
    public void ATitleWrittenAsAScriptUrl_IsDropped_SoTheOutputPassesTheFirstGate()
    {
        var result = MessageHtmlSanitizer.Sanitize("<p title=\"javascript:alert(1)\">a</p>", Fragment);
        Assert.Equal("<p>a</p>\n", result.Html);
        Assert.Empty(UserMessageRules.FindForbiddenHtml(result.Html));
    }

    [Fact]
    public void Validate_OfASanitizedResultIsEmpty()
    {
        foreach (var name in CfHtmlFixture.Names)
        {
            var sanitized = MessageHtmlSanitizer.Sanitize(CfHtmlFixture.Html(name), Fragment).Html;
            Assert.Empty(MessageHtmlSanitizer.Validate(sanitized, Fragment));
        }
    }

    [Fact]
    public void EveryFixture_IsIdempotentStableAndPassesTheFirstGate()
    {
        foreach (var name in CfHtmlFixture.Names)
        {
            foreach (var mode in new[] { HtmlSanitizeMode.Fragment, HtmlSanitizeMode.Document })
            {
                var options = Fragment with { Mode = mode };
                var once = MessageHtmlSanitizer.Sanitize(CfHtmlFixture.Html(name), options).Html;
                var twice = MessageHtmlSanitizer.Sanitize(once, options);
                Assert.Equal(once, twice.Html);
                Assert.DoesNotContain(twice.Changes, c => c.Kind != HtmlChangeKind.RemovedElement || c.Detail != "style");
                Assert.Empty(UserMessageRules.FindForbiddenHtml(once));
            }
        }
    }

    [Fact]
    public void MaliciousFixtures_LeaveNothingActive()
    {
        foreach (var name in CfHtmlFixture.Names.Where(n => n.StartsWith("malicious", StringComparison.Ordinal)))
        {
            var result = MessageHtmlSanitizer.Sanitize(CfHtmlFixture.Html(name), Fragment);
            var html = result.Html;
            foreach (var forbidden in new[] { "<script", "onerror", "onclick", "onload", "javascript:", "vbscript:", "<iframe", "<svg", "<math", "url(", "expression(", "position:fixed", "file:", "\\\\attacker" })
            {
                Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
            }

            Assert.NotEmpty(MessageHtmlSanitizer.Validate(CfHtmlFixture.Html(name), Fragment));
        }
    }

    [Fact]
    public void SerializedOutput_ParsesBackToTheSameTree()
    {
        // Stability: what the serializer writes is exactly what a re-parse serializes (no mXSS-style mutation).
        foreach (var name in CfHtmlFixture.Names)
        {
            var once = MessageHtmlSanitizer.Sanitize(CfHtmlFixture.Html(name), Fragment).Html;
            var reparsed = new AngleSharp.Html.Parser.HtmlParser().ParseDocument("<!DOCTYPE html><body>" + once);
            Assert.Equal(once, HtmlSerializer.Children(reparsed.Body!));
        }
    }
}
