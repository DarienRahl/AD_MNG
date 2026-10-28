using AdMng.Core.Correspondence.Import;
using static AdMng.Core.Tests.Correspondence.Import.ImportTestFiles;

namespace AdMng.Core.Tests.Correspondence.Import;

/// <summary>DOCX, ODT and ODS files.</summary>
public class DocumentImportTests
{
    private static ContactImportResult Read(string name, byte[] content) => ContactFileReader.Read(name, content, 1250);

    // ---- DOCX ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Docx_TableKeepsItsColumns_TextAroundItIsFreeText()
    {
        var body = P("Wykaz jednostek – w razie pytań: Biuro Rady <rada@powiat.pl>")
            + Tbl(
                Tr(Tc("Lp"), Tc("Nazwa"), Tc("E-mail"), Tc("Uwagi")),
                Tr(Tc("1"), Tc("Urząd Gminy Ślemień"), Tc("ug@slemien.pl"), Tc("bez uwag")),
                Tr(Tc("2"), Tc("Starostwo Żywiec"), Tc("SP@zywiec.pl"), Tc(string.Empty, "<w:vMerge/>")))
            + P("Stan na 28.10.2026");
        var result = Read("wykaz.docx", Docx(body));
        Assert.Equal(ContactSourceFormat.Word, result.Format);
        Assert.Equal(["rada@powiat.pl", "ug@slemien.pl", "sp@zywiec.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal("Biuro Rady", result.Contacts[0].Name);
        Assert.Equal(["Lp", "Nazwa", "E-mail", "Uwagi"], result.Columns);
        Assert.Equal("Urząd Gminy Ślemień", result.Contacts[1].Name);
        Assert.Equal("bez uwag", result.Contacts[2].Fields["Uwagi"]);   // the vMerge continuation repeats the cell above
    }

    [Fact]
    public void Docx_GridSpanAddsEmptyCells()
    {
        var body = Tbl(
            Tr(Tc("Nazwa"), Tc("Wydział"), Tc("E-mail")),
            Tr(Tc("Urząd Miasta Łodzi", "<w:gridSpan w:val=\"2\"/>"), Tc("um@lodz.pl")));
        var contact = Assert.Single(Read("a.docx", Docx(body)).Contacts);
        Assert.Equal(("um@lodz.pl", "Urząd Miasta Łodzi", string.Empty), (contact.Email, contact.Name, contact.Fields["Wydział"]));
    }

    [Fact]
    public void Docx_RunTabsAreTabs_ParagraphTabStopsAreNot()
    {
        var body = "<w:p><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/></w:tabs></w:pPr><w:r><w:t>Nazwa</w:t></w:r><w:r><w:tab/><w:t>E-mail</w:t></w:r></w:p>"
            + "<w:p><w:r><w:t>Gmina Łapy</w:t><w:tab/><w:t>gmina@lapy.pl</w:t></w:r></w:p>";
        var result = Read("a.docx", Docx(body));
        Assert.Equal(["Nazwa", "E-mail"], result.Columns);
        Assert.Equal("Gmina Łapy", Assert.Single(result.Contacts).Name);
    }

    [Fact]
    public void Docx_MailtoHyperlinkGivesItsAddress()
    {
        var body = "<w:p><w:r><w:t xml:space=\"preserve\">Kontakt: </w:t></w:r><w:hyperlink r:id=\"rId7\"><w:r><w:t>Sekretariat Wójta</w:t></w:r></w:hyperlink></w:p>"
            + "<w:p><w:hyperlink r:id=\"rId8\"><w:r><w:t>biuro@gmina.pl</w:t></w:r></w:hyperlink></w:p>";
        var rels = "<Relationship Id=\"rId7\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"mailto:sekretariat@gmina.pl?subject=Pismo\" TargetMode=\"External\"/>"
            + "<Relationship Id=\"rId8\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"mailto:biuro@gmina.pl\" TargetMode=\"External\"/>";
        var result = Read("a.docx", Docx(body, rels));
        Assert.Equal(["sekretariat@gmina.pl", "biuro@gmina.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal("Sekretariat Wójta", result.Contacts[0].Name);
        Assert.Equal(0, result.Duplicates);   // the shown address is not appended twice
    }

    [Fact]
    public void Docx_DeletedTextFieldCodesAndFallbacksAreSkipped()
    {
        var body = "<w:p><w:del w:id=\"1\" w:author=\"x\"><w:r><w:delText>usuniety@gmina.pl</w:delText></w:r></w:del>"
            + "<w:r><w:instrText xml:space=\"preserve\"> HYPERLINK \"mailto:kod@gmina.pl\" </w:instrText></w:r>"
            + "<mc:AlternateContent><mc:Choice Requires=\"wps\"><w:r><w:t>wybor@gmina.pl</w:t></w:r></mc:Choice>"
            + "<mc:Fallback><w:r><w:t>zapasowy@gmina.pl</w:t></w:r></mc:Fallback></mc:AlternateContent>"
            + "<w:r><w:t xml:space=\"preserve\"> i obecny@gmina.pl</w:t></w:r></w:p>";
        Assert.Equal(["wybor@gmina.pl", "obecny@gmina.pl"], Read("a.docx", Docx(body)).Contacts.Select(c => c.Email));
    }

    [Fact]
    public void Docx_NestedTableIsFlattenedIntoItsCell()
    {
        var nested = Tbl(Tr(Tc("zastępca"), Tc("zastepca@gmina.pl")));
        var body = Tbl(
            Tr(Tc("Nazwa"), Tc("E-mail"), Tc("Inni")),
            Tr(Tc("Gmina Łukta"), Tc("gmina@lukta.pl"), "<w:tc>" + nested + "<w:p/></w:tc>"));
        var contact = Assert.Single(Read("a.docx", Docx(body)).Contacts);
        Assert.Equal("gmina@lukta.pl", contact.Email);
        Assert.Contains("zastepca@gmina.pl", contact.Fields["Inni"], StringComparison.Ordinal);
    }

    [Fact]
    public void Docx_LineBreaksInsideACell()
    {
        var body = Tbl(
            Tr(Tc("Odbiorca"), Tc("E-mail")),
            Tr("<w:tc><w:p><w:r><w:t>Urząd Gminy</w:t><w:br/><w:t>Wydział Ochrony Środowiska</w:t></w:r></w:p></w:tc>", Tc("os@gmina.pl")));
        var contact = Assert.Single(Read("a.docx", Docx(body)).Contacts);
        Assert.Equal("Urząd Gminy Wydział Ochrony Środowiska", contact.Name);
        Assert.Equal("Urząd Gminy\nWydział Ochrony Środowiska", contact.Fields["Odbiorca"]);
    }

    [Fact]
    public void Docx_WithoutDocumentXml_IsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => Read("a.docx", Zip(("word/styles.xml", "<w:styles xmlns:w=\"" + WordNs + "\"/>"))));

    [Fact]
    public void Docx_Dtd_IsProhibited()
    {
        var document = $"<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x \"y\">]><w:document xmlns:w=\"{WordNs}\"><w:body/></w:document>";
        Assert.Throws<InvalidDataException>(() => Read("a.docx", Zip(("word/document.xml", document))));
    }

    // ---- ODS -----------------------------------------------------------------------------------------------------------

    private static string Cell(string text, string attributes = "") =>
        $"<table:table-cell office:value-type=\"string\" {attributes}><text:p>{Escape(text)}</text:p></table:table-cell>";

    private static string Row(params string[] cells) => "<table:table-row>" + string.Concat(cells) + "</table:table-row>";

    private static string Table(string name, params string[] rows) => $"<table:table table:name=\"{name}\">" + string.Concat(rows) + "</table:table>";

    [Fact]
    public void Ods_RepeatedColumnsAreCapped_TrailingEmptiesDropped()
    {
        var sheet = Table("Arkusz1",
            Row(Cell("Nazwa"), Cell("E-mail"), Cell("Telefon"), Cell("Telefon zapasowy"), "<table:table-cell table:number-columns-repeated=\"1020\"/>"),
            Row(Cell("Gmina Ożarów"), Cell("gmina@ozarow.pl"), Cell("555", "table:number-columns-repeated=\"2\""),
                "<table:table-cell table:number-columns-repeated=\"16000\"/>"),
            "<table:table-row table:number-rows-repeated=\"1048000\"><table:table-cell table:number-columns-repeated=\"1024\"/></table:table-row>");
        var result = Read("a.ods", OpenDocument(sheet, spreadsheet: true));
        Assert.Equal(ContactSourceFormat.OpenDocument, result.Format);
        Assert.Equal(["Nazwa", "E-mail", "Telefon", "Telefon zapasowy"], result.Columns);
        var contact = Assert.Single(result.Contacts);
        Assert.Equal(("Gmina Ożarów", "555", "555"), (contact.Name, contact.Fields["Telefon"], contact.Fields["Telefon zapasowy"]));
    }

    [Fact]
    public void Ods_LeadingEmptyAndCoveredCellsKeepTheColumns()
    {
        var sheet = Table("A",
            Row("<table:table-cell table:number-columns-repeated=\"2\"/>", Cell("Nazwa"), Cell("E-mail")),
            Row("<table:table-cell/>", "<table:covered-table-cell/>", Cell("Szkoła w Łące"), Cell("szkola@laka.pl")));
        var contact = Assert.Single(Read("a.ods", OpenDocument(sheet, spreadsheet: true)).Contacts);
        Assert.Equal("Szkoła w Łące", contact.Name);
    }

    [Fact]
    public void Ods_EachSheetHasItsOwnHeader_SheetsWithoutAnEmailColumnAreIgnored()
    {
        var notes = Table("Notatki", Row(Cell("Uwagi")), Row(Cell("dzwonić: prywatny@adres.pl")));
        var list = Table("Lista", Row(Cell("Odbiorca"), Cell("E-mail")), Row(Cell("Ośrodek Kultury"), Cell("ok@gmina.pl")));
        var result = Read("a.ods", OpenDocument(notes + list, spreadsheet: true));
        Assert.Equal("ok@gmina.pl", Assert.Single(result.Contacts).Email);
    }

    [Fact]
    public void Ods_AnnotationsAreSkipped_SpacesAreKept()
    {
        var sheet = Table("A",
            Row(Cell("Nazwa"), Cell("E-mail")),
            Row("<table:table-cell><office:annotation><text:p>adnotacja@gmina.pl</text:p></office:annotation><text:p>Urząd<text:s text:c=\"2\"/>Gminy</text:p></table:table-cell>",
                Cell("ug@gmina.pl")));
        var result = Read("a.ods", OpenDocument(sheet, spreadsheet: true));
        var contact = Assert.Single(result.Contacts);
        Assert.Equal("Urząd Gminy", contact.Name);
        Assert.Equal("Urząd  Gminy", contact.Fields["Nazwa"]);
    }

    // ---- ODT -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Odt_ParagraphsLinksAndTables()
    {
        var body = "<text:h>Wykaz</text:h>"
            + "<text:p>Kontakt: <text:a xlink:href=\"mailto:biuro@firma.pl\">Biuro Firmy</text:a></text:p>"
            + "<text:p>Dyżur<text:tab/>dyzur@firma.pl<text:line-break/>Zastępca: Jan Łoś &lt;jan.los@firma.pl&gt;</text:p>"
            + Table("T", Row(Cell("Nazwa"), Cell("E-mail")), Row(Cell("Filia w Żninie"), Cell("znin@firma.pl")));
        var result = Read("a.odt", OpenDocument(body, spreadsheet: false));
        Assert.Equal(ContactSourceFormat.OpenDocument, result.Format);
        Assert.Equal(["biuro@firma.pl", "dyzur@firma.pl", "jan.los@firma.pl", "znin@firma.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal(["Biuro Firmy", "", "Jan Łoś", "Filia w Żninie"], result.Contacts.Select(c => c.Name));
        Assert.Equal(["Nazwa", "E-mail"], result.Columns);
    }

    [Fact]
    public void Odt_WithoutContentXml_IsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => Read("a.odt", Zip(("mimetype", "application/vnd.oasis.opendocument.text"))));

    [Fact]
    public void Odt_TrackedDeletionsAreSkipped()
    {
        var body = "<text:tracked-changes><text:changed-region text:id=\"c1\"><text:deletion><text:p>stary@firma.pl</text:p></text:deletion></text:changed-region></text:tracked-changes>"
            + "<text:p>nowy@firma.pl</text:p>";
        Assert.Equal("nowy@firma.pl", Assert.Single(Read("a.odt", OpenDocument(body, spreadsheet: false)).Contacts).Email);
    }
}
