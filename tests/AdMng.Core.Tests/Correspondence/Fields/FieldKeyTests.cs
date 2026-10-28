using AdMng.Core.Correspondence.Fields;

namespace AdMng.Core.Tests.Correspondence.Fields;

public class FieldKeyTests
{
    [Theory]
    [InlineData("Kod pocztowy", "kodpocztowy")]
    [InlineData("  KOD   POCZTOWY ", "kodpocztowy")]
    [InlineData("kod-pocztowy", "kodpocztowy")]
    [InlineData("kod_pocztowy", "kodpocztowy")]
    [InlineData("Kod\u00A0pocztowy", "kodpocztowy")]
    [InlineData("Kod\u2011pocztowy", "kodpocztowy")]
    [InlineData("Miejscowość", "miejscowosc")]
    [InlineData("ŻÓŁĆ Gęślą", "zolcgesla")]
    [InlineData("E-mail", "email")]
    [InlineData("Adres e-mail", "adresemail")]
    [InlineData("Nr\u200Bsprawy", "nrsprawy")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" - _ ", "")]
    public void Of_Folds(string? name, string expected) => Assert.Equal(expected, FieldKey.Of(name));

    [Theory]
    [InlineData("Kod pocztowy", "kod-POCZTOWY", true)]
    [InlineData("Miejscowość", "miejscowosc", true)]
    [InlineData("Ulica i nr", "Ulica_i_nr", true)]
    [InlineData("Telefon", "Telefon 2", false)]
    [InlineData(null, "", true)]
    public void Same_ComparesKeys(string? a, string? b, bool expected) => Assert.Equal(expected, FieldKey.Same(a, b));
}

public class BuiltInFieldsTests
{
    [Fact]
    public void All_InOrder() => Assert.Equal(["zwrot", "nazwa", "email", "lista", "liczba"], BuiltInFields.All);

    [Theory]
    [InlineData("Nazwa", true)]
    [InlineData("NAME", true)]
    [InlineData("E-mail", true)]
    [InlineData("e_mail", true)]
    [InlineData("Mail", true)]
    [InlineData("Adres e-mail", true)]
    [InlineData("adres email", true)]
    [InlineData("Zwrot", true)]
    [InlineData("Nazwa skrócona", false)]
    [InlineData("Inne e-maile", false)]
    [InlineData("Lista", false)]
    [InlineData("Miejscowość", false)]
    public void IsBuiltInColumn_HidesDuplicatesOfBuiltIns(string column, bool expected) =>
        Assert.Equal(expected, BuiltInFields.IsBuiltInColumn(column));
}
