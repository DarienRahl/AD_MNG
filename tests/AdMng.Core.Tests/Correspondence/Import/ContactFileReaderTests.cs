using System.Text;
using AdMng.Core.Correspondence.Import;
using static AdMng.Core.Tests.Correspondence.Import.ImportTestFiles;

namespace AdMng.Core.Tests.Correspondence.Import;

/// <summary>Text files, size limits and the supported extensions (the packaged formats have their own test classes).</summary>
public class ContactFileReaderTests
{
    [Theory]
    [InlineData("kontakty.csv")]
    [InlineData("KONTAKTY.CSV")]
    [InlineData(@"C:\Users\jan\Pobrane\prawo-pl_kontakty_2026.xlsx")]
    [InlineData("/tmp/lista.Docx")]
    [InlineData("a.tsv")]
    [InlineData("a.txt")]
    [InlineData("a.xlsm")]
    [InlineData("a.xltx")]
    [InlineData("a.dotx")]
    [InlineData("a.docm")]
    [InlineData("a.odt")]
    [InlineData("a.ods")]
    [InlineData("a.rtf")]
    [InlineData("a.htm")]
    [InlineData("a.html")]
    public void IsSupported_TheListedExtensions(string fileName) => Assert.True(ContactFileReader.IsSupported(fileName));

    [Theory]
    [InlineData("a.pdf")]
    [InlineData("a.xls")]
    [InlineData("a.doc")]
    [InlineData("csv")]
    [InlineData("katalog.csv/plik")]
    [InlineData("")]
    [InlineData("  ")]
    public void IsSupported_OtherExtensions(string fileName) => Assert.False(ContactFileReader.IsSupported(fileName));

    [Fact]
    public void Extensions_AreLowerCaseWithADot() =>
        Assert.All(ContactFileReader.Extensions, e => Assert.Matches("^\\.[a-z]+$", e));

    [Fact]
    public void Read_RefusesFilesAbove50MiB()
    {
        var content = new byte[ContactFileReader.MaxFileBytes + 1];
        Assert.Throws<InvalidDataException>(() => ContactFileReader.Read("duzy.csv", content, 1250));
    }

    [Fact]
    public void Read_CsvInWindows1250()
    {
        var bytes = Cp1250.GetBytes("Nazwa;E-mail;Miejscowość\r\nUrząd Miasta Łodzi;um@lodz.pl;Łódź\r\nGmina Źródła;gmina@zrodla.pl;Źródła\r\n");
        var result = ContactFileReader.Read("kontakty.csv", bytes, 1250);
        Assert.Equal(ContactSourceFormat.Delimited, result.Format);
        Assert.Equal(["Nazwa", "E-mail", "Miejscowość"], result.Columns);
        Assert.Equal(["Urząd Miasta Łodzi", "Gmina Źródła"], result.Contacts.Select(c => c.Name));
        Assert.Equal("Łódź", result.Contacts[0].Fields["Miejscowość"]);
    }

