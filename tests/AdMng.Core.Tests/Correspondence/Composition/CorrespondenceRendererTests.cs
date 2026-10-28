using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using static AdMng.Core.Tests.Correspondence.Composition.CompositionTestData;

namespace AdMng.Core.Tests.Correspondence.Composition;

public class CorrespondenceRendererTests
{
    private static RenderedCorrespondence Render(CorrespondenceDraft draft, CorrespondenceUnit? unit = null, IReadOnlyCollection<string>? columns = null) =>
        CorrespondenceRenderer.Render(draft, unit ?? Unit(), columns ?? Columns);

    [Fact]
    public void SubjectAndBody_AreMerged_TheDocumentIsSanitized()
    {
        var rendered = Render(new CorrespondenceDraft
        {
            Subject = "  Pismo do {{nazwa}} – {{Miejscowość}} ",
            BodyHtml = "<p>{{zwrot}},</p><p>w nawiązaniu do działalności {{nazwa}} w {{Miejscowość}} ({{Kod pocztowy}})…</p>",
        });
        Assert.Equal("Pismo do Urząd Gminy Żórawina – Żórawina", rendered.Subject);
        Assert.StartsWith("<!DOCTYPE html>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("font-family:Calibri", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("font-size:11pt", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("<p>Szanowni Państwo,</p>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("w nawiązaniu do działalności Urząd Gminy Żórawina w Żórawina (55-020)…", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Empty(rendered.UnknownFields);
        Assert.Empty(rendered.EmptyFields);
        Assert.Empty(rendered.Images);
        Assert.Equal(rendered.HtmlDocument, Resanitized(rendered));
        Assert.Empty(UserMessageRules.FindForbiddenHtml(rendered.HtmlDocument));
    }

    [Fact]
    public void ValuesCannotInjectMarkup()
    {
        var row = Row(name: "<script>alert(1)</script> & <b onclick=\"x\">Spółka</b>", town: "javascript:alert(2)");
        var rendered = Render(new CorrespondenceDraft
        {
            Subject = "{{nazwa}}",
            BodyHtml = "<p>{{nazwa}}</p><p><a href=\"{{Miejscowość}}\">link</a> <a href=\"https://gmina.pl/{{Kod pocztowy}}\">strona</a></p>",
        }, new CorrespondenceUnit("a@b.pl", "<script>alert(1)</script> & <b onclick=\"x\">Spółka</b>", [row]));
        Assert.DoesNotContain("<script", rendered.HtmlDocument, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b onclick", rendered.HtmlDocument, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", rendered.HtmlDocument, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; &lt;b onclick=", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("href=\"https://gmina.pl/55-020\"", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Equal("<script>alert(1)</script> & <b onclick=\"x\">Spółka</b>", rendered.Subject);   // a subject is text
    }

    [Fact]
    public void SplitTokensInTheBodyAreRepaired()
    {
        var rendered = Render(new CorrespondenceDraft { BodyHtml = "<p>Dla {{na<b>zwa</b>}} w {{Miejscowość&nbsp;}}</p>" });
        Assert.Contains("<b>Urząd Gminy Żórawina</b>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("w Żórawina", rendered.HtmlDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void Group_ListsItsRows_CountsThem_FirstRowGivesTheOtherFields()
    {
        var rows = new[]
        {
            new Dictionary<string, string> { ["Zwrot osobowy"] = "Szanowny Panie", ["Skrzynka"] = "Sekretariat IT", ["Adres skrzynki"] = "sekretariat.it@contoso.test", ["Ostatnie logowanie"] = "3 marca 2026" },
            new Dictionary<string, string> { ["Zwrot osobowy"] = "inny", ["Skrzynka"] = "Rekrutacja <HR>", ["Adres skrzynki"] = "rekrutacja@contoso.test", ["Ostatnie logowanie"] = "" },
            new Dictionary<string, string> { ["Zwrot osobowy"] = "inny", ["Skrzynka"] = "Archiwum", ["Adres skrzynki"] = "archiwum@contoso.test", ["Ostatnie logowanie"] = "1 stycznia 2025" },
        };
        var draft = new CorrespondenceDraft
        {
            Subject = "Skrzynki ({{liczba}}): {{lista}}",
            BodyHtml = "<p>{{Zwrot osobowy}},</p><p>{{lista}}</p><p>Skrzynek: {{liczba}}.</p>",
            ListItemTemplate = "<b>{{Skrzynka}}</b> ({{Adres skrzynki}}) – ostatnie logowanie: {{Ostatnie logowanie|brak danych}}",
        };
        var rendered = Render(draft, new CorrespondenceUnit("jan.kowalski@contoso.test", "Jan Kowalski", rows), ["Zwrot osobowy", "Skrzynka", "Adres skrzynki", "Ostatnie logowanie"]);
        var html = rendered.HtmlDocument;
        Assert.Contains("<p>Szanowny Panie,</p>", html, StringComparison.Ordinal);
        Assert.Equal(1, Count(html, "<ul"));
        Assert.Equal(3, Count(html, "<li>"));
        Assert.Contains("<b>Sekretariat IT</b> (sekretariat.it@contoso.test) – ostatnie logowanie: 3 marca 2026", html, StringComparison.Ordinal);
        Assert.Contains("<b>Rekrutacja &lt;HR&gt;</b> (rekrutacja@contoso.test) – ostatnie logowanie: brak danych", html, StringComparison.Ordinal);
        Assert.Contains("Skrzynek: 3.", html, StringComparison.Ordinal);
        Assert.StartsWith("Skrzynki (3): ", rendered.Subject, StringComparison.Ordinal);
        Assert.Contains("Archiwum (archiwum@contoso.test)", rendered.Subject, StringComparison.Ordinal);
        Assert.Contains("Rekrutacja <HR> (rekrutacja@contoso.test)", rendered.Subject, StringComparison.Ordinal);   // the subject is text
        Assert.DoesNotContain("\n", rendered.Subject, StringComparison.Ordinal);
        Assert.Empty(rendered.EmptyFields);
        Assert.Equal(html, Resanitized(rendered));
    }

    [Fact]
    public void AParagraphHoldingTheList_IsReplacedByIt()
    {
        var rendered = Render(new CorrespondenceDraft { BodyHtml = "<p>Przed</p><p style=\"margin:0 0 8pt 0\">{{lista}}</p><p>Po</p>", ListItemTemplate = "{{Miejscowość}}" });
        Assert.DoesNotContain("<p></p>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Matches("<p>Przed</p>\\s*<ul[^>]*><li>Żórawina</li>\\s*</ul>\\s*<p>Po</p>", rendered.HtmlDocument);
    }

    [Fact]
    public void AParagraphWithTextAroundTheList_IsSplit()
    {
        var rendered = Render(new CorrespondenceDraft
        {
            BodyHtml = "<p style=\"text-align:center\">Skrzynki: <b>{{lista}}</b> – proszę o odpowiedź, {{nazwa}}.</p>",
            ListItemTemplate = "{{Miejscowość}}",
        });
        var html = rendered.HtmlDocument;
        Assert.DoesNotContain("<p></p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b></b>", html, StringComparison.Ordinal);
        var before = html.IndexOf("Skrzynki:", StringComparison.Ordinal);
        var list = html.IndexOf("<ul", StringComparison.Ordinal);
        var after = html.IndexOf("– proszę o odpowiedź, Urząd Gminy Żórawina.", StringComparison.Ordinal);
        Assert.True(before >= 0 && before < list && list < after, html);
        Assert.Equal(2, Count(html, "text-align:center"));   // both halves keep the paragraph's formatting
        Assert.Equal(html, Resanitized(rendered));
    }

    [Fact]
    public void ListInACellStaysWhereItIs()
    {
        var rendered = Render(new CorrespondenceDraft { BodyHtml = "<table><tr><td>Lista: {{lista}}</td></tr></table>", ListItemTemplate = "{{Miejscowość}}" });
        Assert.Matches("<td>Lista: <ul[^>]*><li>Żórawina</li>\\s*</ul>\\s*</td>", rendered.HtmlDocument);
    }

    [Fact]
    public void SingleRow_ListHasOneItem()
    {
        var rendered = Render(new CorrespondenceDraft { BodyHtml = "<p>{{lista}}</p><p>{{liczba}}</p>", ListItemTemplate = "{{Miejscowość}}" });
        Assert.Equal(1, Count(rendered.HtmlDocument, "<li>"));
        Assert.Contains("<li>Żórawina</li>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("<p>1</p>", rendered.HtmlDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void UnusedList_IsNotRendered_ItsFieldsNotReported()
    {
        var rendered = Render(new CorrespondenceDraft { Subject = "Temat", BodyHtml = "<p>Bez listy</p>", ListItemTemplate = "{{Nieznane}} {{Telefon}}" });
        Assert.DoesNotContain("<ul", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Empty(rendered.UnknownFields);
        Assert.Empty(rendered.EmptyFields);
    }

    [Fact]
    public void ListOnlyInTheSubject()
    {
        var rendered = Render(new CorrespondenceDraft { Subject = "Dotyczy: {{lista}}", BodyHtml = "<p>x</p>", ListItemTemplate = "<b>{{Miejscowość}}</b> {{Kod pocztowy}}" },
            Unit(Row(), Row(town: "Łódź")));
        Assert.Equal("Dotyczy: Żórawina 55-020, Łódź 55-020", rendered.Subject);
        Assert.DoesNotContain("<ul", rendered.HtmlDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankListItemTemplate_ListIsEmptyAndReported()
    {
        var rendered = Render(new CorrespondenceDraft { BodyHtml = "<p>{{lista}}</p>", ListItemTemplate = "  " });
        Assert.DoesNotContain("<ul", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Equal(["lista"], rendered.EmptyFields);
    }

    [Fact]
    public void ListItemsSeeTheRowOnly()
    {
        var rendered = Render(new CorrespondenceDraft { BodyHtml = "{{lista}}", ListItemTemplate = "[{{lista}}|{{liczba}}|{{zwrot}}]" });
        Assert.Contains("<li>[||Szanowni Państwo]</li>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Equal(["lista", "liczba"], rendered.UnknownFields);
    }

    [Fact]
    public void UnitWithoutRows_LikeABccMessage()
    {
        var rendered = Render(new CorrespondenceDraft
        {
            Subject = "{{zwrot}}",
            BodyHtml = "<p>[{{nazwa}}][{{Miejscowość}}][{{liczba}}]</p>{{lista}}",
            ListItemTemplate = "{{Miejscowość}}",
            Salutation = "Szanowni Państwo Radni",
        }, new CorrespondenceUnit("biuro@rada.pl", string.Empty, []));
        Assert.Equal("Szanowni Państwo Radni", rendered.Subject);
        Assert.Contains("<p>[Szanowni Państwo Radni][][0]</p>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Equal(["Miejscowość", "lista"], rendered.EmptyFields);
    }

    [Fact]
    public void UnknownAndEmpty_SubjectBodyThenList_OncePerKey()
    {
        var rendered = Render(new CorrespondenceDraft
        {
            Subject = "{{Nieznane}} {{telefon}}",
            BodyHtml = "<p>{{Telefon}} {{nieznane}} {{Inne}} {{Telefon|brak}}</p><p>{{lista}}</p>",
            ListItemTemplate = "{{Fax}} {{Telefon}}",
        });
        Assert.Equal(["Nieznane", "Inne", "Fax"], rendered.UnknownFields);
        Assert.Equal(["telefon"], rendered.EmptyFields);
    }

    [Fact]
    public void CustomFields()
    {
        var rendered = Render(new CorrespondenceDraft
        {
            BodyHtml = "<p>Termin: {{Termin}}. Adres: {{Adres}}</p>",
            CustomFields = [new CustomFieldDefinition("Termin", "30 listopada 2026"), new CustomFieldDefinition("Adres", "{{Kod pocztowy}} {{Miejscowość}} <ul>")],
        });
        Assert.Contains("Termin: 30 listopada 2026. Adres: 55-020 Żórawina &lt;ul&gt;", rendered.HtmlDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyReferencedImages_InOrderOfFirstReference_SignatureImagesIncluded()
    {
        var first = Image(0x10);
        var unused = Image(0x20);
        var second = Image(0x30);
        var logo = Image(0x40);
        var draft = new CorrespondenceDraft
        {
            BodyHtml = $"<p><img src=\"cid:{second.ContentId}\" alt=\"b\"> <img src=\"cid:{first.ContentId}\" alt=\"a\"> <img src=\"cid:{second.ContentId}\"></p>"
                + "<p><img src=\"cid:img-0000000000000000@admng\" alt=\"brak\"> <img src=\"https://evil.example/t.gif\"></p>",
            Images = ById(first, unused, second),
            Signature = new CorrespondenceSignature("Jan", $"<p>Jan Kowalski <img src=\"cid:{logo.ContentId}\" alt=\"logo\"></p>", [logo]),
        };
        var rendered = Render(draft);
        Assert.Equal([second.ContentId, first.ContentId, logo.ContentId], rendered.Images.Select(i => i.ContentId));
        Assert.DoesNotContain("img-0000000000000000@admng", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.DoesNotContain(unused.ContentId, rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Equal(rendered.Images.Sum(i => (long)i.Data.Length), rendered.ImageBytes);
        Assert.False(rendered.ExceedsImageLimits);
        Assert.Equal(rendered.HtmlDocument, Resanitized(rendered));
    }

    [Fact]
    public void ImagesKeyedByAnotherIdThanTheirContentId_AreNotReferenced()
    {
        var image = Image(0x50);
        var rendered = Render(new CorrespondenceDraft
        {
            BodyHtml = "<p><img src=\"cid:logo\" alt=\"logo\"></p>",
            Images = new Dictionary<string, MessageImage> { ["logo"] = image },
        });
        Assert.Empty(rendered.Images);
        Assert.DoesNotContain("cid:", rendered.HtmlDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void SignatureIsAppendedAfterTheBody_WithoutMerging()
    {
        var rendered = Render(new CorrespondenceDraft
        {
            BodyHtml = "<p>Treść {{nazwa}}</p>",
            Signature = new CorrespondenceSignature("Podpis", "<p>Z poważaniem<br>Jan Kowalski {{nazwa}}</p>", []),
        });
        var text = CorrespondenceRenderer.PlainText(rendered.HtmlDocument);
        Assert.True(text.IndexOf("Treść Urząd Gminy Żórawina", StringComparison.Ordinal) < text.IndexOf("Z poważaniem", StringComparison.Ordinal));
        Assert.Contains("Jan Kowalski {{nazwa}}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TooManyImages_ExceedTheLimit()
    {
        var images = Enumerable.Range(0, CorrespondenceLimits.MaxImagesPerMessage + 1).Select(i => Image((byte)i, 2 + (i % 7), 2 + (i / 7))).ToArray();
        var body = string.Concat(images.Select(i => $"<img src=\"cid:{i.ContentId}\">"));
        var rendered = Render(new CorrespondenceDraft { BodyHtml = "<p>" + body + "</p>", Images = ById(images) });
        Assert.Equal(CorrespondenceLimits.MaxImagesPerMessage + 1, rendered.Images.Count);
        Assert.True(rendered.ExceedsImageLimits);
    }

    [Fact]
    public void SubjectValuesAreHeaderSafe()
    {
        var rendered = Render(new CorrespondenceDraft { Subject = "Do: {{Miejscowość}}" }, Unit(Row(town: "Żórawina\r\nBcc: x@y.pl")));
        Assert.Equal("Do: Żórawina  Bcc: x@y.pl", rendered.Subject);
    }

    [Fact]
    public void EmptyDraft_IsAnEmptyDocument()
    {
        var rendered = Render(new CorrespondenceDraft());
        Assert.Equal(string.Empty, rendered.Subject);
        Assert.Contains("<body>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Equal(string.Empty, CorrespondenceRenderer.PlainText(rendered.HtmlDocument).Trim());
    }

    [Fact]
    public void NullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => CorrespondenceRenderer.Render(null!, Unit(), Columns));
        Assert.Throws<ArgumentNullException>(() => CorrespondenceRenderer.Render(new CorrespondenceDraft(), null!, Columns));
        Assert.Throws<ArgumentNullException>(() => CorrespondenceRenderer.Render(new CorrespondenceDraft(), Unit(), null!));
    }

    [Fact]
    public void ToStringNeverShowsContent()
    {
        var rendered = Render(new CorrespondenceDraft { Subject = "{{nazwa}}" });
        Assert.Equal(nameof(RenderedCorrespondence), rendered.ToString());
        Assert.Equal(nameof(CorrespondenceDraft), new CorrespondenceDraft { Subject = "x" }.ToString());
        Assert.DoesNotContain("@", Unit().ToString(), StringComparison.Ordinal);
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
