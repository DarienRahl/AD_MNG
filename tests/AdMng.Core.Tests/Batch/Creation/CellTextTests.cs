using AdMng.Core.Batch.Creation;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>Cell rules of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.1, 2.11).</summary>
public sealed class CellTextTests
{
    // Escapes, not the characters (editors strip them silently).
    private const string Nbsp = "\u00A0";
    private const string FigureSpace = "\u2007";
    private const string NarrowNbsp = "\u202F";
    private const string ZeroWidth = "\u200B";
    private const string WordJoiner = "\u2060";

    [Theory]
    [InlineData("Łucja", "Łucja")]
    [InlineData("  Łucja  Żak  ", "Łucja Żak")]
    [InlineData("Anna" + Nbsp + "Maria", "Anna Maria")]
    [InlineData("12" + NarrowNbsp + "345", "12 345")]
    [InlineData("12" + FigureSpace + "346", "12 346")]
    [InlineData("Kowal" + ZeroWidth + "ski", "Kowalski")]
    [InlineData("\uFEFFImię", "Imię")]
    [InlineData("Ż" + WordJoiner + "ak", "Żak")]
    [InlineData("a\u200Cb\u200Dc", "abc")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normalize_RemovesInvisibleCharacters_AndCollapsesSpaces(string value, string expected) =>
        Assert.Equal(expected, CellText.Normalize(value));

    [Theory]
    [InlineData("ul. Kwiatowa 5\n00-950 Warszawa", "ul. Kwiatowa 5\r\n00-950 Warszawa")]
    [InlineData("ul. Kwiatowa 5\r00-950 Warszawa", "ul. Kwiatowa 5\r\n00-950 Warszawa")]
    [InlineData("ul. Kwiatowa 5\r\n00-950 Warszawa", "ul. Kwiatowa 5\r\n00-950 Warszawa")]
    [InlineData("Ewa \n Katarzyna", "Ewa\r\nKatarzyna")]
    [InlineData("\nEwa\n", "Ewa")]
    [InlineData("a\u2028b", "a\r\nb")]
    public void Normalize_MakesLineBreaksCrLf(string value, string expected) => Assert.Equal(expected, CellText.Normalize(value));

    [Theory]
    [InlineData("#N/D!")]
    [InlineData("#N/D")]
    [InlineData("#ARG!")]
    [InlineData("#NAZWA?")]
    [InlineData("#DZIEL/0!")]
    [InlineData("#ADR!")]
    [InlineData("#LICZBA!")]
    [InlineData("#ZERO!")]
    [InlineData("#PUSTE!")]
    [InlineData("#N/A")]
    [InlineData("#VALUE!")]
    [InlineData("#REF!")]
    [InlineData("#NAME?")]
    [InlineData("#DIV/0!")]
    [InlineData("#NUM!")]
    [InlineData("#NULL!")]
    [InlineData("#SPILL!")]
    [InlineData("#CALC!")]
    [InlineData(" #n/a ")]
    public void IsSpreadsheetError_KnowsBothLanguages(string value) => Assert.True(CellText.IsSpreadsheetError(value));

    [Theory]
    [InlineData("#")]
    [InlineData("#1")]
    [InlineData("N/A")]
    [InlineData("#Kadry")]
    [InlineData("Łucja")]
    [InlineData("")]
    public void IsSpreadsheetError_IgnoresOrdinaryText(string value) => Assert.False(CellText.IsSpreadsheetError(value));

    [Theory]
    [InlineData("4,86E+11", true)]
    [InlineData("4.86E+11", true)]
    [InlineData("4,86e+11", true)]
    [InlineData("-1,2E-05", true)]
    [InlineData("+5E3", true)]
    [InlineData(" 4,86E+11 ", true)]
    [InlineData("486001002001", false)]
    [InlineData("4,86E", false)]
    [InlineData("E+11", false)]
    [InlineData("44,86E+11", false)]
    [InlineData("4,E+11", false)]
    [InlineData("Ewa", false)]
    public void IsScientificNotation_MatchesExcelsDisplay(string value, bool expected) => Assert.Equal(expected, CellText.IsScientificNotation(value));

    [Theory]
    [InlineData("lucja.zak@contoso.com", true)]
    [InlineData(" lucja.zak@contoso.com ", true)]
    [InlineData("zofia.slezak-nowinska@contoso.com", true)]
    [InlineData("jan@localhost", false)]
    [InlineData("Łucja Żak <lucja.zak@contoso.com>", false)]
    [InlineData("a@b@contoso.com", false)]
    [InlineData("E-mail", false)]
    public void LooksLikeEmail(string value, bool expected) => Assert.Equal(expected, CellText.LooksLikeEmail(value));

    [Theory]
    [InlineData("+48 600 100 201", true)]
    [InlineData("600 100 202", true)]
    [InlineData("48600100203", true)]
    [InlineData("+48 (22) 555-01-01", true)]
    [InlineData("22 555 01 01", true)]
    [InlineData("12345", false)]
    [InlineData("600 100 20a", false)]
    [InlineData("2026-11-09", false)]
    [InlineData("09.11.2026", false)]
    [InlineData("4,86E+11", false)]
    [InlineData("1234567890123456", false)]
    [InlineData("600+100", false)]
    public void LooksLikePhone(string value, bool expected) => Assert.Equal(expected, CellText.LooksLikePhone(value));

    [Theory]
    [InlineData("600100200", true)]
    [InlineData("+48 600 100 200", true)]
    [InlineData("48 600 100 200", true)]
    [InlineData("0048 600-100-200", true)]
    [InlineData("+48 512 345 678", true)]
    [InlineData("+48 450 000 000", true)]
    [InlineData("+48 880 000 000", true)]
    [InlineData("+48 22 555 01 01", false)]
    [InlineData("+48 600 100 20", false)]
    [InlineData("+49 600 100 200", false)]
    [InlineData("4860010020", false)]
    [InlineData("Ewa", false)]
    public void LooksLikePolishMobile(string value, bool expected) => Assert.Equal(expected, CellText.LooksLikePolishMobile(value));

    [Theory]
    [InlineData("KOWALSKI", true)]
    [InlineData("ŻÓŁKIEWSKA", true)]
    [InlineData("NOWAK-KOWALSKA", true)]
    [InlineData("JAN K.", true)]
    [InlineData("Kowalski", false)]
    [InlineData("A", false)]
    [InlineData("A1", false)]
    [InlineData("123", false)]
    [InlineData("", false)]
    public void IsAllCaps(string value, bool expected) => Assert.Equal(expected, CellText.IsAllCaps(value));

    [Theory]
    [InlineData("NOWAK-KOWALSKA", TextCase.Title, "Nowak-Kowalska")]
    [InlineData("łucja żak", TextCase.Title, "Łucja Żak")]
    [InlineData("ŻAK-O'NEILL", TextCase.Title, "Żak-O'Neill")]
    [InlineData("anna maria ŚLĘZAK", TextCase.Title, "Anna Maria Ślęzak")]
    [InlineData("Łucja Żak", TextCase.Lower, "łucja żak")]
    [InlineData("Łucja Żak", TextCase.Upper, "ŁUCJA ŻAK")]
    [InlineData("ŁUCJA żak", TextCase.AsIs, "ŁUCJA żak")]
    public void ApplyCase_UsesPolishCasing(string value, TextCase textCase, string expected) => Assert.Equal(expected, CellText.ApplyCase(value, textCase));

    [Fact]
    public void SplitValues_SplitsOnPipeOnly()
    {
        Assert.Equal(["+48 22 555 01 01", "+48 22 555 01 02"], CellText.SplitValues("+48 22 555 01 01 | +48 22 555 01 02"));
        Assert.Equal(["ul. Długa 1\r\n00-950 Warszawa"], CellText.SplitValues("ul. Długa 1\r\n00-950 Warszawa"));
        Assert.Equal(["a;b", "c,d"], CellText.SplitValues("a;b|c,d||"));
        Assert.Empty(CellText.SplitValues(" | "));
    }

    [Fact]
    public void SplitList_SplitsOnSemicolonPipeAndLineBreaks()
    {
        Assert.Equal(["GG_Nowi", "DL_Drukarki", "UG_Projekty", "GG_Księgowość"], CellText.SplitList("GG_Nowi; DL_Drukarki|UG_Projekty\r\nGG_Księgowość"));
        Assert.Equal(["GG_Nowi, DL_Drukarki"], CellText.SplitList("GG_Nowi, DL_Drukarki"));
        Assert.Empty(CellText.SplitList(";;\r\n"));
    }

    [Fact]
    public void SplitCommas_SplitsOnCommas() =>
        Assert.Equal(["GG_Nowi", "DL_Drukarki"], CellText.SplitCommas(" GG_Nowi , DL_Drukarki ,"));

    [Theory]
    [InlineData("Jan Kowalski", NameOrder.GivenNameFirst, "Jan", "Kowalski")]
    [InlineData("Anna Maria Nowak-Kowalska", NameOrder.GivenNameFirst, "Anna Maria", "Nowak-Kowalska")]
    [InlineData("dr hab. Anna Maria Nowak-Kowalska", NameOrder.GivenNameFirst, "Anna Maria", "Nowak-Kowalska")]
    [InlineData("prof. dr hab. Józef Ćwikła", NameOrder.GivenNameFirst, "Józef", "Ćwikła")]
    [InlineData("mgr inż. Łucja Żak", NameOrder.GivenNameFirst, "Łucja", "Żak")]
    [InlineData("Kowalski Jan", NameOrder.SurnameFirst, "Jan", "Kowalski")]
    [InlineData("Nowak-Kowalska Anna Maria", NameOrder.SurnameFirst, "Anna Maria", "Nowak-Kowalska")]
    [InlineData("Kowalski, Jan", NameOrder.SurnameFirst, "Jan", "Kowalski")]
    [InlineData("dr Ślęzak Zofia", NameOrder.SurnameFirst, "Zofia", "Ślęzak")]
    [InlineData("Brzęczyszczykiewicz", NameOrder.GivenNameFirst, "", "Brzęczyszczykiewicz")]
    [InlineData("Brzęczyszczykiewicz", NameOrder.SurnameFirst, "", "Brzęczyszczykiewicz")]
    [InlineData("  Łucja   Żak  ", NameOrder.GivenNameFirst, "Łucja", "Żak")]
    [InlineData("", NameOrder.GivenNameFirst, "", "")]
    public void SplitFullName_RemovesTitles_InBothOrders(string value, NameOrder order, string givenName, string surname)
    {
        var (given, sn) = CellText.SplitFullName(value, order);

        Assert.Equal(givenName, given);
        Assert.Equal(surname, sn);
    }

    [Theory]
    [InlineData("+48 600 100 200", "+48 600 100 200")]
    [InlineData("600 100 200", "+48 600 100 200")]
    [InlineData("600100200", "+48 600 100 200")]
    [InlineData("48600100200", "+48 600 100 200")]
    [InlineData("0048 600 100 200", "+48 600 100 200")]
    [InlineData("+48 600-100-200", "+48 600 100 200")]
    [InlineData("+48 (600) 100.200", "+48 600 100 200")]
    [InlineData("22 555 01 01", "+48 22 555 01 01")]
    [InlineData("+48 (22) 555-01-01", "+48 22 555 01 01")]
    [InlineData("+49 30 1234567", "+49301234567")]
    [InlineData("0049 30 1234567", "+49301234567")]
    [InlineData("+1 (202) 555-0100", "+12025550100")]
    public void NormalizePhone_International(string value, string expected) => Assert.Equal(expected, CellText.NormalizePhone(value, PhoneFormat.International));

    [Theory]
    [InlineData("600 100 200 wew. 12")]
    [InlineData("12345")]
    [InlineData("4,86E+11")]
    [InlineData("6001002001")]
    [InlineData("600/100/200")]
    [InlineData("brak")]
    [InlineData("")]
    public void NormalizePhone_International_LeavesWhatItCannotFormat(string value) => Assert.Equal(value, CellText.NormalizePhone(value, PhoneFormat.International));

    [Fact]
    public void NormalizePhone_AsIs_KeepsTheValue() => Assert.Equal("600 100 200", CellText.NormalizePhone("600 100 200", PhoneFormat.AsIs));

    [Fact]
    public void Members_RejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => CellText.Normalize(null!));
        Assert.Throws<ArgumentNullException>(() => CellText.SplitFullName(null!, NameOrder.GivenNameFirst));
        Assert.Throws<ArgumentNullException>(() => CellText.NormalizePhone(null!, PhoneFormat.International));
        Assert.Throws<ArgumentNullException>(() => CellText.ApplyCase(null!, TextCase.Title));
    }
}
