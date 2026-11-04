using AdMng.Core.Correspondence.Fields;

namespace AdMng.Core.Tests.Correspondence.Fields;

public class RepairHtmlTests
{
    [Theory]
    [InlineData("<p>Dzień dobry {{nazwa}}!</p>")]
    [InlineData("<p>{{ Kod pocztowy | brak }}</p>")]
    [InlineData("<p>bez pól</p>")]
    [InlineData("<p>{pojedyncze} i {{ niezamknięte</p>")]
    [InlineData("")]
    public void UnsplitHtml_IsUnchanged(string html) => Assert.Equal(html, FieldTemplate.RepairHtml(html));

    [Fact]
    public void Null_IsEmpty() => Assert.Equal(string.Empty, FieldTemplate.RepairHtml(null));

    [Fact]
    public void FieldWrappedInATag_GoesIntoTheTag() =>
        Assert.Equal("<p>Dla <span style=\"color:red\">{{nazwa}}</span></p>", FieldTemplate.RepairHtml("<p>Dla {{<span style=\"color:red\">nazwa</span>}}</p>"));

    [Fact]
    public void FieldSplitByFormatting_IsJoinedWhereMostOfItsNameIs()
    {
        var repaired = FieldTemplate.RepairHtml("<p>W {{Miej<b>scowość</b>}} dnia</p>");
        Assert.Equal("<p>W <b>{{Miejscowość}}</b> dnia</p>", repaired);
    }

    [Fact]
    public void BracesSplitByTags_AreJoined()
    {
        var repaired = FieldTemplate.RepairHtml("<p>{<span>{</span>zwrot}<i>}</i>,</p>");
        Assert.Equal("<p><span></span>{{zwrot}}<i></i>,</p>", repaired);
        Assert.Single(FieldTemplate.Parse(repaired));
    }

    [Fact]
    public void NonBreakingSpaces_BecomeSpaces()
    {
        Assert.Equal("<p>{{Kod pocztowy}}</p>", FieldTemplate.RepairHtml("<p>{{Kod&nbsp;pocztowy}}</p>"));
        Assert.Equal("<p>{{Kod pocztowy}}</p>", FieldTemplate.RepairHtml("<p>{{Kod&#160;pocztowy}}</p>"));
        Assert.Equal("<p>{{Kod pocztowy}}</p>", FieldTemplate.RepairHtml("<p>{{Kod&#xA0;pocztowy}}</p>"));
        Assert.Equal("<p>{{Kod pocztowy}}</p>", FieldTemplate.RepairHtml("<p>{{Kod\u00A0pocztowy}}</p>"));
        Assert.Equal("<p>{{ nazwa | brak danych }}</p>", FieldTemplate.RepairHtml("<p>{{&nbsp;nazwa&nbsp;|&nbsp;brak danych&nbsp;}}</p>"));
    }

    [Fact]
    public void InvisibleCharactersAndSourceLineBreaks_AreDropped()
    {
        Assert.Equal("<p>{{nazwa}}</p>", FieldTemplate.RepairHtml("<p>{{na\u00ADzwa\u200B}}</p>"));
        Assert.Equal("<p>{{Kod pocztowy}}</p>", FieldTemplate.RepairHtml("<p>{{Kod\npocztowy}}</p>"));
    }

    [Fact]
    public void NeverAcrossBlocksOrLineBreaks()
    {
        Assert.Equal("<p>{{na</p><p>zwa}}</p>", FieldTemplate.RepairHtml("<p>{{na</p><p>zwa}}</p>"));
        Assert.Equal("<p>{{na<br>zwa}}</p>", FieldTemplate.RepairHtml("<p>{{na<br>zwa}}</p>"));
        Assert.Equal("<td>{{na</td><td>zwa}}</td>", FieldTemplate.RepairHtml("<td>{{na</td><td>zwa}}</td>"));
    }

    [Fact]
    public void NeverInsideAttributeValues()
    {
        var html = "<p><a href=\"https://x.pl/?q={{<b>nazwa</b>}}\" title='{{a&nbsp;b}}'>{{<i>e</i>mail}}</a> <img alt=\"}} >\" src=\"cid:x\">{{x}}</p>";
        var repaired = FieldTemplate.RepairHtml(html);
        Assert.Equal("<p><a href=\"https://x.pl/?q={{<b>nazwa</b>}}\" title='{{a&nbsp;b}}'><i></i>{{email}}</a> <img alt=\"}} >\" src=\"cid:x\">{{x}}</p>", repaired);
    }

    [Fact]
    public void ATokenNeverJoinsWithBracesInAnAttribute()
    {
        // The "}}" inside the attribute belongs to the tag: the text "{{a" has no closing braces.
        const string html = "<p>{{a<span title=\"}}\">b</span></p>";
        Assert.Equal(html, FieldTemplate.RepairHtml(html));
    }

    [Fact]
    public void CommentsScriptsAndStylesAreNotTouched()
    {
        const string html = "<p>{{na<!-- x -->zwa}}</p><style>p{{color:red}}</style><script>var a='{{<b>x</b>}}';</script>";
        Assert.Equal(html, FieldTemplate.RepairHtml(html));
    }

    [Fact]
    public void SeveralTokensInOneParagraph()
    {
        var repaired = FieldTemplate.RepairHtml("<p>{{zw<b>rot</b>}}, {{na<i>zwa</i>}} i {{email}} oraz {{Kod&nbsp;pocztowy}}.</p>");
        Assert.Equal("<p><b>{{zwrot}}</b>, <i>{{nazwa}}</i> i {{email}} oraz {{Kod pocztowy}}.</p>", repaired);
        Assert.Equal(["zwrot", "nazwa", "email", "Kod pocztowy"], FieldTemplate.UsedFields(repaired));
    }

    [Fact]
    public void InvalidCandidatesStayAsTheyAre()
    {
        const string html = "<p>{{<b>a{b</b>}} {{" + "x" + "</p>";
        Assert.Equal(html, FieldTemplate.RepairHtml(html));
    }

    [Fact]
    public void UppercaseTagsAndUnquotedAttributes()
    {
        var repaired = FieldTemplate.RepairHtml("<P>{{<SPAN class=x>nazwa</SPAN>}}</P>");
        Assert.Equal("<P><SPAN class=x>{{nazwa}}</SPAN></P>", repaired);
    }
}
