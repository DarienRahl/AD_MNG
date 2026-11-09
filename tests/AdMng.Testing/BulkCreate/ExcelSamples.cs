using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using AdMng.Testing.RichHtml;

namespace AdMng.Testing.BulkCreate;

/// <summary>
/// Hand-written synthetic clipboard and file samples of the bulk creation wizard (F3, docs/dev/phase3-bulk-create.md 2.1,
/// 2.11): what Excel, LibreOffice and Google Sheets put on the clipboard (Unicode text with Excel's quoting rule, the HTML
/// format with <c>x:num</c>), the quirks the reader handles, headerless and English tables, a cp1250 CSV and the UTF-16 LE
/// text of <em>Tekst Unicode (*.txt)</em>, generated Polish rows and a minimal <c>.xlsx</c>. Never real data; the people
/// are invented, with Polish diacritics (ą ć ę ł ń ó ś ź ż) on purpose.
/// </summary>
public static class ExcelSamples
{
    private const string Nbsp = " ";
    private const string NarrowNbsp = " ";
    private const string ZeroWidth = "​";
    private const string Bom = "﻿";

    /// <summary>The headers of <see cref="PolishTsv"/>.</summary>
    public static IReadOnlyList<string> PolishHeaders { get; } =
        ["Telefon komórkowy", "Imię", "Nazwisko", "E-mail", "Stanowisko", "Dział", "Przełożony"];

    /// <summary>
    /// A Polish HR range as Excel copies it (CRLF, a final CRLF): mobile phones in three spellings (<c>+48 600 100 201</c>,
    /// <c>600 100 202</c>, <c>48600100203</c>), names with diacritics, a double-barrelled surname, managers by name, login and
    /// e-mail and one empty manager cell.
    /// </summary>
    public static string PolishTsv { get; } = Tsv(
        PolishHeaders,
        ["+48 600 100 201", "Łucja", "Żak", "lucja.zak@contoso.com", "Księgowa", "Księgowość", "Piotr Zieliński"],
        ["600 100 202", "Grzegorz", "Brzęczyszczykiewicz", "grzegorz.brzeczyszczykiewicz@contoso.com", "Specjalista ds. płac", "Kadry", "pzielinski"],
        ["48600100203", "Ewa", "Wiśniewska", "ewa.wisniewska@contoso.test", "Handlowiec", "Sprzedaż", "pawel.lewandowski@contoso.test"],
        ["+48 512 345 678", "Józef", "Ćwikła", "jozef.cwikla@contoso.com", "Magazynier", "Logistyka", ""],
        ["+48 698 765 432", "Zofia", "Ślęzak-Nowińska", "zofia.slezak-nowinska@contoso.com", "Kierownik zmiany", "Produkcja", "Jan Kowalski"]);

    /// <summary>
    /// The operator scenario of 2.6 (<c>BulkCreateEndToEndTests.OperatorScenario</c>): five columns – <em>Telefon komórkowy,
    /// Imię, Nazwisko, Dział, E-mail</em> – with five people with diacritics.
    /// </summary>
    public static string OperatorScenarioTsv { get; } = Tsv(
        ["Telefon komórkowy", "Imię", "Nazwisko", "Dział", "E-mail"],
        ["+48 600 200 301", "Łukasz", "Grzędziński", "Księgowość", "lukasz.grzedzinski@firma.pl"],
        ["+48 600 200 302", "Małgorzata", "Źrebiec", "Księgowość", "malgorzata.zrebiec@firma.pl"],
        ["+48 600 200 303", "Bożena", "Kąkol", "Kadry", "bozena.kakol@firma.pl"],
        ["+48 600 200 304", "Mikołaj", "Śliwiński", "Logistyka", "mikolaj.sliwinski@firma.pl"],
        ["+48 600 200 305", "Jolanta", "Żółkiewska", "Sprzedaż", "jolanta.zolkiewska@firma.pl"]);

