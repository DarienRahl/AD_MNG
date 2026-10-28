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
