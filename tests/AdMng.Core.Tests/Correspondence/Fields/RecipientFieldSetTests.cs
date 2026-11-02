using AdMng.Core.Correspondence.Fields;

namespace AdMng.Core.Tests.Correspondence.Fields;

public class RecipientFieldSetTests
{
    private static readonly Dictionary<string, string> Row = new(StringComparer.Ordinal)
    {
        ["Nazwa"] = "Urząd Gminy Żórawina",
        ["E-mail"] = "ug@zorawina.pl",
        ["Miejscowość"] = "Żórawina",
        ["Kod pocztowy"] = "55-020",
        ["Telefon"] = "",
        ["Liczba"] = "7",
    };

    private static readonly string[] Known = ["Nazwa", "E-mail", "Miejscowość", "Kod pocztowy", "Telefon", "Liczba", "Fax"];

    private static RecipientFieldSet Set(
        string name = "Urząd Gminy Żórawina", string salutation = "Szanowni Państwo", IReadOnlyList<CustomFieldDefinition>? custom = null,
        string? list = null, int? count = null, IReadOnlyDictionary<string, string>? row = null) =>
        new("ug@zorawina.pl", name, row ?? Row, salutation, custom ?? [], Known, list, count);

    [Theory]
    [InlineData("zwrot", "Szanowna Pani Wójt")]
    [InlineData("ZWROT", "Szanowna Pani Wójt")]
    [InlineData("nazwa", "Urząd Gminy Żórawina")]
    [InlineData("name", "Urząd Gminy Żórawina")]
    [InlineData("email", "ug@zorawina.pl")]
    [InlineData("e-mail", "ug@zorawina.pl")]
    [InlineData("mail", "ug@zorawina.pl")]
    [InlineData("Adres e-mail", "ug@zorawina.pl")]
    [InlineData("miejscowosc", "Żórawina")]
    [InlineData("Kod-Pocztowy", "55-020")]
    [InlineData("Telefon", "")]
    [InlineData("Fax", "")]
    public void Lookup_BuiltInsThenColumns(string field, string expected) =>
        Assert.Equal(expected, Set(salutation: "  Szanowna Pani Wójt ").Lookup(field));

    [Theory]
    [InlineData("Nieznane")]
    [InlineData("")]
    [InlineData(" - ")]
    public void Lookup_UnknownIsNull(string field) => Assert.Null(Set().Lookup(field));

    [Fact]
    public void BlankSalutation_IsTheDefault() => Assert.Equal(BuiltInFields.DefaultSalutation, Set(salutation: "  ").Lookup("zwrot"));

    [Fact]
    public void Nazwa_WithoutAName_IsTheSalutation()
    {
        Assert.Equal("Szanowni Państwo", Set(name: " ").Lookup("nazwa"));
        Assert.Equal("Dzień dobry", Set(name: "", salutation: "Dzień dobry").Lookup("Name"));
    }

    [Fact]
    public void GroupValues_WhenGrouped()
    {
        var set = Set(list: "<ul><li>a</li></ul>", count: 3);
        Assert.Equal("<ul><li>a</li></ul>", set.Lookup("lista"));
        Assert.Equal("3", set.Lookup("liczba"));
    }

    [Fact]
    public void GroupValues_WhenNotGrouped_FallThroughToColumns()
    {
        var set = Set();
        Assert.Null(set.Lookup("lista"));
        Assert.Equal("7", set.Lookup("liczba"));   // a column called "Liczba"
    }

    [Fact]
    public void CustomFields_MergedWithTheOtherFields_AsText()
    {
        var set = Set(custom:
        [
            new CustomFieldDefinition("Adres", "{{Kod pocztowy}} {{Miejscowość}}"),
            new CustomFieldDefinition("Termin", "15 listopada 2026 r."),
            new CustomFieldDefinition("Telefon", "{{Telefon|brak telefonu}}"),
            new CustomFieldDefinition("Znaki", "<b>&</b> {{nazwa}}"),
        ]);
        Assert.Equal("55-020 Żórawina", set.Lookup("adres"));
        Assert.Equal("15 listopada 2026 r.", set.Lookup("Termin"));
        Assert.Equal("brak telefonu", set.Lookup("Telefon"));   // custom fields come before columns
        Assert.Equal("<b>&</b> Urząd Gminy Żórawina", set.Lookup("Znaki"));   // encoded once, by the outer merge
        Assert.Equal("&lt;b&gt;&amp;&lt;/b&gt; Urząd Gminy Żórawina", FieldTemplate.Merge("{{Znaki}}", set.Lookup, FieldEncoding.Html));
    }

    [Fact]
    public void CustomFields_GetTheListAsText_NeverItsMarkup()
    {
        var set = new RecipientFieldSet("ug@zorawina.pl", "Urząd Gminy Żórawina", Row, "Szanowni Państwo",
            [new CustomFieldDefinition("Wykaz", "Lista: {{lista}} ({{liczba}})")], Known,
            list: "<ul style=\"margin:0 0 8pt 0\"><li>Łódź</li><li>Żórawina</li></ul>", count: 2, listText: "Łódź, Żórawina");
        Assert.Equal("Lista: Łódź, Żórawina (2)", set.Lookup("wykaz"));
        Assert.Equal("Lista: Łódź, Żórawina (2)", FieldTemplate.Merge("{{Wykaz}}", set.Lookup, FieldEncoding.Html));
        Assert.StartsWith("<ul", set.Lookup("lista"), StringComparison.Ordinal);   // the body's own {{lista}} stays HTML
        Assert.Equal("Lista:  (3)", Set(custom: [new CustomFieldDefinition("Wykaz", "Lista: {{lista}} ({{liczba}})")], list: "<ul></ul>", count: 3)
            .Lookup("Wykaz"));   // no text given: empty, not markup
    }

