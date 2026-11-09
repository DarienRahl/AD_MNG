using System.Text;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Correspondence.Import;
using AdMng.Testing.BulkCreate;
using AdMng.Testing.RichHtml;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>
/// Reading pasted and loaded tables (docs/dev/phase3-bulk-create.md 2.1, 2.11) on the shared synthetic samples
/// (<see cref="ExcelSamples"/>): clipboard text with Excel's quirks, the HTML format, CSV, <c>.xlsx</c>, header detection,
/// limits, appending and re-interpretation.
/// </summary>
public sealed class TableTextReaderTests
{
    private static InputTable Paste(string text, HeaderMode header = HeaderMode.Auto) => TableTextReader.ReadClipboard(text, null, null, header);

    private static IReadOnlyList<string> Headers(InputTable table) => [.. table.Columns.Select(c => c.Header)];

    private static IReadOnlyList<TableWarning> Of(InputTable table, TableWarningCode code) => [.. table.Warnings.Where(w => w.Code == code)];

    [Fact]
    public void PolishTsv_HeaderColumnsAndRows()
    {
        var table = Paste(ExcelSamples.PolishTsv);

        Assert.True(table.HasHeader);
        Assert.Equal(TableSource.ClipboardText, table.Source);
        Assert.Equal(1, table.HeaderLine);
        Assert.Empty(table.TitleRows);
        Assert.Equal(ExcelSamples.PolishHeaders, Headers(table));
        Assert.Equal(["A", "B", "C", "D", "E", "F", "G"], table.Columns.Select(c => c.Letter));
        Assert.Equal([1, 2, 3, 4, 5], table.Rows.Select(r => r.Number));
        Assert.Equal("Łucja", table.Cell(0, 1));
        Assert.Equal("Brzęczyszczykiewicz", table.Cell(1, 2));
        Assert.Equal("Ślęzak-Nowińska", table.Cell(4, 2));
        Assert.Equal("48600100203", table.Cell(2, 0));
        Assert.Equal(string.Empty, table.Cell(3, 6));
        Assert.Equal(6, table.NextRowNumber);
        Assert.Empty(table.Warnings);
        Assert.Equal(0, table.FindColumn("Telefon komorkowy")!.Index);
    }

    [Fact]
    public void QuotesTsv_MultiLineDoubledAndUnbalancedQuotes()
    {
        var table = Paste(ExcelSamples.QuotesTsv);

        Assert.Equal(ExcelSamples.QuotesRowCount, table.Rows.Count);
        Assert.Equal("\"Społem\" PSS", table.Cell(0, 2));
        Assert.Equal("ul. Kwiatowa 5\r\n00-950 Warszawa", table.Cell(1, 3));
        Assert.Equal("Firma \"Bis\"\r\nOddział 2", table.Cell(2, 2));
        Assert.Equal("Dział\tA", table.Cell(3, 2));
        Assert.Equal("\"Piotr", table.Cell(4, 0));
        Assert.Equal(["Hanna", "Witold", "Celina", "Bogdan"], table.Rows.Skip(5).Select(r => r.Cell(0)));
        Assert.Equal("Ćmiel", table.Cell(8, 1));
        Assert.Equal(5, table.Columns.Count);
    }

    [Fact]
    public void ErrorValues_AreEmptied_InBothLanguages()
    {
        var table = Paste(ExcelSamples.ErrorValuesTsv);

        Assert.All(table.Rows, r => Assert.Equal(string.Empty, r.Cell(2)));
        Assert.All(table.Rows, r => Assert.Equal(string.Empty, r.Cell(3)));
        var errors = Of(table, TableWarningCode.FormulaError);
        Assert.Equal(10, errors.Count);
        Assert.Contains(new TableWarning(TableWarningCode.FormulaError, 1, 2), errors);
        Assert.Contains(new TableWarning(TableWarningCode.FormulaError, 5, 3), errors);
        Assert.Equal(4, table.Columns.Count);
    }

