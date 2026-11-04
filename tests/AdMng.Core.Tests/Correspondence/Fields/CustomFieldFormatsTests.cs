using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;

namespace AdMng.Core.Tests.Correspondence.Fields;

/// <summary>Date fields (<see cref="CustomFieldKind.Date"/>): Polish formatting, the default "15 listopada 2026r." and the
/// lookup of <see cref="RecipientFieldSet"/>.</summary>
public class CustomFieldFormatsTests
{
    private static readonly DateOnly Deadline = new(2026, 11, 15);

    [Fact]
    public void DefaultFormat_IsTheDeadlineOfTheMailboxQuery() =>
        Assert.Equal("15 listopada 2026r.", CustomFieldFormats.FormatDate(Deadline, CustomFieldFormats.DefaultDate));

    [Theory]
    [InlineData("d MMMM yyyy 'r.'", "15 listopada 2026 r.")]
    [InlineData("d MMMM yyyy", "15 listopada 2026")]
    [InlineData("dd.MM.yyyy", "15.11.2026")]
    [InlineData("dd.MM.yyyy 'r.'", "15.11.2026 r.")]
    [InlineData("dddd, d MMMM yyyy", "niedziela, 15 listopada 2026")]
    [InlineData("yyyy-MM-dd", "2026-11-15")]
    [InlineData("MMMM yyyy", "listopad 2026")]                 // no day: the nominative
    public void Formats_ArePolish_WithTheGenitiveAfterADay(string format, string expected) =>
        Assert.Equal(expected, CustomFieldFormats.FormatDate(Deadline, format));

    [Fact]
    public void Choices_StartWithTheDefault_AndAllFormat()
    {
        Assert.Equal(CustomFieldFormats.DefaultDate, CustomFieldFormats.DateChoices[0]);
        Assert.Equal(7, CustomFieldFormats.DateChoices.Count);
        Assert.All(CustomFieldFormats.DateChoices, f => Assert.NotEqual(string.Empty, CustomFieldFormats.FormatDate(Deadline, f)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("d MMMM 'r")]          // an unclosed quote
    [InlineData("HH:mm")]              // a time part
    [InlineData("x")]                  // an unknown standard format
    public void BlankOrInvalidFormats_UseTheDefault(string? format) =>
        Assert.Equal("15 listopada 2026r.", CustomFieldFormats.FormatDate(Deadline, format));

    [Fact]
    public void ATooLongFormat_UsesTheDefault() =>
        Assert.Equal("15 listopada 2026r.", CustomFieldFormats.FormatDate(Deadline, new string('d', CustomFieldFormats.MaxFormatLength + 1)));

    [Fact]
    public void Values_AreInvariantDates()
    {
        Assert.Equal("2026-11-15", CustomFieldFormats.ValueOf(Deadline));
        Assert.True(CustomFieldFormats.TryParseValue(" 2026-11-15 ", out var parsed));
        Assert.Equal(Deadline, parsed);
        Assert.False(CustomFieldFormats.TryParseValue("15.11.2026", out _));
        Assert.False(CustomFieldFormats.TryParseValue("", out _));
        Assert.False(CustomFieldFormats.TryParseValue(null, out _));
    }

    [Fact]
    public void DateText_IsEmpty_WithoutAUsableDate()
    {
        Assert.Equal("15.11.2026", CustomFieldFormats.DateText(new CustomFieldDefinition("Termin", "2026-11-15", CustomFieldKind.Date, "dd.MM.yyyy")));
        Assert.Equal("15 listopada 2026r.", CustomFieldFormats.DateText(new CustomFieldDefinition("Termin", "2026-11-15", CustomFieldKind.Date)));
        Assert.Equal(string.Empty, CustomFieldFormats.DateText(new CustomFieldDefinition("Termin", "", CustomFieldKind.Date)));
        Assert.Equal(string.Empty, CustomFieldFormats.DateText(new CustomFieldDefinition("Termin", "jutro", CustomFieldKind.Date)));
    }

    [Fact]
    public void FieldSet_FormatsDateFields_TextFieldsStayText()
    {
        IReadOnlyList<CustomFieldDefinition> custom =
        [
            new("Termin", "2026-11-15", CustomFieldKind.Date),
            new("Termin krótki", "2026-11-15", CustomFieldKind.Date, "dd.MM.yyyy"),
            new("Brak terminu", "", CustomFieldKind.Date),
            new("Opis", "Skrzynka {{Skrzynka}} – do {{Termin}}"),       // text: other custom fields (a date too) inside
        ];
        var set = new RecipientFieldSet("jan.kowalski@contoso.test", "Jan Kowalski",
            new Dictionary<string, string> { ["Skrzynka"] = "Sekretariat Wydziału Łączności" }, "Szanowni Państwo", custom, ["Skrzynka"]);

        Assert.Equal("15 listopada 2026r.", set.Lookup("termin"));
        Assert.Equal("15.11.2026", set.Lookup("Termin krótki"));
        Assert.Equal(string.Empty, set.Lookup("Brak terminu"));      // known and empty: the preview flags it
        Assert.Equal("Skrzynka Sekretariat Wydziału Łączności – do 15 listopada 2026r.", set.Lookup("Opis"));
    }

    [Fact]
    public void Renderer_PutsTheFormattedDateIntoSubjectAndBody()
    {
        var draft = new CorrespondenceDraft
        {
            Subject = "Odpowiedź do {{Termin}}",
            BodyHtml = "<p>Prosimy o udzielenie odpowiedzi do <b>{{Termin}}</b></p>",
            CustomFields = [new CustomFieldDefinition("Termin", "2026-11-15", CustomFieldKind.Date)],
        };

        var rendered = CorrespondenceRenderer.Render(draft, new CorrespondenceUnit("anna.nowak@contoso.test", "Anna Nowak", [new Dictionary<string, string>()]), []);

        Assert.Equal("Odpowiedź do 15 listopada 2026r.", rendered.Subject);
        Assert.Contains("<b>15 listopada 2026r.</b>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Empty(rendered.EmptyFields);
        Assert.Empty(rendered.UnknownFields);
    }

    [Fact]
    public void ADateFieldsValue_NeverListsTheGroup()
    {
        // A date field's value is a date: even a malformed one holding {{lista}} renders "" and never makes the list
        // rendered (whose item would report its unknown field).
        var draft = new CorrespondenceDraft
        {
            Subject = "Skrzynki",
            BodyHtml = "<p>Termin: {{Termin}}</p>",
            CustomFields = [new CustomFieldDefinition("Termin", "{{lista}}", CustomFieldKind.Date)],
            ListItemTemplate = "{{Skrzynka}} {{Nieznane pole}}",
            GroupByAddress = true,
        };
        var unit = new CorrespondenceUnit("jan.kowalski@contoso.test", "Jan Kowalski",
            [new Dictionary<string, string> { ["Skrzynka"] = "Rekrutacja" }]);

        var rendered = CorrespondenceRenderer.Render(draft, unit, ["Skrzynka"]);

        Assert.DoesNotContain("Rekrutacja", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Empty(rendered.UnknownFields);
        Assert.Equal(["Termin"], rendered.EmptyFields);
    }

    [Fact]
    public void Definitions_KeepTheirKindAndFormat_AndOldOnesAreText()
    {
        var old = new CustomFieldDefinition("Termin", "15 listopada");
        Assert.Equal(CustomFieldKind.Text, old.Kind);
        Assert.Null(old.Format);
        Assert.NotEqual(old, old with { Kind = CustomFieldKind.Date });
        Assert.Equal("CustomFieldDefinition Termin (Text)", old.ToString());
    }
}
