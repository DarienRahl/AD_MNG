using AdMng.Core.Correspondence.Composition;

namespace AdMng.Core.Tests.Correspondence.Composition;

public class RecipientGroupingTests
{
    private static (string Email, string Name, IReadOnlyDictionary<string, string> Fields) Row(string email, string name, string mailbox) =>
        (email, name, new Dictionary<string, string> { ["Skrzynka"] = mailbox });

    private static readonly (string Email, string Name, IReadOnlyDictionary<string, string> Fields)[] Rows =
    [
        Row("jan.kowalski@contoso.test", "", "Sekretariat IT"),
        Row("anna.nowak@contoso.test", "Anna Nowak", "Biuro projektu"),
        Row("JAN.KOWALSKI@contoso.test ", "Jan Kowalski", "Archiwum"),
        Row("mailto:jan.kowalski@contoso.test", "Jan K.", "Konferencja 2026"),
        Row("", "Bez adresu", "Pomoc techniczna"),
        Row("  ", "Też bez adresu", "Stara skrzynka"),
    ];

    [Fact]
    public void WithoutGrouping_OneUnitPerRow_InOrder()
    {
        var units = RecipientGrouping.Group(Rows, byAddress: false);
        Assert.Equal(6, units.Count);
        Assert.All(units, u => Assert.Single(u.Rows));
        Assert.Equal(["Sekretariat IT", "Biuro projektu", "Archiwum", "Konferencja 2026", "Pomoc techniczna", "Stara skrzynka"], units.Select(u => u.Rows[0]["Skrzynka"]));
        Assert.Equal("JAN.KOWALSKI@contoso.test ", units[2].Email);
    }

    [Fact]
    public void ByAddress_FirstAppearanceOrder_NormalizedAddresses_FirstNonEmptyName()
    {
        var units = RecipientGrouping.Group(Rows, byAddress: true);
        Assert.Equal(4, units.Count);
        var jan = units[0];
        Assert.Equal("jan.kowalski@contoso.test", jan.Email);
        Assert.Equal("Jan Kowalski", jan.Name);
        Assert.Equal(["Sekretariat IT", "Archiwum", "Konferencja 2026"], jan.Rows.Select(r => r["Skrzynka"]));
        Assert.Equal(("anna.nowak@contoso.test", "Anna Nowak"), (units[1].Email, units[1].Name));
        Assert.Equal(["Pomoc techniczna"], units[2].Rows.Select(r => r["Skrzynka"]));   // rows without an address are never grouped
        Assert.Equal(["Stara skrzynka"], units[3].Rows.Select(r => r["Skrzynka"]));
    }

    [Fact]
    public void NullFieldsAreAnEmptyRow()
    {
        var unit = Assert.Single(RecipientGrouping.Group([("a@b.pl", "A", null!)], byAddress: true));
        Assert.Empty(Assert.Single(unit.Rows));
    }

    [Fact]
    public void Empty() => Assert.Empty(RecipientGrouping.Group([], byAddress: true));
}