    [Fact]
    public void CustomFields_UseOtherCustomFields_AChainOfThem()
    {
        var set = Set(custom:
        [
            new CustomFieldDefinition("Link do systemu zgłoszeń", "https://{{System zgłoszeń}}"),
            new CustomFieldDefinition("System zgłoszeń", "pomoc.example.test"),
            new CustomFieldDefinition("Opis", "{{Link do systemu zgłoszeń}} – {{nazwa}}"),
            new CustomFieldDefinition("Termin", "2026-11-15", CustomFieldKind.Date),
            new CustomFieldDefinition("Do kiedy", "do {{Termin}}"),
        ]);
        Assert.Equal("https://pomoc.example.test", set.Lookup("link do systemu zgloszen"));
        Assert.Equal("https://pomoc.example.test – Urząd Gminy Żórawina", set.Lookup("Opis"));
        Assert.Equal("do 15 listopada 2026r.", set.Lookup("Do kiedy"));
        Assert.Equal("<a href=\"https://pomoc.example.test\">pomoc.example.test</a>",
            FieldTemplate.Merge("<a href=\"{{Link do systemu zgłoszeń}}\">{{System zgłoszeń}}</a>", set.Lookup, FieldEncoding.Html));
    }

    [Fact]
    public void CustomFields_ALoopIsEmptyThere_NeverEndless()
    {
        var set = Set(custom:
        [
            new CustomFieldDefinition("A", "[{{B}}]"),
            new CustomFieldDefinition("B", "b{{A}}"),
            new CustomFieldDefinition("C", "{{C}}"),
        ]);
        Assert.Equal("[b]", set.Lookup("A"));
        Assert.Equal(string.Empty, set.Lookup("C"));
        Assert.Equal("b[]", Set(custom: [new CustomFieldDefinition("A", "[{{B}}]"), new CustomFieldDefinition("B", "b{{A}}")]).Lookup("B"));   // where the merge starts decides
    }

    [Fact]
    public void CustomFields_NestedDeeperThanTheLimit_AreEmptyThere()
    {
        var chain = Enumerable.Range(0, RecipientFieldSet.MaxCustomDepth + 2)
            .Select(i => new CustomFieldDefinition("P" + i, i == RecipientFieldSet.MaxCustomDepth + 1 ? "koniec" : "{{P" + (i + 1) + "}}"))
            .ToList();
        Assert.Equal(string.Empty, Set(custom: chain).Lookup("P0"));
        Assert.Equal("koniec", Set(custom: chain).Lookup("P2"));
    }

    [Fact]
    public void CustomFields_UsingAnotherOne_GetTheListAsText()
    {
        var set = new RecipientFieldSet("ug@zorawina.pl", "Urząd Gminy Żórawina", Row, "Szanowni Państwo",
            [new CustomFieldDefinition("Wykaz", "Lista: {{lista}}"), new CustomFieldDefinition("Wstęp", "{{Wykaz}}.")], Known,
            list: "<ul><li>Łódź</li></ul>", count: 1, listText: "Łódź");
        Assert.Equal("Lista: Łódź.", set.Lookup("Wstęp"));
    }

    [Fact]
    public void CustomFields_BuiltInsWin_FirstDefinitionWins_BlankNamesIgnored()
    {
        var set = Set(custom:
        [
            new CustomFieldDefinition("zwrot", "nadpisany"),
            new CustomFieldDefinition("Termin", "pierwszy"),
            new CustomFieldDefinition("termin", "drugi"),
            new CustomFieldDefinition("  ", "pusty"),
        ]);
        Assert.Equal("Szanowni Państwo", set.Lookup("zwrot"));
        Assert.Equal("pierwszy", set.Lookup("TERMIN"));
        Assert.Null(set.Lookup(" "));
    }

    [Fact]
    public void CustomFields_EmptyValueIsKnownAndEmpty()
    {
        var empty = new List<string>();
        var set = Set(custom: [new CustomFieldDefinition("Termin", "")]);
        Assert.Equal(string.Empty, FieldTemplate.Merge("{{Termin}}", set.Lookup, FieldEncoding.Text, empty: empty));
        Assert.Equal(["Termin"], empty);
    }

    [Fact]
    public void RepeatedColumnKeys_FirstWins()
    {
        var row = new Dictionary<string, string> { ["Kod pocztowy"] = "pierwszy", ["kod-pocztowy"] = "drugi" };
        Assert.Equal("pierwszy", Set(row: row).Lookup("KOD POCZTOWY"));
    }

    [Fact]
    public void ToString_NeverShowsValues() => Assert.Equal(nameof(RecipientFieldSet), Set().ToString());
}
