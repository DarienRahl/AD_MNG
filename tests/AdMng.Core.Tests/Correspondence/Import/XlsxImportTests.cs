using System.Text;
using AdMng.Core.Correspondence.Import;
using static AdMng.Core.Tests.Correspondence.Import.ImportTestFiles;

namespace AdMng.Core.Tests.Correspondence.Import;

public class XlsxImportTests
{
    private const string Shared =
        "<si><t>Nazwa</t></si>"
        + "<si><t>E-mail</t></si>"
        + "<si><r><t xml:space=\"preserve\">Urząd </t></r><r><rPr><b/></rPr><t>Gminy Łęczyca</t></r><rPh sb=\"0\" eb=\"1\"><t>ルビ</t></rPh></si>"
        + "<si><t>Telefon</t></si>"
        + "<si><t>Aktywny</t></si>";

    private static ContactImportResult Read(byte[] xlsx, string name = "kontakty.xlsx") => ContactFileReader.Read(name, xlsx, 1250);

    [Fact]
    public void CellsArePlacedByReference_SharedRichInlineNumbersBooleans()
    {
        var sheet =
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"C1\" t=\"s\"><v>1</v></c><c r=\"D1\" t=\"s\"><v>3</v></c><c r=\"E1\" t=\"s\"><v>4</v></c></row>"
            + "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"C2\" t=\"inlineStr\"><is><t>UG@Leczyca.pl</t></is></c><c r=\"D2\"><v>48123456789</v></c><c r=\"E2\" t=\"b\"><v>1</v></c></row>"
            + "<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><r><t>Staro</t></r><r><t>stwo Żagań</t></r></is></c><c r=\"C3\" t=\"str\"><f>LOWER(\"SP@ZAGAN.PL\")</f><v>sp@zagan.pl</v></c><c r=\"E3\" t=\"b\"><v>0</v></c></row>";
        var result = Read(Xlsx(Shared, ("Arkusz1", sheet)));
        Assert.Equal(ContactSourceFormat.Excel, result.Format);
        Assert.Equal(["Nazwa", "E-mail", "Telefon", "Aktywny"], result.Columns);
        Assert.Equal(["ug@leczyca.pl", "sp@zagan.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal(["Urząd Gminy Łęczyca", "Starostwo Żagań"], result.Contacts.Select(c => c.Name));
        Assert.Equal("48123456789", result.Contacts[0].Fields["Telefon"]);
        Assert.Equal("TRUE", result.Contacts[0].Fields["Aktywny"]);
        Assert.Equal("FALSE", result.Contacts[1].Fields["Aktywny"]);
        Assert.Equal(string.Empty, result.Contacts[1].Fields["Telefon"]);
    }

    [Fact]
    public void CellsWithoutReference_FollowThePreviousCell()
    {
        var sheet = "<row><c t=\"inlineStr\"><is><t>Nazwa</t></is></c><c t=\"inlineStr\"><is><t>E-mail</t></is></c></row>"
            + "<row><c t=\"inlineStr\"><is><t>Gmina Łask</t></is></c><c t=\"inlineStr\"><is><t>gmina@lask.pl</t></is></c></row>";
        Assert.Equal("Gmina Łask", Assert.Single(Read(Xlsx(null, ("A", sheet))).Contacts).Name);
    }

    [Fact]
    public void GapsBeforeTheFirstCell_KeepTheColumns()
    {
        var sheet = InlineRow(3, ("B3", "Nazwa"), ("D3", "E-mail")) + InlineRow(4, ("B4", "Gmina Łapy"), ("D4", "gmina@lapy.pl"));
        var result = Read(Xlsx(null, ("A", sheet)));
        Assert.Equal(["Nazwa", "E-mail"], result.Columns);
        Assert.Equal("Gmina Łapy", Assert.Single(result.Contacts).Name);
    }

    [Fact]
    public void EachSheetHasItsOwnHeader_SheetsWithoutAnEmailColumnAreIgnored()
    {
        var notes = InlineRow(1, ("A1", "Notatki")) + InlineRow(2, ("A2", "Zapytać prywatny@adres.pl o termin"));
        var offices = InlineRow(1, ("A1", "Urząd"), ("B1", "E-mail")) + InlineRow(2, ("A2", "Urząd Gminy Ęlk"), ("B2", "ug@elk.pl"));
        var schools = InlineRow(1, ("A1", "Adres e-mail"), ("B1", "Szkoła"), ("C1", "Dyrektor"))
            + InlineRow(2, ("A2", "sp1@elk.pl"), ("B2", "Szkoła Podstawowa nr 1"), ("C2", "Zofia Żak"))
            + InlineRow(3, ("A3", "UG@ELK.PL"), ("B3", "duplikat"), ("C3", ""));
        var result = Read(Xlsx(null, ("Notatki", notes), ("Urzędy", offices), ("Szkoły", schools)));
        Assert.Equal(["ug@elk.pl", "sp1@elk.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal(["Urząd", "E-mail", "Adres e-mail", "Szkoła", "Dyrektor"], result.Columns);
        Assert.Equal("Zofia Żak", result.Contacts[1].Fields["Dyrektor"]);
        Assert.False(result.Contacts[0].Fields.ContainsKey("Dyrektor"));
        Assert.Equal(1, result.Duplicates);
    }

    [Fact]
    public void NoSheetWithAnEmailColumn_EveryCellIsFreeText()
    {
        var sheet = InlineRow(1, ("A1", "Kontakty")) + InlineRow(2, ("A2", "Jan Kowalski <jan@urzad.pl>"), ("B2", "anna@urzad.pl"));
        var result = Read(Xlsx(null, ("A", sheet)));
        Assert.Equal(ContactSourceFormat.Excel, result.Format);
        Assert.Empty(result.Columns);
        Assert.Equal(["jan@urzad.pl", "anna@urzad.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal("Jan Kowalski", result.Contacts[0].Name);
    }

    [Fact]
    public void MergedTitleNamingEmailAboveTheHeader_IsNotTheHeader()
    {
        var sheet = InlineRow(1, ("A1", "Wykaz adresów e-mail jednostek samorządu"))
            + InlineRow(2, ("A2", "Nazwa"), ("B2", "E-mail"), ("C2", "Kod pocztowy"), ("D2", "Miejscowość"))
            + InlineRow(3, ("A3", "Gmina Łęczyca"), ("B3", "ug@leczyca.pl"), ("C3", "99-100"), ("D3", "Łęczyca"))
            + InlineRow(4, ("A4", "Gmina Żychlin"), ("B4", "ug@zychlin.pl"), ("C4", "99-320"), ("D4", "Żychlin"))
            + InlineRow(5, ("A5", "Gmina Śleszyn"), ("B5", "ug@sleszyn.pl"), ("C5", "99-307"), ("D5", "Śleszyn"));
        var result = Read(Xlsx(null, ("Wykaz", sheet)));
        Assert.Equal(["Nazwa", "E-mail", "Kod pocztowy", "Miejscowość"], result.Columns);
        Assert.Equal(["ug@leczyca.pl", "ug@zychlin.pl", "ug@sleszyn.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal("99-307", result.Contacts[2].Fields["Kod pocztowy"]);
        Assert.Equal(0, result.InvalidAddresses);
    }

    [Fact]
    public void SheetWhoseEmailColumnHoldsNoAddress_IsFreeText_NextToTheTablesOfOtherSheets()
    {
        var offices = InlineRow(1, ("A1", "Urząd"), ("B1", "E-mail")) + InlineRow(2, ("A2", "Urząd Gminy Ełk"), ("B2", "ug@elk.pl"));
        var posts = InlineRow(1, ("A1", "Nazwa"), ("B1", "Poczta"), ("C1", "Kontakt"))
            + InlineRow(2, ("A2", "Sołectwo Łoje"), ("B2", "Ełk"), ("C2", "soltys@loje.pl"));
        var result = Read(Xlsx(null, ("Urzędy", offices), ("Sołectwa", posts)));
        Assert.Equal(["ug@elk.pl", "soltys@loje.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal(["Urząd", "E-mail"], result.Columns);
        Assert.Equal(1, result.InvalidAddresses);
    }

    private const string Styles =
        "<styleSheet xmlns=\"" + SpreadsheetNs + "\"><numFmts count=\"5\">"
        + "<numFmt numFmtId=\"164\" formatCode=\"00\\-000\"/>"
        + "<numFmt numFmtId=\"165\" formatCode=\"[$-415]d\\ mmmm\\ yyyy;@\"/>"
        + "<numFmt numFmtId=\"166\" formatCode=\"00000000000\"/>"
        + "<numFmt numFmtId=\"167\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm\"/>"
        + "<numFmt numFmtId=\"168\" formatCode=\"#,##0.00\\ &quot;zł&quot;\"/>"
        + "<numFmt numFmtId=\"169\" formatCode=\"[h]:mm\"/></numFmts>"
        + "<cellStyleXfs count=\"1\"><xf numFmtId=\"14\"/></cellStyleXfs>"
        + "<cellXfs count=\"9\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\" applyNumberFormat=\"1\"/><xf numFmtId=\"164\"/><xf numFmtId=\"165\"/>"
        + "<xf numFmtId=\"166\"/><xf numFmtId=\"167\"/><xf numFmtId=\"168\"/><xf numFmtId=\"20\"/><xf numFmtId=\"169\"/></cellXfs></styleSheet>";

    private static byte[] StyledWorkbook(string sheetData, bool date1904 = false)
    {
        var workbook = $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"{RelationshipsNs}\">"
            + (date1904 ? "<workbookPr date1904=\"1\"/>" : "<workbookPr/>")
            + "<sheets><sheet name=\"Umowy\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
        var rels = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
            + "</Relationships>";
        return Zip(("xl/workbook.xml", workbook), ("xl/_rels/workbook.xml.rels", rels), ("xl/styles.xml", Styles), ("xl/worksheets/sheet1.xml", Sheet(sheetData)));
    }

    private static string Number(string reference, string value, int? style = null) =>
        $"<c r=\"{reference}\"" + (style is { } s ? $" s=\"{s}\"" : string.Empty) + $"><v>{value}</v></c>";

    [Fact]
    public void DatesAndDigitPatterns_AreWhatExcelShows_OtherNumbersAsStored()
    {
        var header = InlineRow(1, ("A1", "E-mail"), ("B1", "Data umowy"), ("C1", "Kod pocztowy"), ("D1", "Podpisano"), ("E1", "PESEL"),
            ("F1", "Spotkanie"), ("G1", "Kwota"), ("H1", "Godzina"), ("I1", "Czas pracy"), ("J1", "Telefon"), ("K1", "Formuła"));
        var row = "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>ug@zgierz.pl</t></is></c>"
            + Number("B2", "46096", 1) + Number("C2", "95020", 2) + Number("D2", "46096", 3) + Number("E2", "2270803628", 4)
            + Number("F2", "46024.41666666666", 5) + Number("G2", "1234.5", 6) + Number("H2", "0.5", 7) + Number("I2", "1.75", 8)
            + Number("J2", "48123456789") + "<c r=\"K2\" s=\"1\"><f>TODAY()</f><v>46096</v></c></row>";

        var fields = Assert.Single(Read(StyledWorkbook(header + row)).Contacts).Fields;

        Assert.Equal("15.03.2026", fields["Data umowy"]);     // built-in short date (14)
        Assert.Equal("95-020", fields["Kod pocztowy"]);        // 00\-000
        Assert.Equal("15.03.2026", fields["Podpisano"]);       // [$-415]d mmmm yyyy – numeric, the Polish way
        Assert.Equal("02270803628", fields["PESEL"]);          // the leading zero of 00000000000
        Assert.Equal("02.01.2026 10:00", fields["Spotkanie"]); // yyyy-mm-dd hh:mm, rounded to the second
        Assert.Equal("1234.5", fields["Kwota"]);               // currency: as stored
        Assert.Equal("12:00", fields["Godzina"]);              // built-in h:mm (20)
        Assert.Equal("1.75", fields["Czas pracy"]);            // elapsed time [h]:mm: as stored
        Assert.Equal("48123456789", fields["Telefon"]);        // General
        Assert.Equal("15.03.2026", fields["Formuła"]);         // a formula's cached value has the cell's format too
    }

    [Fact]
    public void Dates_Of1904Workbooks_AndOutOfRangeSerials()
    {
        var header = InlineRow(1, ("A1", "E-mail"), ("B1", "Data"), ("C1", "Ujemna"), ("D1", "Za duża"));
        var row = "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>ug@zgierz.pl</t></is></c>"
            + Number("B2", "44634", 1) + Number("C2", "-5", 1) + Number("D2", "99999999", 1) + "</row>";
        var fields = Assert.Single(Read(StyledWorkbook(header + row, date1904: true)).Contacts).Fields;
        Assert.Equal("15.03.2026", fields["Data"]);
        Assert.Equal("-5", fields["Ujemna"]);
        Assert.Equal("99999999", fields["Za duża"]);
    }

    [Fact]
    public void HeaderTypedWithAltEnter_IsOneLineFieldName()
    {
        var sheet = InlineRow(1, ("A1", "Nazwa"), ("B1", "E-mail"), ("C1", "Kod\npocztowy"))
            + InlineRow(2, ("A2", "Gmina Żórawina"), ("B2", "ug@zorawina.pl"), ("C2", "55-020"));
        var result = Read(Xlsx(null, ("A", sheet)));
        Assert.Equal(["Nazwa", "E-mail", "Kod pocztowy"], result.Columns);
        Assert.Equal("55-020", Assert.Single(result.Contacts).Fields["Kod pocztowy"]);
    }

    [Fact]
    public void SheetOrderComesFromTheWorkbook()
    {
        var first = Sheet(InlineRow(1, ("A1", "E-mail")) + InlineRow(2, ("A2", "pierwszy@urzad.pl")));
        var second = Sheet(InlineRow(1, ("A1", "E-mail")) + InlineRow(2, ("A2", "drugi@urzad.pl")));
        var workbook = $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"{RelationshipsNs}\"><sheets>"
            + "<sheet name=\"Drugi\" sheetId=\"2\" r:id=\"rId2\"/><sheet name=\"Pierwszy\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
        var rels = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"/xl/worksheets/sheet2.xml\"/>"
            + "</Relationships>";
        var xlsx = Zip(("xl/workbook.xml", workbook), ("xl/_rels/workbook.xml.rels", rels), ("xl/worksheets/sheet1.xml", first), ("xl/worksheets/sheet2.xml", second));
        Assert.Equal(["drugi@urzad.pl", "pierwszy@urzad.pl"], Read(xlsx).Contacts.Select(c => c.Email));
    }

    [Fact]
    public void HiddenSheetsAndHiddenRows_AreLeftOut_AndCounted()
    {
        // "Archiwum" is hidden, "Stare" very hidden; in "Lista" row 3 is filtered out and row 5 (empty) hidden.
        var list = Sheet(InlineRow(1, ("A1", "Nazwa"), ("B1", "E-mail"))
            + InlineRow(2, ("A2", "Urząd Gminy Łask"), ("B2", "ug@lask.pl"))
            + InlineRow(3, ("A3", "Wycofany urząd"), ("B3", "wycofany@urzad.pl")).Replace("<row r=\"3\">", "<row r=\"3\" hidden=\"1\">", StringComparison.Ordinal)
            + InlineRow(4, ("A4", "Starostwo Zduńska Wola"), ("B4", "sp@zdunskawola.pl")).Replace("<row r=\"4\">", "<row r=\"4\" hidden=\"0\">", StringComparison.Ordinal)
            + "<row r=\"5\" hidden=\"true\"/>");
        var archive = Sheet(InlineRow(1, ("A1", "Nazwa"), ("B1", "E-mail")) + InlineRow(2, ("A2", "Stary urząd"), ("B2", "stary@urzad.pl")));
        var workbook = $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"{RelationshipsNs}\"><sheets>"
            + "<sheet name=\"Lista\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Archiwum\" sheetId=\"2\" state=\"hidden\" r:id=\"rId2\"/>"
            + "<sheet name=\"Stare\" sheetId=\"3\" state=\"veryHidden\" r:id=\"rId3\"/></sheets></workbook>";
        var rels = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>"
            + "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet3.xml\"/>"
            + "</Relationships>";
        var xlsx = Zip(("xl/workbook.xml", workbook), ("xl/_rels/workbook.xml.rels", rels),
            ("xl/worksheets/sheet1.xml", list), ("xl/worksheets/sheet2.xml", archive), ("xl/worksheets/sheet3.xml", archive));

        var result = Read(xlsx);

        Assert.Equal(["ug@lask.pl", "sp@zdunskawola.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal(2, result.HiddenSheets);
        Assert.Equal(1, result.HiddenRows);
        Assert.Equal(0, result.InvalidAddresses);
    }

    [Fact]
    public void OnlyHiddenSheets_NothingIsRead()
    {
        var sheet = Sheet(InlineRow(1, ("A1", "E-mail")) + InlineRow(2, ("A2", "ukryty@urzad.pl")));
        var workbook = $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"{RelationshipsNs}\"><sheets><sheet name=\"A\" sheetId=\"1\" state=\"hidden\" r:id=\"rId1\"/></sheets></workbook>";
        var rels = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>";
        var result = Read(Zip(("xl/workbook.xml", workbook), ("xl/_rels/workbook.xml.rels", rels), ("xl/worksheets/sheet1.xml", sheet)));
        Assert.Empty(result.Contacts);
        Assert.Equal(1, result.HiddenSheets);
    }

    [Fact]
    public void WithoutAWorkbook_SheetsInNaturalOrder()
    {
        string SheetWith(string address) => Sheet(InlineRow(1, ("A1", "E-mail")) + InlineRow(2, ("A2", address)));
        var xlsx = Zip(("xl/worksheets/sheet10.xml", SheetWith("dziesiaty@x.pl")), ("xl/worksheets/sheet2.xml", SheetWith("drugi@x.pl")),
            ("xl/worksheets/sheet1.xml", SheetWith("pierwszy@x.pl")));
        Assert.Equal(["pierwszy@x.pl", "drugi@x.pl", "dziesiaty@x.pl"], Read(xlsx).Contacts.Select(c => c.Email));
    }

    [Fact]
    public void SharedStringIndexOutOfRange_IsAnEmptyCell()
    {
        var sheet = "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>"
            + "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>99</v></c><c r=\"B2\" t=\"inlineStr\"><is><t>gmina@x.pl</t></is></c></row>";
        Assert.Equal(string.Empty, Assert.Single(Read(Xlsx(Shared, ("A", sheet))).Contacts).Name);
    }

    [Fact]
    public void CellsFarRight_AreIgnored()
    {
        var sheet = InlineRow(1, ("A1", "Nazwa"), ("B1", "E-mail")) + InlineRow(2, ("A2", "Gmina"), ("B2", "g@x.pl"), ("XFD2", "daleko"));
        var result = Read(Xlsx(null, ("A", sheet)));
        Assert.Equal(["Nazwa", "E-mail"], result.Columns);
    }

    [Fact]
    public void ZipBomb_HighCompressionRatio_IsRefused()
    {
        var padding = new string('a', 3 * 1024 * 1024);
        var xlsx = Xlsx(null, ("A", InlineRow(1, ("A1", "E-mail"), ("B1", padding))));
        Assert.Throws<InvalidDataException>(() => Read(xlsx));
    }

    [Fact]
    public void ForgedUncompressedSize_IsRefused()
    {
        var rows = string.Concat(Enumerable.Range(1, 2000).Select(i => InlineRow(i, ("A" + i, $"osoba{i}@urzad.pl"), ("B" + i, Guid.NewGuid().ToString()))));
        var single = Zip(("xl/worksheets/sheet1.xml", Sheet(InlineRow(1, ("A1", "E-mail")) + rows)));
        Assert.True(Read(single).Contacts.Count > 1000);   // the honest package reads
        Assert.Throws<InvalidDataException>(() => Read(ForgeUncompressedSize(single, 1000)));
    }

    [Fact]
    public void SharedStringReferencedByManyCells_ExpandingToTooMuchText_IsRefused()
    {
        // About 1 MB of shared strings (ASCII: under the ratio check) referenced by 1 500 cells: 1.5 billion characters.
        var shared = "<si><t>" + new string('a', 1_000_000) + "</t></si>";
        var rows = new StringBuilder();
        for (var r = 1; r <= 150; r++)
        {
            rows.Append("<row r=\"").Append(r).Append("\">");
            for (var c = 0; c < 10; c++)
            {
                rows.Append("<c r=\"").Append((char)('A' + c)).Append(r).Append("\" t=\"s\"><v>0</v></c>");
            }

            rows.Append("</row>");
        }

        var ex = Assert.Throws<InvalidDataException>(() => Read(Xlsx(shared, ("A", rows.ToString()))));
        Assert.Contains("too much text", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedStringsCountOncePerCell_TheTableOfSharedStringsSeparately()
    {
        // 52 cells use a 1 000 000-character string (ASCII: under the ratio check): 52 million characters, just under the
        // limit of 50 Mi – the string itself is counted in the shared strings' own budget.
        var shared = "<si><t>" + new string('e', 1_000_000) + "</t></si>";
        var rows = new StringBuilder(InlineRow(1, ("A1", "E-mail"), ("B1", "Opis")));
        for (var r = 2; r <= 53; r++)
        {
            rows.Append(InlineRow(r, ("A" + r, $"urzad{r}@gmina.pl")).Replace("</row>", $"<c r=\"B{r}\" t=\"s\"><v>0</v></c></row>", StringComparison.Ordinal));
        }

        var result = Read(Xlsx(shared, ("A", rows.ToString())));
        Assert.Equal(52, result.Contacts.Count);
        Assert.Equal(1_000_000, result.Contacts[0].Fields["Opis"].Length);
    }

    [Fact]
    public void Dtd_IsProhibited()
    {
        var sheet = $"<?xml version=\"1.0\"?><!DOCTYPE worksheet [<!ENTITY e \"jan@urzad.pl\">]><worksheet xmlns=\"{SpreadsheetNs}\"><sheetData>"
            + "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>E-mail</t></is></c></row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>&e;</t></is></c></row></sheetData></worksheet>";
        var ex = Assert.Throws<InvalidDataException>(() => Read(Zip(("xl/worksheets/sheet1.xml", sheet))));
        Assert.IsType<System.Xml.XmlException>(ex.InnerException);
    }

    [Fact]
    public void MalformedXml_IsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => Read(Zip(("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row>"))));

    [Fact]
    public void ManyEmptyFormattedRows_CostNothing()
    {
        var empty = new StringBuilder();
        for (var i = 3; i < 20_000; i++)
        {
            empty.Append("<row r=\"").Append(i).Append("\" s=\"1\" customFormat=\"1\"/>");
        }

        var sheet = InlineRow(1, ("A1", "E-mail")) + InlineRow(2, ("A2", "jan@urzad.pl")) + empty;
        Assert.Single(Read(Xlsx(null, ("A", sheet))).Contacts);
    }
}
