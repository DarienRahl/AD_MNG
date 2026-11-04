using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>Fields split by Outlook / Word are joined again; text that only looks like braces is left alone.</summary>
public class PlaceholderRepairTests
{
    private static readonly UserMessageFields Fields = new();

    private static (string Html, IReadOnlyList<string> Repaired) Repair(string html)
    {
        var run = HtmlSanitizerRun.Execute(html, new HtmlSanitizeOptions());
        var repaired = PlaceholderRepair.Repair(run.Body, c => PastedHtmlImporter.IsField(c, Fields.Engine));
        run.Finish();
        return (run.Result().Html.TrimEnd('\n'), repaired);
    }

    [Fact]
    public void SplitBySpellCheckSpans_IsJoined()
    {
        // span.SpellE / GramE lose their class and are unwrapped: the text nodes merge on their own.
        var (html, _) = Repair("<p>Dzień dobry {<span class=SpellE>given</span>Name}!</p>");
        Assert.Equal("<p>Dzień dobry {givenName}!</p>", html);
    }

    [Fact]
    public void SplitByFormatting_TheLongestPieceWins()
    {
        var (html, repaired) = Repair("<p>Konto <b>{sAMAccount</b>Name} wygasa</p>");
        Assert.Equal("<p>Konto <b>{sAMAccountName}</b> wygasa</p>", html);
        Assert.Equal(["{sAMAccountName}"], repaired);

        // The longer piece is outside the bold run: the field loses the bold, the empty <b> goes.
        Assert.Equal("<p>Konto {sAMAccountName} wygasa</p>", Repair("<p>Konto <b>{sAM</b>AccountName} wygasa</p>").Html);

        var (other, _) = Repair("<p><span style='color:red'>{$pass</span><span style='color:blue'>wordExpiresInDays}</span></p>");
        Assert.Equal("<p><span style=\"color:blue\">{$passwordExpiresInDays}</span></p>", other);
    }

    [Fact]
    public void SplitByALanguageSpan_IsJoined()
    {
        var (html, repaired) = Repair("<p>wygaśnie <span lang=EN-US style='font-size:11pt'>{$passwordExpires:d</span> MMMM yyyy}</p>");
        Assert.Equal("<p>wygaśnie <span lang=\"EN-US\" style=\"font-size:11pt\">{$passwordExpires:d MMMM yyyy}</span></p>", html);
        Assert.Single(repaired);
    }

    [Fact]
    public void NonBreakingSpacesAndSoftHyphens_InsideAField_AreRepaired()
    {
        var (html, repaired) = Repair("<p>{$passwordExpires:d&nbsp;MMMM yyyy} i {given&shy;Name\u200B}</p>");
        Assert.Equal("<p>{$passwordExpires:d MMMM yyyy} i {givenName}</p>", html);
        Assert.Equal(2, repaired.Count);
    }

    [Fact]
    public void FieldsNeverCrossBreaksOrBlocks()
    {
        Assert.Equal("<p>{given<br>Name}</p>", Repair("<p>{given<br>Name}</p>").Html);
        Assert.Equal("<div>{given<p>Name}</p>\n</div>", Repair("<div>{given<p>Name}</p></div>").Html);
    }

    [Fact]
    public void FalseCandidates_AreLeftAlone()
    {
        const string html = "<p>{ to jest nawias } <b>{</b>nie pole} {{literal}} {$nieznane}</p>";
        var (result, repaired) = Repair(html);
        Assert.Equal("<p>{ to jest nawias } <b>{</b>nie pole} {{literal}} {$nieznane}</p>", result);
        Assert.Empty(repaired);
    }

    [Fact]
    public void EncodedFieldsInLinks_AreDecoded()
    {
        var (html, repaired) = Repair("<p><a href=\"https://portal.contoso.pl/%7BsAMAccountName%7D?d=%7B%24passwordExpiresInDays%7D&amp;x=%7Bnie%20pole%7D\">portal</a></p>");
        Assert.Equal("<p><a href=\"https://portal.contoso.pl/{sAMAccountName}?d={$passwordExpiresInDays}&amp;x=%7Bnie%20pole%7D\">portal</a></p>", html);
        Assert.Equal(["{sAMAccountName}", "{$passwordExpiresInDays}"], repaired);
    }

    [Fact]
    public void SeveralFieldsInOneParagraph()
    {
        var (html, repaired) = Repair("<p><i>{given</i>Name} <i>{s</i>n} – {department}</p>");
        Assert.Equal("<p><i>{givenName}</i> <i>{sn}</i> – {department}</p>", html);
        Assert.Equal(["{givenName}", "{sn}"], repaired);
    }
}
