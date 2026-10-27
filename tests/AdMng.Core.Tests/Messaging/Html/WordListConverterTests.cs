using AdMng.Core.Messaging.Html;
using AdMng.Testing.RichHtml;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>Word's list paragraphs → real lists: markers, types, start, nesting, item styles.</summary>
public class WordListConverterTests
{
    [Theory]
    [InlineData("·", "ul", null, 1)]
    [InlineData("•", "ul", null, 1)]
    [InlineData("-", "ul", null, 1)]
    [InlineData("–", "ul", null, 1)]
    [InlineData("o", "ul", "circle", 1)]
    [InlineData("§", "ul", "square", 1)]
    [InlineData("Ø", "ul", null, 1)]
    [InlineData("ü", "ul", null, 1)]
    [InlineData("1.", "ol", null, 1)]
    [InlineData("3)", "ol", null, 3)]
    [InlineData("a.", "ol", "lower-alpha", 1)]
    [InlineData("c)", "ol", "lower-alpha", 3)]
    [InlineData("A.", "ol", "upper-alpha", 1)]
    [InlineData("i.", "ol", "lower-roman", 1)]
    [InlineData("iv.", "ol", "lower-roman", 4)]
    [InlineData("I.", "ol", "upper-roman", 1)]
    [InlineData("XII)", "ol", "upper-roman", 12)]
    public void Markers_DecideTheList(string marker, string element, string? type, int start)
    {
        var style = WordListConverter.Classify(marker + "\u00A0\u00A0 ", firstOfLevel: true);
        Assert.Equal(new WordListConverter.ListStyle(element, type, start), style);
    }

    [Fact]
    public void LetterI_InTheMiddleOfAnAlphabeticList_IsALetter() =>
        Assert.Equal(new WordListConverter.ListStyle("ol", "lower-alpha", 9), WordListConverter.Classify("i)", firstOfLevel: false));

    private static string Import(string paragraphs) =>
        PastedHtmlImporter.ImportAsync(
            new HtmlImportRequest { Document = CfHtmlParser.Parse(CfHtmlFixture.Build("<html><body><!--StartFragment-->" + paragraphs + "<!--EndFragment--></body></html>")) },
            TestContext.Current.CancellationToken).GetAwaiter().GetResult().Html;

    private static string Item(string level, string marker, string text, string extra = "") =>
        $"<p class=MsoListParagraph style='margin:0cm;margin-left:36pt;text-indent:-18.0pt;mso-list:l0 {level} lfo1{extra}'><![if !supportLists]><span style='font-family:Symbol'><span style='mso-list:Ignore'>{marker}<span style='font:7.0pt \"Times New Roman\"'>&nbsp;&nbsp; </span></span></span><![endif]>{text}<o:p></o:p></p>";

    [Fact]
    public void TwoLevels_Nest()
    {
        var html = Import(Item("level1", "·", "jeden") + Item("level2", "o", "jeden.a") + Item("level2", "o", "jeden.b") + Item("level1", "·", "dwa"));
        Assert.Equal(
            "<ul style=\"margin-top:0;margin-bottom:0\"><li>jeden<ul style=\"margin-top:0;margin-bottom:0;list-style-type:circle\"><li>jeden.a</li>\n<li>jeden.b</li>\n</ul>\n</li>\n<li>dwa</li>\n</ul>\n",
            html);
    }

    [Fact]
    public void NumberedList_KeepsItsStart_AndTheItemFonts()
    {
        var html = Import(Item("level1", "4.", "cztery", ";font-size:11pt;color:#C00000") + Item("level1", "5.", "pięć", ";font-size:11pt;color:#C00000"));
        Assert.Equal(
            "<ol start=\"4\" style=\"margin-top:0;margin-bottom:0\"><li style=\"font-size:11pt;color:#C00000\">cztery</li>\n<li style=\"font-size:11pt;color:#C00000\">pięć</li>\n</ol>\n",
            html);
    }

    [Fact]
    public void ATypeChange_StartsASiblingList()
    {
        var html = Import(Item("level1", "·", "punkt") + Item("level1", "1.", "numer"));
        Assert.Equal("<ul style=\"margin-top:0;margin-bottom:0\"><li>punkt</li>\n</ul>\n<ol style=\"margin-top:0;margin-bottom:0\"><li>numer</li>\n</ol>\n", html);
    }

    [Fact]
    public void ParagraphsWithVerticalMargins_GiveAListWithItsOwnMargins()
    {
        var html = Import("<p style='margin-top:6pt;margin-bottom:6pt;text-indent:-18pt;mso-list:l0 level1 lfo1'><span style='mso-list:Ignore'>-<span> </span></span>a</p>");
        Assert.Equal("<ul><li>a</li>\n</ul>\n", html);
    }

    [Fact]
    public void ListsBetweenOtherParagraphs_StayInPlace()
    {
        var html = Import("<p>przed</p>" + Item("level1", "·", "x") + "<p>po</p>");
        Assert.Equal("<p>przed</p>\n<ul style=\"margin-top:0;margin-bottom:0\"><li>x</li>\n</ul>\n<p>po</p>\n", html);
    }

    [Fact]
    public void OnlyMarkedParagraphs_AreLevels()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var document = parser.ParseDocument("<p style='mso-list:l3 level2 lfo4'>a</p><p style='mso-list:Ignore'>b</p><span style='mso-list:l0 level1 lfo1'>c</span>");
        var elements = document.Body!.Children.ToList();
        Assert.Equal(2, WordListConverter.ListLevel(elements[0]));
        Assert.Null(WordListConverter.ListLevel(elements[1]));
        Assert.Null(WordListConverter.ListLevel(elements[2]));
    }
}
