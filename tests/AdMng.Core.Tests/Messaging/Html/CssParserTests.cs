using AdMng.Core.Messaging.Html;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>Declarations, rules, the supported selector subset, CDO/CDC, comments and at-rules.</summary>
public class CssParserTests
{
    [Fact]
    public void Declarations_KeepQuotesParenthesesAndImportant()
    {
        var declarations = CssParser.ParseDeclarations("font-family:\"Aptos\",sans-serif; COLOR: rgb(1, 2, 3) !important;;margin:0cm 0cm 8.0pt; bad; :x; a b:c");
        Assert.Equal(
        [
            new CssDeclaration("font-family", "\"Aptos\",sans-serif"),
            new CssDeclaration("color", "rgb(1, 2, 3)", Important: true),
            new CssDeclaration("margin", "0cm 0cm 8.0pt"),
        ],
        declarations);
    }

    [Fact]
    public void SemicolonsInsideStrings_DoNotSplit()
    {
        var declarations = CssParser.ParseDeclarations("font-family:'A;B', serif; color:red");
        Assert.Equal(["font-family", "color"], declarations.Select(d => d.Property));
        Assert.Equal("'A;B', serif", declarations[0].Value);
    }

    [Fact]
    public void OutlookStyleSheet_SupportedRulesOnly()
    {
        const string css = """
            <!--
            /* Font Definitions */
            @font-face {font-family:Aptos; panose-1:2 11 0 4 2 2 2 2 2 4;}
            p.MsoNormal, li.MsoNormal, div.MsoNormal {margin:0cm; font-size:12.0pt;}
            a:link, span.MsoHyperlink {color:#467886;}
            a:visited, span.MsoHyperlinkFollowed {color:#96607D;}
            @page WordSection1 {size:612.0pt 792.0pt;}
            div.WordSection1 {page:WordSection1;}
            @list l0:level1 {mso-level-text:\F0B7; font-family:Symbol;}
            @media print { p { color: black } }
            v\:* {behavior:url(#default#VML);}
            #naglowek {color:red}
            table td {padding:0}
            .Klasa_x {font-weight:bold}
            -->
            """;
        var rules = CssParser.ParseStyleSheet(css);
        Assert.Equal(5, rules.Count);
        Assert.Equal(["p", "li", "div"], rules[0].Selectors.Select(s => s.Type));
        Assert.All(rules[0].Selectors, s => Assert.Equal("MsoNormal", s.Class));
        Assert.Equal(11, rules[0].Selectors[0].Specificity);
        Assert.Equal("a", rules[1].Selectors[0].Type);
        Assert.Equal(11, rules[1].Selectors[0].Specificity);
        Assert.Equal("div", rules[2].Selectors[0].Type);
        Assert.Equal("naglowek", rules[3].Selectors[0].Id);
        Assert.Equal(100, rules[3].Selectors[0].Specificity);
        Assert.Equal("Klasa_x", rules[4].Selectors[0].Class);
        Assert.Equal([0, 1, 2, 3, 4], rules.Select(r => r.Order));
    }

    [Theory]
    [InlineData("p > span")]
    [InlineData("p span")]
    [InlineData("a[href]")]
    [InlineData("a:hover")]
    [InlineData("p.a.b")]
    [InlineData("*")]
    [InlineData("p, td span")]
    public void UnsupportedSelectors_SkipTheRule(string selector) => Assert.Null(CssParser.ParseSelectors(selector));

    [Fact]
    public void Matching_IgnoresCaseOfClassesAndTypes()
    {
        var selector = CssParser.ParseSelectors("P.msonormal")![0];
        Assert.True(selector.Matches("p", ["MsoNormal"], null));
        Assert.False(selector.Matches("div", ["MsoNormal"], null));
        Assert.False(selector.Matches("p", ["Inna"], null));
    }

    [Fact]
    public void OrderOffset_ContinuesAcrossStyleElements()
    {
        var rules = CssParser.ParseStyleSheet("p{color:red} b{color:blue}", orderOffset: 7);
        Assert.Equal([7, 8], rules.Select(r => r.Order));
    }

    [Fact]
    public void Comments_AreRemovedEvenInsideARule()
    {
        var rules = CssParser.ParseStyleSheet("p { color: red; /* font-size: 0; */ margin: 0 }");
        Assert.Equal(["color", "margin"], rules[0].Declarations.Select(d => d.Property));
    }
}