    /// <summary>
    /// Excel's quoting rule (2.1): row 1 <c>"Społem" PSS</c> keeps its quotes (the closing quote is followed by a space);
    /// row 2 a quoted multi-line address (Alt+Enter is LF); row 3 doubled quotes inside a quoted multi-line cell; row 4 a
    /// quoted cell with a tab; row 5 an unbalanced opening quote that must not swallow rows 6–9.
    /// </summary>
    public static string QuotesTsv { get; } =
        "Imię\tNazwisko\tFirma\tAdres\tUwagi\r\n"
        + "Anna\tNowak\t\"Społem\" PSS\tul. Długa 1\t\r\n"
        + "Jan\tKowalski\tContoso\t\"ul. Kwiatowa 5\n00-950 Warszawa\"\tok\r\n"
        + "Marek\tZając\t\"Firma \"\"Bis\"\"\nOddział 2\"\tul. Krótka 2\t\r\n"
        + "Aleksandra\tKrawiec\t\"Dział\tA\"\tul. Polna 3\t\r\n"
        + "\"Piotr\tWójcik\tContoso\tul. Leśna 7\tbez zamknięcia cudzysłowu\r\n"
        + "Hanna\tŁęcka\tContoso\tul. Brzozowa 8\t\r\n"
        + "Witold\tŚwiątek\tContoso\tul. Sosnowa 9\t\r\n"
        + "Celina\tŻuraw\tContoso\tul. Jodłowa 10\t\r\n"
        + "Bogdan\tĆmiel\tContoso\tul. Bukowa 11\t\r\n";

    /// <summary>The number of data rows of <see cref="QuotesTsv"/>.</summary>
    public const int QuotesRowCount = 9;

    /// <summary>Excel error values in both languages (Polish <c>#N/D!</c> … and English <c>#N/A</c> …) in the <em>E-mail</em> and <em>Telefon</em> columns.</summary>
    public static string ErrorValuesTsv { get; } = Tsv(
        ["Imię", "Nazwisko", "E-mail", "Telefon"],
        ["Anna", "Nowak", "#N/D!", "#ARG!"],
        ["Jan", "Kowalski", "#NAZWA?", "#DZIEL/0!"],
        ["Ewa", "Wiśniewska", "#N/A", "#VALUE!"],
        ["Józef", "Ćwikła", "#REF!", "#NAME?"],
        ["Zofia", "Żak", "#ADR!", "#LICZBA!"]);

    /// <summary>A title row and a blank row above the header (<c>TitleRowsSkipped</c>, header line 3).</summary>
    public static string TitleRowTsv { get; } =
        "Nowi pracownicy – listopad 2026\r\n"
        + "\r\n"
        + "Imię\tNazwisko\tE-mail\tTelefon komórkowy\r\n"
        + "Łucja\tŻak\tlucja.zak@contoso.com\t+48 600 100 201\r\n"
        + "Grzegorz\tBąk\tgrzegorz.bak@contoso.com\t+48 600 100 202\r\n";

    /// <summary>
    /// The normalisation quirks: a BOM, NBSP in a name, U+202F thousands separators in <em>Numer pracownika</em>, zero-width
    /// spaces, trailing empty columns in every row, Alt+Enter in an <em>Imię</em> cell (quoted, LF), an empty middle row and a
    /// ragged row longer than the header.
    /// </summary>
    public static string QuirksTsv { get; } =
        Bom + "Imię\tNazwisko\tNumer pracownika\t\t\r\n"
        + "Anna" + Nbsp + "Maria\tNowak\t12" + NarrowNbsp + "345\t\t\r\n"
        + "Jan\tKowal" + ZeroWidth + "ski\t12" + NarrowNbsp + "346\t\t\r\n"
        + "\t\t\t\t\r\n"
        + "\"Ewa\nKatarzyna\"\tWiśniewska\t12" + NarrowNbsp + "347\t\t\r\n"
        + "Józef\tĆwikła\t12" + NarrowNbsp + "348\tnadmiarowa\tkomórka\r\n"
        + "\t\t\t\t\r\n";

    /// <summary>LF-only line endings and no final line break.</summary>
    public static string LfOnlyTsv { get; } = "Imię\tNazwisko\tE-mail\nŁucja\tŻak\tlucja.zak@contoso.com\nGrzegorz\tBąk\tgrzegorz.bak@contoso.com";

    /// <summary>A mobile number in a <em>General</em> column shown in scientific notation (<c>4,86E+11</c>; warning without HTML).</summary>
    public static string ScientificTsv { get; } = Tsv(
        ["Imię", "Nazwisko", "Telefon komórkowy"],
        ["Łucja", "Żak", "4,86E+11"],
        ["Grzegorz", "Bąk", "+48 600 100 202"]);

    /// <summary>The full value of the scientific-notation cell of <see cref="ScientificTsv"/> (Excel's <c>x:num</c>).</summary>
    public const string ScientificFullValue = "486001002001";

