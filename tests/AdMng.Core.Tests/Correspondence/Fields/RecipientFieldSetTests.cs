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
    public void CustomFields_CannotUseOtherCustomFields()
    {
        var set = Set(custom: [new CustomFieldDefinition("A", "[{{B}}]"), new CustomFieldDefinition("B", "b"), new CustomFieldDefinition("C", "{{C}}")]);
        Assert.Equal("[]", set.Lookup("A"));
        Assert.Equal(string.Empty, set.Lookup("C"));   // no recursion
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
