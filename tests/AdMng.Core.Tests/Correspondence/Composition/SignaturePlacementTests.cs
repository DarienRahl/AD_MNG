using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using static AdMng.Core.Tests.Correspondence.Composition.CompositionTestData;

namespace AdMng.Core.Tests.Correspondence.Composition;

/// <summary><c>{{podpis}}</c>: the signature where the body places it – no line break before it, no empty paragraph, not
/// appended again – and "" everywhere else.</summary>
public class SignaturePlacementTests
{
    private static readonly CorrespondenceSignature Signature = new(
        "Zespół IT",
        "<p style=\"color:#1F3864\"><b>Zespół Wsparcia Użytkowników</b><br>tel. 42 000 00 00</p>",
        []);

    private static RenderedCorrespondence Render(string body, CorrespondenceSignature? signature, string subject = "Skrzynka", string listItem = "",
        bool group = false, IReadOnlyList<CustomFieldDefinition>? custom = null) =>
        CorrespondenceRenderer.Render(new CorrespondenceDraft
        {
            Subject = subject,
            BodyHtml = body,
            Signature = signature,
            ListItemTemplate = listItem,
            GroupByAddress = group,
            CustomFields = custom ?? [],
        }, Unit(), Columns);

    /// <summary>The children of the wrapping div of a rendered document.</summary>
    private static IReadOnlyList<IElement> Blocks(RenderedCorrespondence rendered) =>
        [.. new HtmlParser().ParseDocument(rendered.HtmlDocument).Body!.Children.Single().Children];