    [Fact]
    public void TitleRow_AndBlankRow_AboveTheHeader()
    {
        var table = Paste(ExcelSamples.TitleRowTsv);

        Assert.True(table.HasHeader);
        Assert.Equal(3, table.HeaderLine);
        Assert.Equal(2, table.TitleRows.Count);
        Assert.Equal(["Nowi pracownicy – listopad 2026"], table.TitleRows[0]);
        Assert.Empty(table.TitleRows[1]);
        Assert.Equal(["Imię", "Nazwisko", "E-mail", "Telefon komórkowy"], Headers(table));
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new TableWarning(TableWarningCode.TitleRowsSkipped, Count: 2), Assert.Single(table.Warnings));
    }

    [Fact]
    public void Quirks_BomNbspZeroWidthTrailingColumnsAltEnterAndEmptyRows()
    {
        var table = Paste(ExcelSamples.QuirksTsv);

        Assert.Equal(ExcelSamples.QuirksColumnCount, table.Columns.Count);
        Assert.Equal(ExcelSamples.QuirksRowCount, table.Rows.Count);
        Assert.Equal(["Imię", "Nazwisko", "Numer pracownika"], Headers(table));
        Assert.Equal("Anna Maria", table.Cell(0, 0));
        Assert.Equal("12 345", table.Cell(0, 2));
        Assert.Equal("Kowalski", table.Cell(1, 1));
        Assert.Equal("Ewa\r\nKatarzyna", table.Cell(2, 0));
        Assert.Equal([1, 2, 3, 4], table.Rows.Select(r => r.Number));
        Assert.Equal(new TableWarning(TableWarningCode.EmptyRowsSkipped, Count: 1), Assert.Single(table.Warnings));
    }

    [Fact]
    public void RaggedRows_ExtendTheTable()
    {
        var table = Paste(ExcelSamples.RaggedTsv);

        Assert.Equal(ExcelSamples.RaggedColumnCount, table.Columns.Count);
        Assert.Equal(["F", "G"], table.Columns.Skip(5).Select(c => c.Name));
        Assert.Equal(string.Empty, table.Columns[5].Header);
        Assert.Equal("komórka", table.Cell(1, 6));
        Assert.Equal(string.Empty, table.Cell(2, 3));
        Assert.Equal(new TableWarning(TableWarningCode.RaggedRows, ExcelSamples.RaggedRowNumber, 5, 2), Assert.Single(table.Warnings));
    }

    [Fact]
    public void DuplicateHeaders_KeepBothColumns()
    {
        var table = Paste(ExcelSamples.DuplicateHeaderTsv);

        Assert.Equal(["Imię", "Nazwisko", "Telefon", "Telefon (2)"], Headers(table));
        Assert.Equal(new TableWarning(TableWarningCode.DuplicateHeader, Column: 3), Assert.Single(table.Warnings));
        Assert.Equal(3, table.FindColumn("telefon (2)")!.Index);
    }

    [Fact]
    public void SensitiveColumns_PasswordLikeIsWarned_IdentifierIsNot()
    {
        var table = Paste(ExcelSamples.SensitiveColumnsTsv);

        Assert.True(table.HasHeader);
        Assert.Equal(new TableWarning(TableWarningCode.PasswordLikeColumn, Column: 2), Assert.Single(table.Warnings));
        Assert.Equal("PESEL", table.Columns[3].Header);
    }

    [Theory]
    [InlineData(nameof(ExcelSamples.CrOnlyTsv))]
    [InlineData(nameof(ExcelSamples.LfOnlyTsv))]
    public void CrOnlyAndLfOnly_LineEndings(string sample)
    {
        var text = sample == nameof(ExcelSamples.CrOnlyTsv) ? ExcelSamples.CrOnlyTsv : ExcelSamples.LfOnlyTsv;

        var table = Paste(text);

        Assert.Equal(["Imię", "Nazwisko", "E-mail"], Headers(table));
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("grzegorz.bak@contoso.com", table.Cell(1, 2));
        Assert.Empty(table.Warnings);
    }

    [Fact]
    public void ScientificNotation_WithoutHtml_IsWarned()
    {
        var table = Paste(ExcelSamples.ScientificTsv);

        Assert.Equal("4,86E+11", table.Cell(0, 2));
        Assert.Equal(new TableWarning(TableWarningCode.ScientificNotation, 1, 2), Assert.Single(table.Warnings));
    }

    [Fact]
    public void ScientificNotation_IsRecoveredFromCfHtml()
    {
        var table = TableTextReader.ReadClipboard(ExcelSamples.ScientificTsv, ExcelSamples.ScientificCfHtml, null, HeaderMode.Auto);

        Assert.Equal(TableSource.ClipboardText, table.Source);
        Assert.Equal(ExcelSamples.ScientificFullValue, table.Cell(0, 2));
        Assert.Equal("+48 600 100 202", table.Cell(1, 2));
        Assert.Equal(new TableWarning(TableWarningCode.RecoveredFromHtml, 1, 2), Assert.Single(table.Warnings));
    }

    [Fact]
    public void ScientificNotation_IsRecoveredFromCfHtmlText()
    {
        var text = Encoding.UTF8.GetString(ExcelSamples.ScientificCfHtml).TrimEnd('\0');

        var table = TableTextReader.ReadClipboard(ExcelSamples.ScientificTsv, null, text, HeaderMode.Auto);

        Assert.Equal(ExcelSamples.ScientificFullValue, table.Cell(0, 2));
    }

    [Fact]
    public void ScientificNotation_IsNotRecovered_FromATableOfAnotherShape()
    {
        var other = CfHtmlFixture.Build("<table><tr><td>Imię</td></tr><tr><td x:num=\"486001002001\">4,86E+11</td></tr></table>");

        var table = TableTextReader.ReadClipboard(ExcelSamples.ScientificTsv, other, null, HeaderMode.Auto);

        Assert.Equal("4,86E+11", table.Cell(0, 2));
        Assert.Equal(TableWarningCode.ScientificNotation, Assert.Single(table.Warnings).Code);
    }

    [Fact]
    public void ScientificNotation_InASingleColumnCopy_IsRecoveredFromTheHtmlTable()
    {
        // One column of mobile numbers copied from Excel: the text has no tab, so the HTML table is the source – its
        // x:num carries the full value of the cell Excel shows as 4,86E+11 (review of B1).
        const string text = "Telefon komórkowy\r\n4,86E+11\r\n+48 600 100 202\r\n";
        var html = CfHtmlFixture.Build(
            "<html xmlns:x=\"urn:schemas-microsoft-com:office:excel\"><body><table><!--StartFragment-->"
            + "<tr><td>Telefon komórkowy</td></tr>"
            + "<tr><td align=right x:num=\"" + ExcelSamples.ScientificFullValue + "\">4,86E+11</td></tr>"
            + "<tr><td x:str>+48 600 100 202</td></tr>"
            + "<!--EndFragment--></table></body></html>");

        var table = TableTextReader.ReadClipboard(text, html, null, HeaderMode.Auto);

        Assert.Equal(TableSource.ClipboardHtml, table.Source);
        Assert.Equal(["Telefon komórkowy"], Headers(table));
        Assert.Equal(ExcelSamples.ScientificFullValue, table.Cell(0, 0));
        Assert.Equal("+48 600 100 202", table.Cell(1, 0));
        Assert.Equal(new TableWarning(TableWarningCode.RecoveredFromHtml, 1, 0), Assert.Single(table.Warnings));
    }

    [Fact]
    public void TheHtmlFormat_IsIgnored_WhenTheTextIsEnough()
    {
        var html = CfHtmlFixture.Build("<table><tr><td>Inne</td></tr><tr><td>dane</td></tr></table>");

        var table = TableTextReader.ReadClipboard(ExcelSamples.PolishTsv, html, null, HeaderMode.Auto);

        Assert.Equal(ExcelSamples.PolishHeaders, Headers(table));
        Assert.Equal(TableSource.ClipboardText, table.Source);
    }

    [Fact]
    public void NeedsHtml()
    {
        Assert.True(TableTextReader.NeedsHtml(null));
        Assert.True(TableTextReader.NeedsHtml(string.Empty));
        Assert.True(TableTextReader.NeedsHtml("  \r\n"));
        Assert.True(TableTextReader.NeedsHtml(ExcelSamples.SingleColumnWebText));
        Assert.True(TableTextReader.NeedsHtml(ExcelSamples.ScientificTsv));
        Assert.False(TableTextReader.NeedsHtml(ExcelSamples.PolishTsv));
        Assert.False(TableTextReader.NeedsHtml(ExcelSamples.QuotesTsv));
    }

    [Fact]
    public void HtmlOnly_ExcelFragment()
    {
        var table = TableTextReader.ReadClipboard(null, CfHtmlFixture.Build(ExcelSamples.ExcelHtml), null, HeaderMode.Auto);

        Assert.Equal(TableSource.ClipboardHtml, table.Source);
        Assert.Equal(["Imię", "Nazwisko", "E-mail"], Headers(table));
        Assert.Equal("Żak", table.Cell(0, 1));
        Assert.Equal("grzegorz.bak@contoso.com", table.Cell(1, 2));
    }

    [Fact]
    public void HtmlOnly_LibreOffice_LineBreakAndColspan()
    {
        var table = TableTextReader.ReadHtml(ExcelSamples.LibreOfficeHtml, HeaderMode.Auto);

        Assert.Equal(["Imię", "Nazwisko", "Adres"], Headers(table));
        Assert.Equal("ul. Brzozowa 8\r\n00-950 Warszawa", table.Cell(0, 2));
        Assert.Equal(["Witold Świątek", "", "ul. Sosnowa 9"], table.Rows[1].Cells);
        Assert.Equal(new TableWarning(TableWarningCode.MergedCells, 2), Assert.Single(table.Warnings));
    }

    [Fact]
    public void HtmlOnly_GoogleSheets() =>
        Assert.Equal(["Celina", "Żuraw", "celina.zuraw@contoso.com"], TableTextReader.ReadHtml(ExcelSamples.GoogleSheetsHtml, HeaderMode.Auto).Rows[0].Cells);

    [Fact]
    public void HtmlOnly_RowspanAndNestedTable()
    {
        var table = TableTextReader.ReadHtml(ExcelSamples.MergedAndNestedHtml, HeaderMode.Auto);

        Assert.Equal(["Imię", "Nazwisko", "Dział", "Uwagi"], Headers(table));
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(["Witold", "Świątek"], table.Rows[1].Cells);
        Assert.Equal("bez zmian", table.Cell(2, 3));
        Assert.Equal(new TableWarning(TableWarningCode.MergedCells, 1), Assert.Single(table.Warnings));
    }

    [Fact]
    public void SingleColumnWebPaste_WithoutHtml_IsOneColumnOfData()
    {
        var table = Paste(ExcelSamples.SingleColumnWebText);

        Assert.False(table.HasHeader);
        Assert.Single(table.Columns);
        Assert.Equal("A", table.Columns[0].Name);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("ewa.wisniewska@contoso.test", table.Cell(2, 0));
    }

    [Fact]
    public void SingleColumnWebPaste_WithHtmlWithoutATable_UsesTheText()
    {
        var html = CfHtmlFixture.Build("<p>lucja.zak@contoso.com</p>");

        var table = TableTextReader.ReadClipboard(ExcelSamples.SingleColumnWebText, html, null, HeaderMode.Auto);

        Assert.Equal(TableSource.ClipboardText, table.Source);
        Assert.Equal(3, table.Rows.Count);
    }

    [Fact]
    public void Headerless_FiveColumns_AreNamedByLetter()
    {
        var table = Paste(ExcelSamples.HeaderlessTsv);

        Assert.False(table.HasHeader);
        Assert.Null(table.HeaderLine);
        Assert.Equal(["A", "B", "C", "D", "E"], table.Columns.Select(c => c.Name));
        Assert.All(table.Columns, c => Assert.Equal(string.Empty, c.Header));
        Assert.Equal(5, table.Rows.Count);
        Assert.Equal("Łucja", table.Cell(0, 0));
        Assert.Equal(2, table.FindColumn("c")!.Index);
    }

    [Fact]
    public void EnglishHeaders_AreDetected() =>
        Assert.Equal(["First Name", "Last Name", "Email", "Mobile Phone", "Department", "Job Title"], Headers(Paste(ExcelSamples.EnglishTsv)));

    [Fact]
    public void HeaderModes_AreForced()
    {
        var noHeader = Paste(ExcelSamples.PolishTsv, HeaderMode.NoHeader);
        var forced = Paste(ExcelSamples.HeaderlessTsv, HeaderMode.FirstRowIsHeader);

        Assert.False(noHeader.HasHeader);
        Assert.Equal(6, noHeader.Rows.Count);
        Assert.Equal("Telefon komórkowy", noHeader.Cell(0, 0));
        Assert.True(forced.HasHeader);
        Assert.Equal("Łucja", forced.Columns[0].Header);
        Assert.Equal(4, forced.Rows.Count);
    }

    [Fact]
    public void Generated5000Rows_AreReadWithDiacritics()
    {
        var table = Paste(ExcelSamples.Rows(5000));

        Assert.Equal(5000, table.Rows.Count);
        Assert.Equal(5000, table.Rows[^1].Number);
        Assert.Equal(["Imię", "Nazwisko", "E-mail", "Telefon komórkowy", "Dział"], Headers(table));
        Assert.Contains(table.Rows, r => r.Cell(0) == "Łucja");
        Assert.Empty(table.Warnings);
    }

    [Fact]
    public void FindHeaderRow_EdgeCases()
    {
        Assert.Equal(0, TableTextReader.FindHeaderRow([["Imię", "Nazwisko", "E-mail"]]));
        Assert.Null(TableTextReader.FindHeaderRow([["Łucja", "Żak", "lucja.zak@contoso.com"]]));
        Assert.Null(TableTextReader.FindHeaderRow([["Imię", "Kolumna X"]]));
        Assert.Null(TableTextReader.FindHeaderRow([]));
        Assert.Null(TableTextReader.FindHeaderRow([[], [""]]));

        // Empty lines are skipped; the index is into the list.
        Assert.Equal(2, TableTextReader.FindHeaderRow([[], ["Nowi pracownicy – listopad 2026"], ["Imię", "Nazwisko"], ["Łucja", "Żak"]]));

        // A line of known headers with a data cell is no header.
        Assert.Null(TableTextReader.FindHeaderRow([["Imię", "Nazwisko", "lucja.zak@contoso.com"], ["Łucja", "Żak", "x"]]));

        // One known word among many data words (Biuro is a department value here) is no header.
        Assert.Null(TableTextReader.FindHeaderRow([["Łucja", "Żak", "Biuro", "Warszawa", "Kierownik", "Mokotów"], ["Grzegorz", "Bąk", "Kadry", "Kraków", "Specjalista", "Śródmieście"]]));

        // Unknown headers above e-mail addresses: the first line is the header.
        Assert.Equal(0, TableTextReader.FindHeaderRow([["Kto", "Gdzie pisać"], ["Łucja Żak", "lucja.zak@contoso.com"], ["Grzegorz Bąk", "grzegorz.bak@contoso.com"]]));
        Assert.Null(TableTextReader.FindHeaderRow([["Kto", "Kto"], ["Łucja Żak", "lucja.zak@contoso.com"]]));
        Assert.Null(TableTextReader.FindHeaderRow([["Kto", "Rok 2026"], ["Łucja Żak", "lucja.zak@contoso.com"]]));
        Assert.Null(TableTextReader.FindHeaderRow([["Kto", "Co"], ["Łucja Żak", "Księgowa"]]));

        // The best candidate within the first five non-empty lines wins.
        Assert.Equal(1, TableTextReader.FindHeaderRow([["Lista", "Imię"], ["Imię", "Nazwisko", "Dział"], ["Łucja", "Żak", "Kadry"]]));
    }

    [Fact]
    public void MaxRows_TruncatesWithAWarning()
    {
        var table = Paste(ExcelSamples.Rows(TableTextReader.MaxRows + 25));

        Assert.Equal(TableTextReader.MaxRows, table.Rows.Count);
        Assert.Equal(new TableWarning(TableWarningCode.TooManyRows, Count: 25), Assert.Single(table.Warnings));
    }

    [Fact]
    public void MaxColumns_TruncatesWithAWarning()
    {
        var header = string.Join('\t', Enumerable.Range(1, 300).Select(i => $"Kolumna {i}"));
        var row = string.Join('\t', Enumerable.Range(1, 300).Select(i => $"w{i}"));

        var table = Paste(header + "\r\n" + row + "\r\n", HeaderMode.FirstRowIsHeader);

        Assert.Equal(TableTextReader.MaxColumns, table.Columns.Count);
        Assert.Equal(TableTextReader.MaxColumns, table.Rows[0].Cells.Count);
        Assert.Equal(new TableWarning(TableWarningCode.TooManyColumns, Count: 300 - TableTextReader.MaxColumns), Assert.Single(table.Warnings));
    }

    [Fact]
    public void MaxCellLength_CutsTheCell()
    {
        var table = Paste("Imię\tUwagi\r\nŁucja\t" + new string('ż', TableTextReader.MaxCellLength + 100) + "\r\n");

        Assert.Equal(TableTextReader.MaxCellLength, table.Cell(0, 1).Length);
        Assert.Equal(new TableWarning(TableWarningCode.CellTooLong, 1, 1), Assert.Single(table.Warnings));
    }

    [Fact]
    public void ReadDelimited_Semicolon_Cp1250()
    {
        var text = TextDecoding.Decode(ExcelSamples.Cp1250Csv, 1250);

        var table = TableTextReader.ReadDelimited(text, null, HeaderMode.Auto);

        Assert.Equal(TableSource.CsvFile, table.Source);
        Assert.Equal(["Imię", "Nazwisko", "E-mail", "Dział"], Headers(table));
        Assert.Equal(["Żaneta", "Ćwiek", "zaneta.cwiek@contoso.com", "Sprzedaż"], table.Rows[1].Cells);
    }

    [Fact]
    public void ReadDelimited_Utf16UnicodeText_IsTabSeparated()
    {
        var text = TextDecoding.Decode(ExcelSamples.Utf16UnicodeText, 1250);

        var table = TableTextReader.ReadDelimited(text, null, HeaderMode.Auto);

        Assert.Equal(["Imię", "Nazwisko", "Telefon komórkowy"], Headers(table));
        Assert.Equal(["Mikołaj", "Śliwiński", "+48 600 100 204"], table.Rows[1].Cells);
    }

    [Fact]
    public void ReadDelimited_Comma_WithQuotesAndABom()
    {
        var table = TableTextReader.ReadDelimited("\uFEFFImię,Nazwisko,Adres\r\nŁucja,Żak,\"ul. Długa 1, m. 2\"\r\n", null, HeaderMode.Auto);

        Assert.Equal(["Imię", "Nazwisko", "Adres"], Headers(table));
        Assert.Equal("ul. Długa 1, m. 2", table.Cell(0, 2));
    }

    [Fact]
    public void ReadDelimited_ExplicitDelimiter_AndSingleColumn()
    {
        Assert.Equal(["a;b", "c"], TableTextReader.ReadDelimited("a;b|c\r\n", '|', HeaderMode.NoHeader).Rows[0].Cells);
        Assert.Single(TableTextReader.ReadDelimited("lucja.zak@contoso.com\r\ngrzegorz.bak@contoso.com\r\n", null, HeaderMode.Auto).Columns);
        Assert.Throws<ArgumentException>(() => TableTextReader.ReadDelimited("a", '"', HeaderMode.Auto));
    }

    [Fact]
    public void ReadDelimited_RemovesFormulaProtection_OnlyForCsvFiles()
    {
        var csv = TableTextReader.ReadDelimited(ExcelSamples.ProtectedCsvText, null, HeaderMode.Auto);
        var text = TableTextReader.ReadDelimited(ExcelSamples.ProtectedCsvText, null, HeaderMode.Auto, TableSource.ClipboardText);

        Assert.Equal(["Łucja", "Żak-O'Neill", "+48 600 100 201", "Księgowość"], csv.Rows[0].Cells);
        Assert.Equal("-Kadry", csv.Cell(1, 3));
        Assert.Equal("'+48 600 100 201", text.Cell(0, 2));
    }

    [Fact]
    public void ReadXlsx_HiddenRowsAndOtherSheets()
    {
        var bytes = ExcelSamples.MinimalXlsx(
            [["Imię", "Nazwisko", "E-mail"], ["Łucja", "Żak", "lucja.zak@contoso.com"], ["Ukryty", "Wiersz", "x@contoso.com"], ["Grzegorz", "Bąk", "grzegorz.bak@contoso.com"]],
            hiddenRows: [3],
            otherSheet: true);

        var table = TableTextReader.ReadXlsx(bytes, HeaderMode.Auto);

        Assert.Equal(TableSource.XlsxFile, table.Source);
        Assert.Equal(["Imię", "Nazwisko", "E-mail"], Headers(table));
        Assert.Equal(["Łucja", "Grzegorz"], table.Rows.Select(r => r.Cell(0)));
        Assert.Contains(new TableWarning(TableWarningCode.OtherSheetsIgnored, Count: 1), table.Warnings);
        Assert.Contains(new TableWarning(TableWarningCode.HiddenRowsIgnored, Count: 1), table.Warnings);
    }

    [Fact]
    public void ReadXlsx_FirstVisibleSheet_AndNumberFormats()
    {
        var bytes = ExcelSamples.MinimalXlsx(
            [["Imię", "Telefon komórkowy", "Kod pocztowy"], ["Łucja", "", ""]],
            hiddenFirstSheet: true,
            numbers: [("B2", "48600100201", ExcelSamples.XlsxPhoneFormat), ("C2", "2950", ExcelSamples.XlsxPostalCodeFormat)]);

        var table = TableTextReader.ReadXlsx(bytes, HeaderMode.Auto);

        Assert.Equal(["Łucja", "+48 600 100 201", "02-950"], table.Rows[0].Cells);
        Assert.Empty(table.Warnings);
    }

    [Fact]
    public void ReadXlsx_NotAWorkbook_IsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => TableTextReader.ReadXlsx(Encoding.UTF8.GetBytes(ExcelSamples.PolishTsv), HeaderMode.Auto));

    [Fact]
    public void Append_ByHeader_MatchesFoldedHeadersAndAddsNewColumns()
    {
        var existing = Paste(ExcelSamples.PolishTsv);
        var added = Paste("NAZWISKO\timie\tE-mail\tNumer pracownika\r\nZając\tBeata\tbeata.zajac@contoso.com\tE-2001\r\n");

        var table = TableTextReader.Append(existing, added);

        Assert.Equal(8, table.Columns.Count);
        Assert.Equal("Numer pracownika", table.Columns[7].Header);
        Assert.Equal("H", table.Columns[7].Letter);
        Assert.Equal(6, table.Rows[^1].Number);
        Assert.Equal(["", "Beata", "Zając", "beata.zajac@contoso.com", "", "", "", "E-2001"], table.Rows[^1].Cells);
        Assert.Equal(7, table.NextRowNumber);
        Assert.Equal(existing.HeaderLine, table.HeaderLine);
    }

    [Fact]
    public void Append_ByPosition_WithoutHeaders()
    {
        var existing = Paste(ExcelSamples.HeaderlessTsv);
        var added = Paste("Bożena\tKąkol\tbozena.kakol@contoso.com\t+48 600 200 303\tKadry\tdodatkowa\r\n");

        var table = TableTextReader.Append(existing, added);

        Assert.Equal(6, table.Columns.Count);
        Assert.Equal("F", table.Columns[5].Name);
        Assert.Equal(6, table.Rows[^1].Number);
        Assert.Equal("dodatkowa", table.Cell(5, 5));
    }

    [Fact]
    public void Append_KeepsTheWarningsOfTheAddedRows_Renumbered()
    {
        var existing = Paste(ExcelSamples.PolishTsv);
        var added = Paste("Imię\tNazwisko\tE-mail\r\nAnna\tNowak\t#N/D!\r\n");

        var table = TableTextReader.Append(existing, added);

        Assert.Equal(new TableWarning(TableWarningCode.FormulaError, 6, 3), Assert.Single(table.Warnings));
    }

    [Fact]
    public void Append_ToAnEmptyTable_ReturnsTheAddedOne_AndRemovedRowsKeepTheirNumbers()
    {
        var added = Paste(ExcelSamples.PolishTsv);

        Assert.Same(added, TableTextReader.Append(InputTable.Empty, added));

        var shortened = added with { Rows = added.Rows.Take(3).ToList(), NextRowNumber = 6 };
        var appended = TableTextReader.Append(shortened, Paste(ExcelSamples.EnglishTsv));
        Assert.Equal([1, 2, 3, 6, 7], appended.Rows.Select(r => r.Number));
    }

    [Fact]
    public void CountRepeatedRows()
    {
        var existing = Paste(ExcelSamples.PolishTsv);
        var same = Paste(ExcelSamples.PolishTsv);
        var reordered = Paste(ExcelSamples.Tsv(
            ["Imię", "Nazwisko", "Telefon komórkowy", "E-mail", "Stanowisko", "Dział", "Przełożony"],
            ["Łucja", "Żak", "+48 600 100 201", "lucja.zak@contoso.com", "Księgowa", "Księgowość", "Piotr Zieliński"],
            ["Nowa", "Osoba", "+48 600 999 999", "nowa.osoba@contoso.com", "", "", ""]));
        var withNewColumn = Paste("Imię\tNazwisko\tUwagi\r\nŁucja\tŻak\tnowa\r\n");

        Assert.Equal(5, TableTextReader.CountRepeatedRows(existing, same));
        Assert.Equal(1, TableTextReader.CountRepeatedRows(existing, reordered));
        Assert.Equal(0, TableTextReader.CountRepeatedRows(existing, withNewColumn));
        Assert.Equal(0, TableTextReader.CountRepeatedRows(InputTable.Empty, same));
    }

    [Fact]
    public void Reinterpret_HeaderToDataAndBack()
    {
        var table = Paste(ExcelSamples.PolishTsv);

        var data = TableTextReader.Reinterpret(table, HeaderMode.NoHeader);
        var back = TableTextReader.Reinterpret(data, HeaderMode.FirstRowIsHeader);

        Assert.False(data.HasHeader);
        Assert.Equal(6, data.Rows.Count);
        Assert.Equal(ExcelSamples.PolishHeaders, data.Rows[0].Cells);
        Assert.Equal([1, 2, 3, 4, 5, 6], data.Rows.Select(r => r.Number));
        Assert.Equal(ExcelSamples.PolishHeaders, Headers(back));
        Assert.Equal(5, back.Rows.Count);
        Assert.Equal(1, back.Rows[0].Number);
    }

    [Fact]
    public void Reinterpret_AnotherHeaderLine_OfATitledPaste()
    {
        var table = Paste(ExcelSamples.TitleRowTsv);

        var titleAsHeader = TableTextReader.Reinterpret(table, HeaderMode.FirstRowIsHeader, headerLine: 1);
        var redetected = TableTextReader.Reinterpret(titleAsHeader, HeaderMode.Auto);
        var noHeader = TableTextReader.Reinterpret(table, HeaderMode.NoHeader);

        Assert.Equal("Nowi pracownicy – listopad 2026", titleAsHeader.Columns[0].Header);
        Assert.Equal(1, titleAsHeader.HeaderLine);
        Assert.Equal(3, titleAsHeader.Rows.Count);
        Assert.Contains(titleAsHeader.Warnings, w => w.Code == TableWarningCode.EmptyRowsSkipped);

        // The blank line was skipped as an empty data row: the lines are now the title (header), then the old header.
        Assert.Equal(2, redetected.HeaderLine);
        Assert.Equal(["Imię", "Nazwisko", "E-mail", "Telefon komórkowy"], Headers(redetected));
        Assert.Equal(4, noHeader.Rows.Count);
        Assert.Equal(["Nowi pracownicy – listopad 2026"], noHeader.Rows[0].Cells);
        Assert.Throws<ArgumentOutOfRangeException>(() => TableTextReader.Reinterpret(table, HeaderMode.FirstRowIsHeader, headerLine: 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => TableTextReader.Reinterpret(table, HeaderMode.FirstRowIsHeader, headerLine: 0));
    }

    [Fact]
    public void Reinterpret_MovesCellWarnings_AndRestoresDuplicateHeaders()
    {
        var errors = Paste(ExcelSamples.ErrorValuesTsv);
        var duplicates = Paste(ExcelSamples.DuplicateHeaderTsv);

        var errorsAsData = TableTextReader.Reinterpret(errors, HeaderMode.NoHeader);
        var duplicatesAsData = TableTextReader.Reinterpret(duplicates, HeaderMode.NoHeader);

        Assert.Contains(new TableWarning(TableWarningCode.FormulaError, 2, 2), errorsAsData.Warnings);
        Assert.Equal(10, errorsAsData.Warnings.Count(w => w.Code == TableWarningCode.FormulaError));
        Assert.Equal(["Imię", "Nazwisko", "Telefon", "Telefon"], duplicatesAsData.Rows[0].Cells);
        Assert.DoesNotContain(duplicatesAsData.Warnings, w => w.Code == TableWarningCode.DuplicateHeader);
    }

    [Fact]
    public void EmptyInput_GivesAnEmptyTable()
    {
        var empty = TableTextReader.ReadClipboard(null, null, null, HeaderMode.Auto);
        var blank = Paste("\r\n\t\r\n");

        Assert.Empty(empty.Rows);
        Assert.Empty(empty.Columns);
        Assert.Empty(blank.Rows);
        Assert.Empty(blank.Warnings);
    }

    [Fact]
    public void TheTable_PrintsNoValue()
    {
        var table = Paste(ExcelSamples.SensitiveColumnsTsv);

        Assert.DoesNotContain("Start!2026a", table.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Łucja", table.ToString(), StringComparison.Ordinal);
        Assert.All(table.Warnings, w => Assert.DoesNotContain("Hasło", w.ToString(), StringComparison.Ordinal));
    }
}
