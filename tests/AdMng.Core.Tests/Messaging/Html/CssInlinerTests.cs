using AdMng.Core.Messaging.Html;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>The cascade into style attributes and the inherited dedupe of the sanitizer.</summary>
public class CssInlinerTests
{
    private static string Sanitize(string html) => MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions()).Html.TrimEnd('\n');

    [Fact]
    public void Specificity_ThenSourceOrder()
    {
        const string html = "<style>p{color:red} .k{color:green} p.k{color:blue} p{font-size:10pt} p{font-size:11pt}</style><p class=k>tekst</p>";
        Assert.Equal("<p style=\"font-size:11pt;color:blue\">tekst</p>", Sanitize(html));
    }

    [Fact]
    public void InlineStyle_BeatsNormalRules_ImportantRuleBeatsInline_ImportantInlineBeatsAll()
    {
        Assert.Equal("<p style=\"color:green\">a</p>", Sanitize("<style>p{color:red}</style><p style='color:green'>a</p>"));
        Assert.Equal("<p style=\"color:red\">a</p>", Sanitize("<style>p{color:red !important}</style><p style='color:green'>a</p>"));
        Assert.Equal("<p style=\"color:green\">a</p>", Sanitize("<style>p{color:red !important}</style><p style='color:green !important'>a</p>"));
    }

    [Fact]
    public void AWinningShorthand_RemovesLowerLonghands()
    {
        // The rule's margin-left loses to the inline margin; the inline margin-left after the rule's margin wins.
        Assert.Equal("<p style=\"margin:0\">a</p>", Sanitize("<style>p{margin-left:36pt}</style><p style='margin:0'>a</p>"));
        Assert.Equal("<p style=\"margin:0;margin-left:36pt\">a</p>", Sanitize("<style>p{margin:0}</style><p style='margin-left:36pt'>a</p>"));
        Assert.Equal("<p style=\"margin:0\">a</p>", Sanitize("<p style='margin-left:36pt;margin:0'>a</p>"));
    }

    [Fact]
    public void UnsupportedSelectors_AreSkipped()
    {
        Assert.Equal("<p>a b</p>", Sanitize("<style>p span{color:red} a:hover{color:red}</style><p>a <span>b</span></p>"));
    }

    [Fact]
    public void InheritedValues_AreNotRepeated()
    {
        const string html = "<div style='font-family:Arial;color:#333'><p style='font-family:Arial;color:#333;font-size:12pt'><span style='font-size:12pt;color:#333'>x</span></p></div>";
        Assert.Equal("<div style=\"font-family:Arial;color:#333\"><p style=\"font-size:12pt\">x</p>\n</div>", Sanitize(html));
    }

    [Fact]
    public void UserAgentDefaults_StopTheDedupe()
    {
        // A link's colour, a heading's size and the fonts of a table (quirks mode) are not inherited from the parent.
        Assert.Equal("<p style=\"color:#467886\"><a href=\"https://contoso.pl/\" style=\"color:#467886\">x</a></p>",
            Sanitize("<p style='color:#467886'><a href='https://contoso.pl/' style='color:#467886'>x</a></p>"));
        Assert.Equal("<div style=\"font-size:12pt\"><h1 style=\"font-size:12pt\">x</h1>\n</div>",
            Sanitize("<div style='font-size:12pt'><h1 style='font-size:12pt'>x</h1></div>"));
        Assert.Contains("<td style=\"font-family:Arial\">", Sanitize("<div style='font-family:Arial'><table><tr><td style='font-family:Arial'>x</td></tr></table></div>"), StringComparison.Ordinal);
    }

    [Fact]
    public void Declarations_AreWrittenInAFixedOrder()
    {
        Assert.Equal("<p style=\"margin:0;padding:1pt;width:10px;font-weight:bold;font-size:9pt;font-family:Arial;color:red;text-align:center\">a</p>",
            Sanitize("<p style='text-align:center;color:red;font-family:Arial;font-size:9pt;font-weight:bold;width:10px;padding:1pt;margin:0'>a</p>"));
    }
}
