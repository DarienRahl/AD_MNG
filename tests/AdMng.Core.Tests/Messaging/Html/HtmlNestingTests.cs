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
        // The parse alone took about a minute; the 400 000-character limit of the service allows this much. The estimate on
        // the text refuses it before any parse (HtmlNesting.ParseDocument): checked as such, not with a clock – a busy
        // runner is slow, not wrong.
        var html = Repeat("<div>", 80_000);
        Assert.True(HtmlNesting.IsTextTooDeep(html));
        var result = SmallStack.Run(() => MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Document }));
        Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.TooDeep && c.IsSecurity);
    }

    /// <summary>A document with a style sheet of <paramref name="rules"/> rules and <paramref name="paragraphs"/> styled
    /// paragraphs: the inliner would match every rule against <c>html</c>, <c>head</c>, <c>body</c> and each paragraph.</summary>
    private static string StyledDocument(int rules, int paragraphs) =>
        "<html><head><style>" + string.Concat(Enumerable.Range(0, rules).Select(i => $".c{i}{{color:red}}")) + "</style></head><body>"
        + Repeat("<p class=\"c1\">Łódź</p>", paragraphs) + "</body></html>";

    private const int BudgetRules = 2_000;

    /// <summary>Paragraphs that bring <see cref="BudgetRules"/> rules exactly to <see cref="MessageHtmlLimits.MaxStyleRuleMatches"/>.</summary>
    private static int ParagraphsAtTheBudget => (int)(MessageHtmlLimits.MaxStyleRuleMatches / BudgetRules) - 3;

    [Fact]
    public void AStyleSheetOverTheRuleBudget_IsDropped_NotInlined()
    {
        // The review's case – 7 000 rules × 34 000 elements – took 40 s of CPU per request in the service. The budget
        // counts rule matches (rules × elements), not time: one paragraph more than the budget allows drops the style
        // sheet on any machine, and a clock would only measure the runner.
        var html = StyledDocument(BudgetRules, ParagraphsAtTheBudget + 1);
        var result = MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Document });
        Assert.DoesNotContain("color", result.Html, StringComparison.Ordinal);
        Assert.Contains(result.Changes, c => c.Kind == HtmlChangeKind.RemovedElement && c.Detail == "style" && !c.IsSecurity);
        Assert.DoesNotContain(result.Changes, c => c.IsSecurity);
    }

    [Fact]
    public void AStyleSheetAtTheRuleBudget_IsStillInlined()
    {
        var html = StyledDocument(BudgetRules, ParagraphsAtTheBudget);
        var result = MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Document });
        Assert.Contains("<p style=\"color:red\">Łódź</p>", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void AStyleSheetWithinTheRuleBudget_IsInlined()
    {
        var html = "<style>.c1{color:red}</style>" + Repeat("<p class=\"c1\">Łódź</p>", 1_000);
        var result = MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions());
        Assert.StartsWith("<p style=\"color:red\">Łódź</p>", result.Html, StringComparison.Ordinal);
    }
}