    /// <summary>The HTML format of <see cref="ScientificTsv"/> as Excel writes it (the same shape, <c>x:num</c> on the number cell).</summary>
    public static string ScientificHtml { get; } = ExcelTable(
        ["Imię", "Nazwisko", "Telefon komórkowy"],
        [["Łucja", "Żak", null], ["Grzegorz", "Bąk", "+48 600 100 202"]],
        numericCell: (0, 2, ScientificFullValue, "4,86E+11"));

    /// <summary>CF_HTML bytes of <see cref="ScientificHtml"/> (header with UTF-8 offsets, trailing NUL).</summary>
    public static byte[] ScientificCfHtml => CfHtmlFixture.Build(ScientificHtml);

    /// <summary>An Excel range in the HTML format only (<c>&lt;!--StartFragment--&gt;</c>, a <c>mailto:</c> link in a name cell stays the name).</summary>
    public static string ExcelHtml { get; } = ExcelTable(
        ["Imię", "Nazwisko", "E-mail"],
        [["Łucja", "<a href=\"mailto:lucja.zak@contoso.com\">Żak</a>", "lucja.zak@contoso.com"], ["Grzegorz", "Bąk", "grzegorz.bak@contoso.com"]],
        numericCell: null);

    /// <summary>A LibreOffice Calc range in the HTML format (<c>&lt;br&gt;</c> in a cell, <c>colspan</c> in the title row).</summary>
    public static string LibreOfficeHtml { get; } =
        "<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.0 Transitional//EN\">\r\n<html><head><meta http-equiv=\"content-type\" content=\"text/html; charset=utf-8\"/>"
        + "<meta name=\"generator\" content=\"LibreOffice 24.2\"/></head><body>\r\n"
        + "<table cellspacing=\"0\" border=\"0\"><colgroup width=\"120\"></colgroup><colgroup span=\"2\" width=\"160\"></colgroup>\r\n"
        + "<tr><td height=\"20\" align=\"left\">Imię</td><td align=\"left\">Nazwisko</td><td align=\"left\">Adres</td></tr>\r\n"
        + "<tr><td height=\"40\" align=\"left\">Hanna</td><td align=\"left\">Łęcka</td><td align=\"left\">ul. Brzozowa 8<br>00-950 Warszawa</td></tr>\r\n"
        + "<tr><td colspan=\"2\" height=\"20\" align=\"left\">Witold Świątek</td><td align=\"left\">ul. Sosnowa 9</td></tr>\r\n"
        + "</table>\r\n</body></html>\r\n";

    /// <summary>A Google Sheets range in the HTML format (<c>google-sheets-html-origin</c>, <c>th</c>-less table).</summary>
    public static string GoogleSheetsHtml { get; } =
        "<meta charset=\"utf-8\"><google-sheets-html-origin><style type=\"text/css\"><!--td {border: 1px solid #cccccc;}--></style>"
        + "<table xmlns=\"http://www.w3.org/1999/xhtml\" cellspacing=\"0\" cellpadding=\"0\" dir=\"ltr\" border=\"1\"><colgroup><col width=\"100\"/><col width=\"100\"/><col width=\"200\"/></colgroup>"
        + "<tbody><tr style=\"height:21px;\"><td>Imię</td><td>Nazwisko</td><td>E-mail</td></tr>"
        + "<tr style=\"height:21px;\"><td>Celina</td><td>Żuraw</td><td>celina.zuraw@contoso.com</td></tr>"
        + "<tr style=\"height:21px;\"><td>Bogdan</td><td>Ćmiel</td><td>bogdan.cmiel@contoso.com</td></tr></tbody></table>";

    /// <summary>A single column copied from a web page: no tab in the text (<c>NeedsHtml</c> is true).</summary>
    public static string SingleColumnWebText { get; } = "lucja.zak@contoso.com\r\ngrzegorz.bak@contoso.com\r\newa.wisniewska@contoso.test\r\n";

    /// <summary>A 5-column range without a header (columns A–E: given name, surname, e-mail, mobile, department).</summary>
    public static string HeaderlessTsv { get; } = Tsv(
        null,
        ["Łucja", "Żak", "lucja.zak@contoso.com", "+48 600 100 201", "Księgowość"],
        ["Grzegorz", "Bąk", "grzegorz.bak@contoso.com", "+48 600 100 202", "Kadry"],
        ["Ewa", "Wiśniewska", "ewa.wisniewska@contoso.test", "+48 600 100 203", "Sprzedaż"],
        ["Józef", "Ćwikła", "jozef.cwikla@contoso.com", "+48 512 345 678", "Logistyka"],
        ["Zofia", "Ślęzak", "zofia.slezak@contoso.com", "+48 698 765 432", "Produkcja"]);

