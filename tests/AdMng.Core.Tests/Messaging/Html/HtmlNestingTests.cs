using System.Diagnostics;
using AdMng.Core.Messaging.Html;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>The bounded cost of the HTML check: the nesting estimate before parsing (the tree builder is quadratic in the
/// depth) and the style-rule budget of the inliner (rules × elements).</summary>
public class HtmlNestingTests
{
    private static string Repeat(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    [Theory]
    [InlineData(1_100)]
    [InlineData(80_000)]
    public void UnclosedElements_AreTooDeep_BeforeParsing(int depth) =>
        Assert.True(HtmlNesting.IsTextTooDeep(Repeat("<div>", depth)));

    [Fact]
    public void SelfClosedHtmlElements_StillNest() =>
        Assert.True(HtmlNesting.IsTextTooDeep(Repeat("<div/>", 2_000)));

    [Theory]
    [InlineData("<p>Zażółć gęślą jaźń</p>", 20_000)]
    [InlineData("<p>bez końca", 20_000)]
    [InlineData("<li>punkt", 20_000)]
    [InlineData("<tr><td><div>komórka</td>", 5_000)]
    [InlineData("<span>a</span><br><img src=\"cid:x\" alt=\"\"><hr>", 10_000)]
    [InlineData("<svg><path d=\"M0 0\"/><path d=\"M1 1\"/></svg>", 2_000)]
    [InlineData("<style>div{color:red}</style><!-- <div><div> --><textarea><div><div></textarea>", 2_000)]
    public void RealContent_IsNotTooDeep(string unit, int count) =>
        Assert.False(HtmlNesting.IsTextTooDeep("<table>" + Repeat(unit, count) + "</table>"));

    [Fact]
    public void EightyThousandLevels_AreRefusedWithoutTheQuadraticParse()
    {
        // The parse alone took about a minute; the 400 000-character limit of the service allows this much.
        var html = Repeat("<div>", 80_000);
        var watch = Stopwatch.StartNew();
        var result = SmallStack.Run(() => MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Document }));
        Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.TooDeep && c.IsSecurity);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{watch.Elapsed}");
    }

    [Fact]
    public void AStyleSheetOverTheRuleBudget_IsDropped_NotInlined()
    {
        // The review's case: 7 000 rules × 34 000 elements took 40 s of CPU per request in the service.
        var css = string.Concat(Enumerable.Range(0, 7_000).Select(i => $".c{i}{{color:red}}"));
        var html = "<html><head><style>" + css + "</style></head><body>" + Repeat("<i class=\"c1\">x</i>", 34_000) + "</body></html>";
        var watch = Stopwatch.StartNew();
        var result = MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Document });
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{watch.Elapsed}");
        Assert.DoesNotContain("color", result.Html, StringComparison.Ordinal);
        Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.RemovedElement && c.Detail == "style" && !c.IsSecurity);
        Assert.DoesNotContain(result.Changes, c => c.IsSecurity);
    }

    [Fact]
    public void AStyleSheetWithinTheRuleBudget_IsInlined()
    {
        var html = "<style>.c1{color:red}</style>" + Repeat("<p class=\"c1\">Łódź</p>", 1_000);
        var result = MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions());
        Assert.StartsWith("<p style=\"color:red\">Łódź</p>", result.Html, StringComparison.Ordinal);
    }
}