    [Fact]
    public void ItsParagraph_IsReplacedByTheSignature_WithoutALineBreakBefore()
    {
        var rendered = Render("<p>Z poważaniem</p><p><b>Zespół Informatyczny</b></p><p>{{podpis}}</p>", Signature);

        var blocks = Blocks(rendered);
        Assert.Equal(["p", "p", "p"], blocks.Select(b => b.LocalName));
        Assert.Equal("Zespół Informatyczny", blocks[1].TextContent);
        Assert.Equal("color:#1F3864", blocks[2].GetAttribute("style"));
        Assert.StartsWith("Zespół Wsparcia Użytkowników", blocks[2].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("<br><p", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("<p></p>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(rendered.HtmlDocument, "Zespół Wsparcia Użytkowników"));   // not appended again
        Assert.Empty(rendered.EmptyFields);
        Assert.Empty(rendered.UnknownFields);
        Assert.Equal(rendered.HtmlDocument, Resanitized(rendered));
    }

    [Fact]
    public void TextAroundTheToken_StaysInParagraphsOfItsOwn()
    {
        var rendered = Render("<p>Pozdrawiam,<br><i>{{podpis}}</i> – do usłyszenia</p>", Signature);

        var blocks = Blocks(rendered);
        Assert.Equal(["p", "p", "p"], blocks.Select(b => b.LocalName));
        Assert.Equal("Pozdrawiam,", blocks[0].TextContent.Trim());
        Assert.Equal(["br"], blocks[0].Children.Select(c => c.LocalName));
        Assert.StartsWith("Zespół Wsparcia Użytkowników", blocks[1].TextContent, StringComparison.Ordinal);
        Assert.Equal(" – do usłyszenia", blocks[2].TextContent);
        Assert.Equal("i", Assert.Single(blocks[2].Children).LocalName);   // the formatting around the token split too
    }

    [Fact]
    public void WithoutASignature_TheTokenRendersNothing_AndIsNoEmptyField()
    {
        var rendered = Render("<p>Z poważaniem</p><p>{{podpis}}</p>", null);

        Assert.Equal(["p"], Blocks(rendered).Select(b => b.LocalName));
        Assert.DoesNotContain("<p></p>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("<br>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Empty(rendered.EmptyFields);
        Assert.Empty(rendered.UnknownFields);
    }

    [Fact]
    public void ABlankSignature_IsLikeNone() =>
        Assert.Equal(["p"], Blocks(Render("<p>Z poważaniem</p><p>{{podpis}}</p>", Signature with { BodyHtml = "  " })).Select(b => b.LocalName));

    [Fact]
    public void WithoutTheToken_TheSignatureIsStillAppendedAfterALineBreak()
    {
        var rendered = Render("<p>Z poważaniem</p>", Signature);

        Assert.Equal(["p", "br", "p"], Blocks(rendered).Select(b => b.LocalName));
        Assert.EndsWith("tel. 42 000 00 00", Blocks(rendered)[2].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void InTheSubjectListItemsAndCustomValues_ItRendersNothing()
    {
        var rendered = Render(
            "<p>{{Podpis zespołu}}</p><p>{{lista}}</p>",
            Signature,
            subject: "Skrzynki {{podpis}}– {{Miejscowość}}",
            listItem: "{{Miejscowość}}{{podpis}}",
            group: true,
            custom: [new CustomFieldDefinition("Podpis zespołu", "Zespół {{podpis}}IT")]);

        Assert.Equal("Skrzynki – Żórawina", rendered.Subject);
        Assert.Contains("<li>Żórawina</li>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("<p>Zespół IT</p>", rendered.HtmlDocument, StringComparison.Ordinal);
        var blocks = Blocks(rendered);
        Assert.Equal(["br", "p"], blocks.TakeLast(2).Select(b => b.LocalName));   // appended: the body has no token
        Assert.Equal("color:#1F3864", blocks[^1].GetAttribute("style"));
        Assert.Empty(rendered.EmptyFields);
        Assert.Empty(rendered.UnknownFields);
    }

    [Fact]
    public void AColumnOrCustomFieldNamedPodpis_NeverWins()
    {
        var row = Row();
        row["Podpis"] = "kolumna";
        var rendered = CorrespondenceRenderer.Render(new CorrespondenceDraft
        {
            Subject = "{{Podpis}}Temat",
            BodyHtml = "<p>{{Podpis}}</p>",
            Signature = Signature,
            CustomFields = [new CustomFieldDefinition("podpis", "własne")],
        }, Unit(row), [.. Columns, "Podpis"]);

        Assert.Equal("Temat", rendered.Subject);
        Assert.DoesNotContain("kolumna", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("własne", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("Zespół Wsparcia Użytkowników", rendered.HtmlDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void SignatureImages_StillGoWithTheMessage()
    {
        var logo = Image(0x61);
        var rendered = Render("<p>Treść</p><p>{{podpis}}</p>",
            new CorrespondenceSignature("Logo", $"<p>Zespół IT <img src=\"cid:{logo.ContentId}\" alt=\"logo\"></p>", [logo]));

        Assert.Equal([logo.ContentId], rendered.Images.Select(i => i.ContentId));
        Assert.Contains("cid:" + logo.ContentId, rendered.HtmlDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListAndTheSignature_BothLeaveTheirParagraphs()
    {
        var rendered = CorrespondenceRenderer.Render(new CorrespondenceDraft
        {
            Subject = "Skrzynki",
            BodyHtml = "<div style=\"margin-bottom:10.5pt\"><p style=\"margin:0\">Skrzynki:</p><p style=\"margin:0\">{{lista}}</p></div><p>{{podpis}}</p>",
            Signature = Signature,
            ListItemTemplate = "{{Miejscowość}}",
            GroupByAddress = true,
        }, Unit(Row(), Row(town: "Kobierzyce")), Columns);

        var blocks = Blocks(rendered);
        Assert.Equal(["div", "p"], blocks.Select(b => b.LocalName));
        Assert.Equal(["p", "ul"], blocks[0].Children.Select(c => c.LocalName));
        Assert.Equal(["Żórawina", "Kobierzyce"], blocks[0].Children[1].Children.Select(li => li.TextContent));
        Assert.DoesNotContain("<p style=\"margin:0\"></p>", rendered.HtmlDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void Signature_IsABuiltInField() => Assert.Contains(BuiltInFields.Signature, BuiltInFields.All);

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