    [Fact]
    public void Read_CsvInUtf8WithBom()
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("E-mail;Nazwa\nżaneta@firma.pl;x\nzaneta@firma.pl;Żaneta Wójcik\n")];
        var contact = Assert.Single(ContactFileReader.Read("k.csv", bytes, 1252).Contacts);
        Assert.Equal(("zaneta@firma.pl", "Żaneta Wójcik"), (contact.Email, contact.Name));
    }

    [Fact]
    public void Read_TxtFreeText()
    {
        var result = ContactFileReader.Read("lista.txt", Encoding.UTF8.GetBytes("Szanowni Państwo,\nproszę o kontakt: Jan Żółtowski <jan@urzad.pl>, a.nowak@urzad.pl"), 1250);
        Assert.Equal(ContactSourceFormat.Text, result.Format);
        Assert.Equal(["jan@urzad.pl", "a.nowak@urzad.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal("Jan Żółtowski", result.Contacts[0].Name);
    }

    [Fact]
    public void Read_TsvTable() =>
        Assert.Equal("Sołectwo Łęg", Assert.Single(ContactFileReader.Read("a.tsv", Encoding.UTF8.GetBytes("Nazwa\tE-mail\nSołectwo Łęg\tsoltys@leg.pl\n"), 1250).Contacts).Name);

    [Fact]
    public void Read_UnknownExtension_IsText() =>
        Assert.Single(ContactFileReader.Read("adresy.lst", Encoding.UTF8.GetBytes("jan@urzad.pl"), 1250).Contacts);

    [Fact]
    public void Read_EmptyFile() => Assert.Empty(ContactFileReader.Read("pusty.csv", [], 1250).Contacts);

    [Theory]
    [InlineData("a.xlsx")]
    [InlineData("a.docx")]
    [InlineData("a.odt")]
    [InlineData("a.ods")]
    public void Read_NotAZipPackage_IsInvalidData(string fileName) =>
        Assert.Throws<InvalidDataException>(() => ContactFileReader.Read(fileName, Encoding.ASCII.GetBytes("to nie jest plik zip"), 1250));

    // ---- every address (the "Nie wysyłać" list) ------------------------------------------------------------------------

    [Fact]
    public void ReadAddresses_TakesEveryAddressOfATable_NotOnlyTheEmailColumn()
    {
        // The table gives one contact per row (the first address of the e-mail column); a do-not-send list needs them all.
        var csv = Cp1250.GetBytes("Nazwa;E-mail;Inne e-maile\r\nGmina Łęki;sekretariat@leki.pl, wojt@leki.pl;skarbnik@leki.pl\r\n"
            + "Gmina Żórawina;;\"rada@zorawina.pl; WOJT@zorawina.pl\"\r\nGmina Ślęża;brak;\r\n");
        Assert.Equal(["sekretariat@leki.pl", "wojt@leki.pl", "skarbnik@leki.pl", "rada@zorawina.pl", "wojt@zorawina.pl"],
            ContactFileReader.ReadAddresses("nie-wysylac.csv", csv, 1250));
        Assert.Equal(["sekretariat@leki.pl"], ContactFileReader.Read("nie-wysylac.csv", csv, 1250).Contacts.Select(c => c.Email));
    }

    [Fact]
    public void ReadAddresses_OfEveryFormat()
    {
        var xlsx = Xlsx(null, ("A", InlineRow(1, ("A1", "Nazwa"), ("B1", "E-mail"), ("C1", "Zastępca"))
            + InlineRow(2, ("A2", "Urząd"), ("B2", "ug@gmina.pl"), ("C2", "zastepca@gmina.pl"))));
        Assert.Equal(["ug@gmina.pl", "zastepca@gmina.pl"], ContactFileReader.ReadAddresses("a.xlsx", xlsx, 1250));

        var docx = Docx(P("Proszę nie pisać do: Biuro <biuro@firma.pl>") + Tbl(Tr(Tc("E-mail"), Tc("Uwagi")), Tr(Tc("jan@firma.pl"), Tc("także anna@firma.pl"))));
        Assert.Equal(["biuro@firma.pl", "jan@firma.pl", "anna@firma.pl"], ContactFileReader.ReadAddresses("a.docx", docx, 1250));

        var html = Encoding.UTF8.GetBytes("<html><body><p><a href=\"mailto:rzecznik@urzad.pl\">Rzecznik</a></p>"
            + "<table><tr><th>E-mail</th><th>Uwagi</th></tr><tr><td>sekretariat@urzad.pl</td><td>kopia: kadry@urzad.pl</td></tr></table></body></html>");
        Assert.Equal(["rzecznik@urzad.pl", "sekretariat@urzad.pl", "kadry@urzad.pl"], ContactFileReader.ReadAddresses("a.html", html, 1250));

        var odt = OpenDocument("<text:p>Wypisani: ewa@x.pl, <text:a xlink:href=\"mailto:piotr@x.pl\">Piotr Łoś</text:a></text:p>", spreadsheet: false);
        Assert.Equal(["ewa@x.pl", "piotr@x.pl"], ContactFileReader.ReadAddresses("a.odt", odt, 1250));

        var rtf = Encoding.ASCII.GetBytes(@"{\rtf1\ansi\ansicpg1250 Nie wysy\'b3a\'e6: a@x.pl; b@x.pl\par}");
        Assert.Equal(["a@x.pl", "b@x.pl"], ContactFileReader.ReadAddresses("a.rtf", rtf, 1250));
    }

    [Fact]
    public void ReadAddresses_IncludesHiddenSheets()
    {
        // Leaving a hidden sheet out of a do-not-send list would mail its addresses: hidden content counts here.
        var hidden = Sheet(InlineRow(1, ("A1", "E-mail")) + InlineRow(2, ("A2", "archiwum@urzad.pl")));
        var workbook = $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"{RelationshipsNs}\"><sheets><sheet name=\"A\" sheetId=\"1\" state=\"hidden\" r:id=\"rId1\"/></sheets></workbook>";
        var rels = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>";
        var xlsx = Zip(("xl/workbook.xml", workbook), ("xl/_rels/workbook.xml.rels", rels), ("xl/worksheets/sheet1.xml", hidden));
        Assert.Equal(["archiwum@urzad.pl"], ContactFileReader.ReadAddresses("a.xlsx", xlsx, 1250));
    }

    [Fact]
    public void ReadAddresses_RefusesWhatReadRefuses()
    {
        Assert.Throws<InvalidDataException>(() => ContactFileReader.ReadAddresses("duzy.csv", new byte[ContactFileReader.MaxFileBytes + 1], 1250));
        Assert.Throws<InvalidDataException>(() => ContactFileReader.ReadAddresses("a.xlsx", Encoding.ASCII.GetBytes("to nie jest plik zip"), 1250));
        Assert.Empty(ContactFileReader.ReadAddresses("pusty.csv", [], 1250));
    }
}