    /// <summary>English headers (<em>First Name, Last Name, Email, Mobile Phone, Department, Job Title</em>).</summary>
    public static string EnglishTsv { get; } = Tsv(
        ["First Name", "Last Name", "Email", "Mobile Phone", "Department", "Job Title"],
        ["Łucja", "Żak", "lucja.zak@contoso.com", "+48 600 100 201", "Księgowość", "Księgowa"],
        ["Grzegorz", "Bąk", "grzegorz.bak@contoso.com", "+48 600 100 202", "Kadry", "Specjalista"]);

    /// <summary>The text of <see cref="Cp1250Csv"/> (semicolon-separated, as Polish Excel saves CSV).</summary>
    public static string Cp1250CsvText { get; } =
        "Imię;Nazwisko;E-mail;Dział\r\nŁucja;Żak;lucja.zak@contoso.com;Księgowość\r\nŻaneta;Ćwiek;zaneta.cwiek@contoso.com;Sprzedaż\r\n";

    /// <summary>A CSV file in Windows-1250 (the ANSI code page of Polish Windows), without a BOM.</summary>
    public static byte[] Cp1250Csv => Encoding.GetEncoding(1250).GetBytes(Cp1250CsvText);

    /// <summary>Excel's <em>Tekst Unicode (*.txt)</em>: UTF-16 LE with a BOM, tab-separated.</summary>
    public static byte[] Utf16UnicodeText => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Tsv(
        ["Imię", "Nazwisko", "Telefon komórkowy"],
        ["Łucja", "Żak", "+48 600 100 201"],
        ["Mikołaj", "Śliwiński", "+48 600 100 204"]))];

    private static readonly string[] GivenNames =
    [
        "Łucja", "Grzegorz", "Ewa", "Józef", "Zofia", "Mikołaj", "Bożena", "Łukasz", "Małgorzata", "Jolanta",
        "Żaneta", "Ścibor", "Ćwiczysław", "Agnieszka", "Bartłomiej", "Halina", "Przemysław", "Weronika", "Zdzisław", "Jadwiga",
    ];

    private static readonly string[] Surnames =
    [
        "Żak", "Bąk", "Wiśniewska", "Ćwikła", "Ślęzak", "Śliwiński", "Kąkol", "Grzędziński", "Źrebiec", "Żółkiewska",
        "Łęcka", "Świątek", "Żuraw", "Ćmiel", "Brzęczyszczykiewicz", "Dąbrowski", "Kaźmierczak", "Wójcik", "Kozłowska", "Szymański",
    ];

    /// <summary>
    /// <paramref name="count"/> generated rows with a header (<em>Imię, Nazwisko, E-mail, Telefon komórkowy, Dział</em>):
    /// Polish names with diacritics (repeating combinations, so name-based patterns collide), unique e-mail addresses and
    /// mobile numbers.
    /// </summary>
    public static string Rows(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var departments = new[] { "Księgowość", "Kadry", "Sprzedaż", "Logistyka", "Produkcja" };
        var text = new StringBuilder("Imię\tNazwisko\tE-mail\tTelefon komórkowy\tDział\r\n");
        for (var i = 0; i < count; i++)
        {
            var given = GivenNames[i % GivenNames.Length];
            var surname = Surnames[i / GivenNames.Length % Surnames.Length];
            var number = 600_000_000 + i;
            text.Append(given).Append('\t')
                .Append(surname).Append('\t')
                .Append(CultureInfo.InvariantCulture, $"pracownik{i + 1:D5}@contoso.com").Append('\t')
                .Append(CultureInfo.InvariantCulture, $"+48 {number / 1_000_000} {number / 1000 % 1000:D3} {number % 1000:D3}").Append('\t')
                .Append(departments[i % departments.Length]).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// A minimal <c>.xlsx</c> workbook (inline strings, no styles) with <paramref name="rows"/> on its first sheet;
    /// <paramref name="hiddenRows"/> (1-based sheet rows) are hidden; <paramref name="otherSheet"/> adds a second non-empty sheet.
    /// </summary>
    public static byte[] MinimalXlsx(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyCollection<int>? hiddenRows = null, bool otherSheet = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        const string spreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string relationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var sheets = new List<(string Name, string Data)> { ("Arkusz1", SheetData(rows, hiddenRows)) };
        if (otherSheet)
        {
            sheets.Add(("Arkusz2", SheetData([["Inne dane"], ["nie do wczytania"]], null)));
        }

        var workbook = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"{spreadsheetNs}\" xmlns:r=\"{relationshipsNs}\"><sheets>");
        var relationships = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(archive, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            for (var i = 0; i < sheets.Count; i++)
            {
                var n = i + 1;
                workbook.Append(CultureInfo.InvariantCulture, $"<sheet name=\"{sheets[i].Name}\" sheetId=\"{n}\" r:id=\"rId{n}\"/>");
                relationships.Append(CultureInfo.InvariantCulture,
                    $"<Relationship Id=\"rId{n}\" Type=\"{relationshipsNs}/worksheet\" Target=\"worksheets/sheet{n}.xml\"/>");
                Entry(archive, $"xl/worksheets/sheet{n}.xml",
                    $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{spreadsheetNs}\"><sheetData>{sheets[i].Data}</sheetData></worksheet>");
            }

            Entry(archive, "xl/workbook.xml", workbook.Append("</sheets></workbook>").ToString());
            Entry(archive, "xl/_rels/workbook.xml.rels", relationships.Append("</Relationships>").ToString());
        }

        return stream.ToArray();
    }

    /// <summary>Tab-separated rows as Excel copies them: CRLF after every row (the header first, when given).</summary>
    public static string Tsv(IReadOnlyList<string>? header, params IReadOnlyList<string>[] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        if (header is not null)
        {
            text.AppendJoin('\t', header).Append("\r\n");
        }

        foreach (var row in rows)
        {
            text.AppendJoin('\t', row).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>An Excel-like HTML range (<c>x:str</c>, the fragment comments); a null cell is the numeric cell.</summary>
    private static string ExcelTable(IReadOnlyList<string> header, IReadOnlyList<IReadOnlyList<string?>> rows, (int Row, int Column, string Number, string Shown)? numericCell)
    {
        var html = new StringBuilder(
            "<html xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:x=\"urn:schemas-microsoft-com:office:excel\" xmlns=\"http://www.w3.org/TR/REC-html40\">\r\n"
            + "<head><meta http-equiv=Content-Type content=\"text/html; charset=utf-8\"><meta name=Generator content=\"Microsoft Excel 15\"></head>\r\n"
            + "<body link=\"#0563C1\" vlink=\"#954F72\">\r\n<table border=0 cellpadding=0 cellspacing=0 width=384 style='border-collapse:collapse;width:288pt'>\r\n"
            + "<!--StartFragment-->\r\n");
        html.Append(" <tr height=20 style='height:15.0pt'>\r\n");
        foreach (var cell in header)
        {
            html.Append("  <td height=20 width=128 style='height:15.0pt;width:96pt'>").Append(WebUtility.HtmlEncode(cell)).Append("</td>\r\n");
        }

        html.Append(" </tr>\r\n");
        for (var r = 0; r < rows.Count; r++)
        {
            html.Append(" <tr height=20 style='height:15.0pt'>\r\n");
            for (var c = 0; c < rows[r].Count; c++)
            {
                if (numericCell is { } numeric && numeric.Row == r && numeric.Column == c)
                {
                    html.Append("  <td align=right x:num=\"").Append(numeric.Number).Append("\">").Append(numeric.Shown).Append("</td>\r\n");
                    continue;
                }

                var value = rows[r][c] ?? string.Empty;
                html.Append("  <td x:str>").Append(value.StartsWith('<') ? value : WebUtility.HtmlEncode(value)).Append("</td>\r\n");
            }

            html.Append(" </tr>\r\n");
        }

        html.Append("<!--EndFragment-->\r\n</table>\r\n</body>\r\n</html>\r\n");
        return html.ToString();
    }

    private static string SheetData(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyCollection<int>? hiddenRows)
    {
        var data = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            var number = r + 1;
            var hidden = hiddenRows is not null && hiddenRows.Contains(number) ? " hidden=\"1\"" : string.Empty;
            data.Append(CultureInfo.InvariantCulture, $"<row r=\"{number}\"{hidden}>");
            for (var c = 0; c < rows[r].Count; c++)
            {
                var reference = ColumnLetters(c) + number.ToString(CultureInfo.InvariantCulture);
                data.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{WebUtility.HtmlEncode(rows[r][c])}</t></is></c>");
            }

            data.Append("</row>");
        }

        return data.ToString();
    }

    private static string ColumnLetters(int index)
    {
        var letters = string.Empty;
        for (var value = index + 1; value > 0; value = (value - 1) / 26)
        {
            letters = (char)('A' + ((value - 1) % 26)) + letters;
        }

        return letters;
    }

    private static void Entry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        writer.Write(bytes, 0, bytes.Length);
    }
}
