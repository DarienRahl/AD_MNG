using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Csv;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Salutations;

namespace AdMng.Core.Batch.Creation;

/// <summary>
/// Turns pasted or loaded text into an <see cref="InputTable"/> (docs/dev/phase3-bulk-create.md 2.1, owner B1): the
/// clipboard's Unicode text with Excel's own quoting rule first, the HTML format only when it is needed, CSV / <c>.txt</c>
/// and <c>.xlsx</c> files; normalisation of every cell, Excel quirks, header detection (also below a title row), limits,
/// appending, repeated-paste detection and re-interpretation of the header choice. Pure and thread-safe; nothing is
/// written anywhere (rule 1).
/// </summary>
/// <remarks>
/// Every source goes through the same steps: cells normalized (<see cref="CellText.Normalize"/>; for
/// <see cref="TableSource.CsvFile"/> first <see cref="CsvCodec.RemoveFormulaProtection"/>), Excel error values emptied
/// (<see cref="TableWarningCode.FormulaError"/>), cells cut at <see cref="MaxCellLength"/>, scientific notation marked (or
/// recovered from the HTML format), trailing empty cells, columns and lines dropped silently, the header found
/// (<see cref="FindHeaderRow"/>) or forced, the lines above it kept as <see cref="InputTable.TitleRows"/>, empty lines in the
/// middle skipped (one <see cref="TableWarningCode.EmptyRowsSkipped"/>; empty lines before the first data line of a table
/// without a header are dropped silently), rows numbered from 1. Warnings carry positions and counts, never a value.
/// </remarks>
public static partial class TableTextReader
{
    /// <summary>Rows read at most; more are dropped with <see cref="TableWarningCode.TooManyRows"/>.</summary>
    public const int MaxRows = 10_000;

    /// <summary>Columns read at most (the spreadsheet limit of U5); more → <see cref="TableWarningCode.TooManyColumns"/>.</summary>
    public const int MaxColumns = Correspondence.Import.SpreadsheetLimits.MaxColumns;

    /// <summary>Characters of one cell at most; longer cells are cut with <see cref="TableWarningCode.CellTooLong"/>.</summary>
    public const int MaxCellLength = 65_536;

    /// <summary>The lines of the text that must hold a tab for it to count as a table (2.1).</summary>
    private const int TabularLines = 20;

    /// <summary>Non-empty lines header detection looks at: five candidates and the five lines after the first (2.1).</summary>
    private const int HeaderScanLines = 10;

    /// <summary>The warnings attached to one cell or line, renumbered when the rows are.</summary>
    private static readonly HashSet<TableWarningCode> CellCodes =
    [
        TableWarningCode.ScientificNotation, TableWarningCode.RecoveredFromHtml, TableWarningCode.CellTooLong,
        TableWarningCode.FormulaError,
    ];

    /// <summary>The warnings of the whole read, kept when the header choice changes.</summary>
    private static readonly HashSet<TableWarningCode> ReadCodes =
    [
        TableWarningCode.OtherSheetsIgnored, TableWarningCode.HiddenRowsIgnored,
    ];

    /// <summary>
    /// Reads a clipboard paste: <paramref name="text"/> (Unicode text, tab-separated, CRLF) is the primary source, split by
    /// Excel's quoting rule; the HTML format (<paramref name="cfHtml"/> raw CF_HTML bytes, or <paramref name="cfHtmlText"/>)
    /// is parsed only when <see cref="NeedsHtml"/> says so – and then recovers scientific-notation cells from Excel's
    /// <c>x:num</c> (<see cref="TableWarningCode.RecoveredFromHtml"/>).
    /// </summary>
    /// <remarks>
    /// Text that is empty or not tabular is replaced by the HTML table when there is one (<see cref="TableSource.ClipboardHtml"/>);
    /// otherwise it is read as one column. The HTML format recovers a cell only when its table has the same shape as the text
    /// (lines × columns after the trailing empty ones are dropped). An unreadable HTML format is ignored.
    /// </remarks>
    public static InputTable ReadClipboard(string? text, byte[]? cfHtml, string? cfHtmlText, HeaderMode header)
    {
        HtmlTable? html = null;
        if (NeedsHtml(text) && (cfHtml is { Length: > 0 } || !string.IsNullOrEmpty(cfHtmlText)))
        {
            html = ReadClipboardHtml(cfHtml, cfHtmlText);
        }

        var tabular = !string.IsNullOrWhiteSpace(text) && SpreadsheetText.HasTabInFirstLines(text, TabularLines);
        if (!tabular && html is { Rows.Count: > 0 } && html.Rows.Any(r => r.Any(c => !string.IsNullOrWhiteSpace(c))))
        {
            return FromHtml(html, header);
        }

        if (string.IsNullOrEmpty(text))
        {
            return EmptyTable(TableSource.ClipboardText);
        }

        var read = Prepare(SpreadsheetText.SplitTsv(text), TableSource.ClipboardText, mergedRows: null);
        if (tabular && html is not null)
        {
            Recover(read, html);
        }

        return Assemble(read, header, headerIndex: null, TableSource.ClipboardText);
    }

    /// <summary>
    /// True when the text alone is not enough: empty, not tabular (no tab in its first 20 lines) or a cell in scientific
    /// notation. The App asks the clipboard for the HTML format only then (2.6).
    /// </summary>
    public static bool NeedsHtml(string? text) =>
        string.IsNullOrWhiteSpace(text)
        || !SpreadsheetText.HasTabInFirstLines(text, TabularLines)
        || SpreadsheetText.ContainsScientificCell(text);

    /// <summary>
    /// Reads CSV or <c>.txt</c> text already decoded by Presentation (<c>CsvFiles.ReadTextAsync</c>): a null
    /// <paramref name="delimiter"/> is the candidate delimiter most frequent in the first record (outside quotes), tab
    /// included; tab-delimited text uses Excel's TSV rule, other delimiters the CSV parser.
    /// </summary>
    /// <remarks>
    /// A leading BOM is skipped. Ties between candidates go to the order of <see cref="CsvCodec.CandidateDelimiters"/>
    /// (<c>;</c>, <c>,</c>, tab); a first record without any is one column (<see cref="CsvCodec.DefaultDelimiter"/>). The
    /// apostrophe AD_MNG's exports put before <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c> is removed for
    /// <see cref="TableSource.CsvFile"/> only.
    /// </remarks>
    /// <exception cref="ArgumentException">The delimiter is a quote or a line break.</exception>
    public static InputTable ReadDelimited(string text, char? delimiter, HeaderMode header, TableSource source = TableSource.CsvFile)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (delimiter is '"' or '\r' or '\n')
        {
            throw new ArgumentException("A quote or a line break cannot be the delimiter.", nameof(delimiter));
        }

        var body = text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        var effective = delimiter ?? DetectDelimiter(body);
        IReadOnlyList<IReadOnlyList<string>> raw = effective == '\t'
            ? SpreadsheetText.SplitTsv(body)
            : [.. DelimitedText.Parse(body, effective).Select(r => r.Cells)];
        return Assemble(Prepare(raw, source, mergedRows: null), header, headerIndex: null, source);
    }

    /// <summary>Reads an HTML table (the table with the most rows; nested tables skipped) – tests and pasted <c>.html</c> files.</summary>
    /// <exception cref="InvalidDataException">The HTML is nested too deeply to be parsed safely.</exception>
    public static InputTable ReadHtml(string html, HeaderMode header)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = HtmlNesting.ParseDocument(html) ?? throw new InvalidDataException("The HTML is nested too deeply.");
        return HtmlTableReader.Read(document) is { } table ? FromHtml(table, header) : EmptyTable(TableSource.ClipboardHtml);
    }

    /// <summary>
    /// Reads the first visible non-empty sheet of an <c>.xlsx</c> workbook from memory (zip-bomb limits of the U5 reader;
    /// never a temp file), skipping hidden rows.
    /// </summary>
    /// <remarks>
    /// Other visible non-empty sheets → <see cref="TableWarningCode.OtherSheetsIgnored"/> (their count); hidden rows →
    /// <see cref="TableWarningCode.HiddenRowsIgnored"/> with the count the U5 reader reports for the visible sheets (in the
    /// usual one-sheet workbook: of that sheet). Numbers come as Excel shows them (number formats such as <c>00-000</c>).
    /// </remarks>
    /// <exception cref="InvalidDataException">The file is larger than 50 MiB, a zip bomb or not a readable workbook.</exception>
    public static InputTable ReadXlsx(byte[] content, HeaderMode header)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > ContactFileReader.MaxFileBytes)
        {
            throw new InvalidDataException("The file is larger than 50 MiB.");
        }

        XlsxWorkbook workbook;
        try
        {
            using var zip = SafeZip.Open(content);
            workbook = XlsxReader.ReadSheets(zip);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("The file is not well-formed XML or uses a prohibited feature (DTD).", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException("The package uses a feature that is not supported (compression method).", ex);
        }

        var nonEmpty = workbook.Sheets.Where(s => s.Any(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))).ToList();
        var read = Prepare(nonEmpty.Count > 0 ? nonEmpty[0] : [], TableSource.XlsxFile, mergedRows: null);
        if (nonEmpty.Count > 1)
        {
            read.Global.Add(new TableWarning(TableWarningCode.OtherSheetsIgnored, Count: nonEmpty.Count - 1));
        }

        if (workbook.HiddenRows > 0)
        {
            read.Global.Add(new TableWarning(TableWarningCode.HiddenRowsIgnored, Count: workbook.HiddenRows));
        }

        return Assemble(read, header, headerIndex: null, TableSource.XlsxFile);
    }

    /// <summary>
    /// Appends <paramref name="added"/> to <paramref name="existing"/>: by folded header when both have headers (unknown
    /// headers become new columns), otherwise by position; row numbers continue at <see cref="InputTable.NextRowNumber"/>.
    /// An empty <paramref name="existing"/> returns <paramref name="added"/>.
    /// </summary>
    /// <remarks>
    /// A header of <paramref name="added"/> that <paramref name="existing"/> cannot use (it has none) becomes its first
    /// appended row. Warnings of <paramref name="added"/> keep their rows (renumbered) and columns (remapped); those of a
    /// column that matched an existing one are dropped (the existing column has its own). The limits apply to the result.
    /// </remarks>
    public static InputTable Append(InputTable existing, InputTable added)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(added);
        if (existing.Rows.Count == 0 && existing.Columns.Count == 0)
        {
            return added;
        }

        if (added.Rows.Count == 0 && added.Columns.Count == 0)
        {
            return existing;
        }

        var mapping = MapColumns(existing, added);
        var columns = existing.Columns.Concat(mapping.NewColumns).ToList();
        var warnings = existing.Warnings.ToList();
        var droppedColumns = 0;
        if (columns.Count > MaxColumns)
        {
            droppedColumns = columns.Count - MaxColumns;
            columns = columns.Take(MaxColumns).ToList();
            warnings.Add(new TableWarning(TableWarningCode.TooManyColumns, Count: droppedColumns));
        }

        var rows = existing.Rows.ToList();
        var numbers = new Dictionary<int, int>();
        var number = existing.NextRowNumber;
        var droppedRows = 0;
        foreach (var (source, cells) in mapping.AddedLines(added))
        {
            if (rows.Count >= MaxRows)
            {
                droppedRows++;
                continue;
            }

            var mapped = Place(cells, mapping.Map, columns.Count);
            if (mapped.Length == 0)
            {
                continue;
            }

            if (source is { } original)
            {
                numbers[original] = number;
            }

            rows.Add(new TableRow(number++, mapped));
        }

        if (droppedRows > 0)
        {
            warnings.Add(new TableWarning(TableWarningCode.TooManyRows, Count: droppedRows));
        }

        foreach (var warning in added.Warnings)
        {
            int? row = null;
            if (warning.Row is { } addedRow)
            {
                if (!numbers.TryGetValue(addedRow, out var renumbered))
                {
                    continue;
                }

                row = renumbered;
            }

            int? column = null;
            if (warning.Column is { } addedColumn)
            {
                var target = addedColumn < mapping.Map.Length ? mapping.Map[addedColumn] : -1;
                if (target < 0 || target >= columns.Count || (warning.Row is null && target < existing.Columns.Count))
                {
                    continue;
                }

                column = target;
            }

            warnings.Add(warning with { Row = row, Column = column });
        }

        return existing with { Columns = columns, Rows = rows, Warnings = warnings, NextRowNumber = Math.Max(number, existing.NextRowNumber) };
    }

    /// <summary>The number of rows of <paramref name="added"/> whose normalized cells equal a row already in <paramref name="existing"/>.</summary>
    /// <remarks>Rows are compared as <see cref="Append"/> would place them (by header or by position), cell by cell (ordinal).</remarks>
    public static int CountRepeatedRows(InputTable existing, InputTable added)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(added);
        if (existing.Rows.Count == 0 || added.Rows.Count == 0)
        {
            return 0;
        }

        var mapping = MapColumns(existing, added);
        var width = existing.Columns.Count + mapping.NewColumns.Count;
        var loaded = new HashSet<string>(existing.Rows.Select(r => Key(Trim([.. Enumerable.Range(0, existing.Columns.Count).Select(r.Cell)]))), StringComparer.Ordinal);
        var repeated = 0;
        foreach (var (_, cells) in mapping.AddedLines(added))
        {
            var placed = Place(cells, mapping.Map, width);
            if (placed.Length > existing.Columns.Count || placed.Length == 0)
            {
                continue;
            }

            if (loaded.Contains(Key(placed)))
            {
                repeated++;
            }
        }

        return repeated;
    }

    /// <summary>
    /// Applies another header choice without reading again: the header becomes a data row, a data row (or a title row,
    /// <paramref name="headerLine"/>) becomes the header; rows are renumbered from 1.
    /// </summary>
    /// <remarks>
    /// The lines are the table's <see cref="InputTable.TitleRows"/>, its header line (with the <c> (2)</c> of renamed
    /// duplicate headers removed) and its rows, in that order; <paramref name="headerLine"/> is 1-based in them (rows skipped
    /// while reading are not counted). <see cref="HeaderMode.NoHeader"/>: every line is data, title rows included;
    /// <see cref="HeaderMode.FirstRowIsHeader"/>: <paramref name="headerLine"/>, else the current header, else the first line;
    /// <see cref="HeaderMode.Auto"/>: <paramref name="headerLine"/>, else detected again. Cell warnings move with their cells.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="headerLine"/> is not a line of the table.</exception>
    public static InputTable Reinterpret(InputTable table, HeaderMode header, int? headerLine = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var read = new PreparedRead();
        foreach (var title in table.TitleRows)
        {
            read.Lines.Add(new Line(Trim([.. title])));
        }

        var renamed = table.Warnings.Where(w => w.Code == TableWarningCode.DuplicateHeader && w.Row is null && w.Column is not null)
            .Select(w => w.Column!.Value)
            .ToHashSet();
        var headerIndex = -1;
        if (table.HasHeader)
        {
            headerIndex = read.Lines.Count;
            var cells = table.Columns.Select(c => renamed.Contains(c.Index) ? DuplicateSuffix().Replace(c.Header, string.Empty) : c.Header).ToArray();
            var line = new Line(Trim(cells));
            foreach (var warning in table.Warnings.Where(w => w.Row is null && w.Column is not null && CellCodes.Contains(w.Code)))
            {
                line.Flag(warning.Code, warning.Column!.Value);
            }

            read.Lines.Add(line);
        }

        var byRow = table.Warnings.Where(w => w.Row is not null).ToLookup(w => w.Row!.Value);
        foreach (var row in table.Rows)
        {
            var line = new Line(Trim([.. Enumerable.Range(0, table.Columns.Count).Select(row.Cell)]));
            foreach (var warning in byRow[row.Number])
            {
                if (warning.Code == TableWarningCode.MergedCells)
                {
                    line.Merged = true;
                }
                else if (warning.Column is { } column && CellCodes.Contains(warning.Code))
                {
                    line.Flag(warning.Code, column);
                }
            }

            read.Lines.Add(line);
        }

        foreach (var warning in table.Warnings)
        {
            switch (warning.Code)
            {
                case TableWarningCode.TooManyRows:
                    read.OverflowRows += warning.Count;
                    break;
                case TableWarningCode.TooManyColumns:
                    read.ExtraColumns += warning.Count;
                    break;
                case TableWarningCode.EmptyRowsSkipped:
                    read.SkippedRows += warning.Count;
                    break;
                case var code when ReadCodes.Contains(code):
                    read.Global.Add(warning);
                    break;
            }
        }

        int? index = null;
        if (headerLine is { } chosen)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(chosen, 1, nameof(headerLine));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(chosen, read.Lines.Count, nameof(headerLine));
            index = chosen - 1;
        }
        else if (header == HeaderMode.FirstRowIsHeader && headerIndex >= 0)
        {
            index = headerIndex;
        }

        return Assemble(read, header, header == HeaderMode.NoHeader ? null : index, table.Source);
    }

    /// <summary>
    /// The 0-based index of the header among the first (up to five) non-empty lines, or null when they are all data
    /// (<see cref="HeaderMode.Auto"/>, 2.1).
    /// </summary>
    /// <remarks>
    /// The index is into <paramref name="firstRows"/> (empty lines are skipped, so with only non-empty lines both readings
    /// agree); pass up to ten lines – the five candidates and the lines the content rule looks at. The header is the
    /// candidate with the most known headers (<see cref="ColumnSynonyms"/>: the synonym table, the LDAP names of its
    /// targets, password-like and identifier headers) – at least a third of its non-empty cells – none of whose cells looks
    /// like data (an e-mail address, a phone number, an ISO or Polish date, a number, a frequent Polish given name). Without one, the first line is the
    /// header only when its cells are distinct, non-empty, without a digit and not data, and a column of the next five lines
    /// holds an e-mail address or a phone number. A single line is data unless every cell is a known header.
    /// </remarks>
    public static int? FindHeaderRow(IReadOnlyList<IReadOnlyList<string>> firstRows)
    {
        ArgumentNullException.ThrowIfNull(firstRows);
        var lines = new List<(int Index, List<string> Cells)>();
        for (var i = 0; i < firstRows.Count && lines.Count < HeaderScanLines; i++)
        {
            var cells = firstRows[i].Select(c => (c ?? string.Empty).Trim()).ToList();
            while (cells.Count > 0 && cells[^1].Length == 0)
            {
                cells.RemoveAt(cells.Count - 1);
            }

            if (cells.Count > 0)
            {
                lines.Add((i, cells));
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        if (lines.Count == 1)
        {
            var only = lines[0].Cells.Where(c => c.Length > 0).ToList();
            return only.All(ColumnSynonyms.IsKnownHeader) ? lines[0].Index : null;
        }

        int? best = null;
        var bestKnown = 0;
        foreach (var (index, cells) in lines.Take(5))
        {
            var filled = cells.Where(c => c.Length > 0).ToList();
            var known = filled.Count(ColumnSynonyms.IsKnownHeader);
            if (known == 0 || known * 3 < filled.Count || filled.Any(LooksLikeData))
            {
                continue;
            }

            if (known > bestKnown)
            {
                best = index;
                bestKnown = known;
            }
        }

        if (best is not null)
        {
            return best;
        }

        var first = lines[0].Cells;
        if (first.Any(c => c.Length == 0) || first.Distinct(StringComparer.OrdinalIgnoreCase).Count() != first.Count
            || first.Any(c => c.Any(char.IsDigit)) || first.Any(LooksLikeData))
        {
            return null;
        }

        var next = lines.Skip(1).Take(5).Select(l => l.Cells).ToList();
        var width = next.Count == 0 ? 0 : next.Max(c => c.Count);
        for (var column = 0; column < width; column++)
        {
            var at = column;
            if (next.Any(cells => at < cells.Count && (CellText.LooksLikeEmail(cells[at]) || CellText.LooksLikePhone(cells[at]))))
            {
                return lines[0].Index;
            }
        }

        return null;
    }

    /// <summary>The Excel letter of a 0-based column index: 0 → <c>A</c>, 25 → <c>Z</c>, 26 → <c>AA</c>, 701 → <c>ZZ</c>, 702 → <c>AAA</c>.</summary>
    public static string ColumnLetter(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var letters = new StringBuilder();
        var value = index + 1;
        while (value > 0)
        {
            var remainder = (value - 1) % 26;
            letters.Insert(0, (char)('A' + remainder));
            value = (value - 1) / 26;
        }

        return letters.ToString();
    }

    /// <summary>The <c> (2)</c> a renamed duplicate header got.</summary>
    [GeneratedRegex(@" \(\d+\)$", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicateSuffix();

    /// <summary>
    /// A cell no header has: an e-mail address, a phone number, a date, a number (2.1) – or a frequent Polish given name
    /// (<see cref="GivenNameLists.Polish"/>; a headerless HR sheet whose job titles and departments are header words, such as
    /// <em>Kierownik</em> and <em>Biuro</em>, is still data).
    /// </summary>
    private static bool LooksLikeData(string cell) =>
        CellText.LooksLikeEmail(cell) || CellText.LooksLikePhone(cell) || CellText.LooksLikeDate(cell) || CellText.LooksLikeNumber(cell)
        || GivenNameLists.Polish.Contains(SalutationKey.Of(cell));

    private static InputTable EmptyTable(TableSource source) => new([], [], false, source, []);

    private static InputTable FromHtml(HtmlTable table, HeaderMode header)
    {
        var read = Prepare(table.Rows, TableSource.ClipboardHtml, table.MergedRows);
        read.ExtraColumns = Math.Max(read.ExtraColumns, table.DroppedColumns);
        return Assemble(read, header, headerIndex: null, TableSource.ClipboardHtml);
    }

    /// <summary>The table of the clipboard's HTML format, or null when there is none or it cannot be read (the text is used then).</summary>
    private static HtmlTable? ReadClipboardHtml(byte[]? cfHtml, string? cfHtmlText)
    {
        try
        {
            CfHtmlDocument? document = null;
            if (cfHtml is { Length: > 0 } && cfHtml.Length <= CfHtmlParser.MaxBytes)
            {
                document = CfHtmlParser.Parse(cfHtml);
            }
            else if (!string.IsNullOrEmpty(cfHtmlText) && cfHtmlText.Length <= CfHtmlParser.MaxBytes)
            {
                document = CfHtmlParser.FromText(cfHtmlText);
            }

            return document is not null && HtmlNesting.ParseDocument(document.Html) is { } parsed ? HtmlTableReader.Read(parsed) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or DecoderFallbackException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The candidate delimiter most frequent in the first record outside quotes (ties: the order of the candidates).</summary>
    private static char DetectDelimiter(string text)
    {
        var candidates = CsvCodec.CandidateDelimiters;
        var counts = new int[candidates.Count];
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
            {
                continue;
            }

            if (c is '\r' or '\n')
            {
                break;
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                if (c == candidates[i])
                {
                    counts[i]++;
                }
            }
        }

        var best = -1;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0 && (best < 0 || counts[i] > counts[best]))
            {
                best = i;
            }
        }

        return best < 0 ? CsvCodec.DefaultDelimiter : candidates[best];
    }

    /// <summary>
    /// Normalizes the raw lines: formula protection (CSV files), <see cref="CellText.Normalize"/>, error values, the cell and
    /// column limits, scientific notation, trailing empty cells. Lines after the first <see cref="MaxRows"/> +
    /// <see cref="HeaderScanLines"/> non-empty ones are only counted (<see cref="PreparedRead.OverflowRows"/>).
    /// </summary>
    private static PreparedRead Prepare(IReadOnlyList<IReadOnlyList<string>> raw, TableSource source, IReadOnlySet<int>? mergedRows)
    {
        var read = new PreparedRead();
        var nonEmpty = 0;
        for (var i = 0; i < raw.Count; i++)
        {
            if (nonEmpty >= MaxRows + HeaderScanLines)
            {
                if (raw[i].Any(c => !string.IsNullOrWhiteSpace(c)))
                {
                    read.OverflowRows++;
                }

                continue;
            }

            var line = PrepareLine(raw[i], source, out var extra);
            read.ExtraColumns = Math.Max(read.ExtraColumns, extra);
            line.Merged = mergedRows?.Contains(i) == true;
            read.Lines.Add(line);
            if (!line.IsEmpty)
            {
                nonEmpty++;
            }
        }

        return read;
    }

    private static Line PrepareLine(IReadOnlyList<string> raw, TableSource source, out int extraColumns)
    {
        var cells = new string[raw.Count];
        List<(TableWarningCode, int)>? flags = null;
        for (var c = 0; c < raw.Count; c++)
        {
            var value = raw[c] ?? string.Empty;
            if (source == TableSource.CsvFile)
            {
                value = CsvCodec.RemoveFormulaProtection(value);
            }

            value = CellText.Normalize(value);
            if (value.Length == 0)
            {
                cells[c] = value;
                continue;
            }

            if (CellText.IsSpreadsheetError(value))
            {
                (flags ??= []).Add((TableWarningCode.FormulaError, c));
                cells[c] = string.Empty;
                continue;
            }

            if (value.Length > MaxCellLength)
            {
                var cut = char.IsHighSurrogate(value[MaxCellLength - 1]) ? MaxCellLength - 1 : MaxCellLength;
                value = value[..cut].TrimEnd();
                (flags ??= []).Add((TableWarningCode.CellTooLong, c));
            }

            if (CellText.IsScientificNotation(value))
            {
                (flags ??= []).Add((TableWarningCode.ScientificNotation, c));
            }

            cells[c] = value;
        }

        var trimmed = Trim(cells);
        extraColumns = Math.Max(0, trimmed.Length - MaxColumns);
        if (extraColumns > 0)
        {
            trimmed = trimmed[..MaxColumns];
        }

        var line = new Line(trimmed);
        foreach (var (code, column) in flags ?? [])
        {
            if (column < trimmed.Length || code == TableWarningCode.FormulaError && column < MaxColumns)
            {
                line.Flag(code, column);
            }
        }

        return line;
    }

    /// <summary>
    /// Takes the full value of scientific-notation cells from Excel's <c>x:num</c> when the HTML table has the shape of the
    /// text (lines × columns after the trailing empty ones are dropped).
    /// </summary>
    private static void Recover(PreparedRead text, HtmlTable html)
    {
        if (text.OverflowRows > 0 || !text.Lines.Any(l => l.Flags?.Any(f => f.Code == TableWarningCode.ScientificNotation) == true))
        {
            return;
        }

        var htmlLines = html.Rows.Select(r => Trim([.. r.Select(c => CellText.Normalize(c ?? string.Empty))])).ToList();
        if (Shape(text.Lines.Select(l => l.Cells).ToList()) != Shape(htmlLines))
        {
            return;
        }

        for (var i = 0; i < text.Lines.Count; i++)
        {
            var line = text.Lines[i];
            if (line.Flags is null || i >= html.Numbers.Count)
            {
                continue;
            }

            for (var f = 0; f < line.Flags.Count; f++)
            {
                var (code, column) = line.Flags[f];
                if (code == TableWarningCode.ScientificNotation && column < html.Numbers[i].Count && html.Numbers[i][column] is { } number)
                {
                    line.Cells[column] = number;
                    line.Flags[f] = (TableWarningCode.RecoveredFromHtml, column);
                }
            }
        }
    }

    private static (int Lines, int Columns) Shape(IReadOnlyList<string[]> lines)
    {
        var last = -1;
        var width = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length > 0)
            {
                last = i;
                width = Math.Max(width, lines[i].Length);
            }
        }

        return (last + 1, width);
    }

    /// <summary>
    /// The table of the prepared lines: the header (<paramref name="headerIndex"/>, else by <paramref name="mode"/>), the title
    /// rows above it, the data rows numbered from 1, the columns with their headers or letters and every warning.
    /// </summary>
    private static InputTable Assemble(PreparedRead read, HeaderMode mode, int? headerIndex, TableSource source)
    {
        var lines = read.Lines;
        var last = lines.FindLastIndex(l => !l.IsEmpty);
        int? header = null;
        if (mode != HeaderMode.NoHeader && last >= 0)
        {
            if (headerIndex is not null)
            {
                header = headerIndex;
            }
            else
            {
                var candidates = lines.Select((l, i) => (Line: l, Index: i)).Where(l => !l.Line.IsEmpty).Take(HeaderScanLines).ToList();
                header = mode == HeaderMode.FirstRowIsHeader
                    ? candidates[0].Index
                    : FindHeaderRow([.. candidates.Select(c => (IReadOnlyList<string>)c.Line.Cells)]) is { } found ? candidates[found].Index : null;
            }
        }

        var warnings = new List<TableWarning>();
        var titleRows = new List<IReadOnlyList<string>>();
        var headerCells = Array.Empty<string>();
        if (header is { } h)
        {
            titleRows.AddRange(lines.Take(h).Select(l => (IReadOnlyList<string>)l.Cells));
            headerCells = lines[h].Cells;
            if (h > 0)
            {
                warnings.Add(new TableWarning(TableWarningCode.TitleRowsSkipped, Count: h));
            }
        }

        var rows = new List<TableRow>();
        var rowWarnings = new List<TableWarning>();
        var skipped = read.SkippedRows;
        var dropped = read.OverflowRows;
        var start = header is { } at ? at + 1 : 0;
        for (var i = start; i <= last; i++)
        {
            var line = lines[i];
            if (line.IsEmpty)
            {
                // Empty lines before the first row of a table without a header are dropped silently.
                if (header is not null || rows.Count > 0)
                {
                    skipped++;
                }

                continue;
            }

            if (rows.Count >= MaxRows)
            {
                dropped++;
                continue;
            }

            var number = rows.Count + 1;
            rows.Add(new TableRow(number, line.Cells));
            if (line.Merged)
            {
                rowWarnings.Add(new TableWarning(TableWarningCode.MergedCells, number));
            }

            foreach (var (code, column) in line.Flags ?? [])
            {
                rowWarnings.Add(new TableWarning(code, number, column));
            }
        }

        var dataWidth = rows.Count == 0 ? 0 : rows.Max(r => r.Cells.Count);
        var width = Math.Max(headerCells.Length, dataWidth);

        if (dropped > 0)
        {
            warnings.Add(new TableWarning(TableWarningCode.TooManyRows, Count: dropped));
        }

        if (read.ExtraColumns > 0)
        {
            warnings.Add(new TableWarning(TableWarningCode.TooManyColumns, Count: read.ExtraColumns));
        }

        warnings.AddRange(read.Global);
        if (skipped > 0)
        {
            warnings.Add(new TableWarning(TableWarningCode.EmptyRowsSkipped, Count: skipped));
        }

        if (header is not null && dataWidth > headerCells.Length)
        {
            var first = rows.First(r => r.Cells.Count > headerCells.Length);
            warnings.Add(new TableWarning(TableWarningCode.RaggedRows, first.Number, headerCells.Length, dataWidth - headerCells.Length));
        }

        var columns = Columns(headerCells, width, warnings);
        if (header is { } headerLine)
        {
            if (lines[headerLine].Merged)
            {
                warnings.Add(new TableWarning(TableWarningCode.MergedCells));
            }

            warnings.AddRange((lines[headerLine].Flags ?? []).Where(f => f.Column < width).Select(f => new TableWarning(f.Code, Column: f.Column)));
        }

        // A cell warning past the last column (an error value emptied at the end of a short line) has no column to show.
        warnings.AddRange(rowWarnings.Where(w => w.Column is null || w.Column < width));
        return new InputTable(columns, rows, header is not null, source, warnings)
        {
            HeaderLine = header + 1,
            TitleRows = titleRows,
        };
    }

    /// <summary>The columns: the header (a duplicate renamed <c>Header (2)</c>), else the Excel letter; password-like headers warned.</summary>
    private static List<TableColumn> Columns(string[] headerCells, int width, List<TableWarning> warnings)
    {
        var columns = new List<TableColumn>(width);
        var used = new HashSet<string>(headerCells.Where(h => h.Length > 0), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < width; i++)
        {
            var name = i < headerCells.Length ? headerCells[i] : string.Empty;
            if (name.Length > 0 && !seen.Add(name))
            {
                var n = 2;
                string candidate;
                do
                {
                    candidate = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name} ({n++})");
                }
                while (used.Contains(candidate) || seen.Contains(candidate));

                seen.Add(candidate);
                name = candidate;
                warnings.Add(new TableWarning(TableWarningCode.DuplicateHeader, Column: i));
            }

            if (name.Length > 0 && ColumnSynonyms.IsPasswordLike(name))
            {
                warnings.Add(new TableWarning(TableWarningCode.PasswordLikeColumn, Column: i));
            }

            columns.Add(new TableColumn(i, ColumnLetter(i), name));
        }

        return columns;
    }

    /// <summary>How the columns of <paramref name="added"/> land in <paramref name="existing"/> (<see cref="Append"/>).</summary>
    private static ColumnPlacement MapColumns(InputTable existing, InputTable added)
    {
        var map = new int[added.Columns.Count];
        var newColumns = new List<TableColumn>();
        var next = existing.Columns.Count;
        var names = new HashSet<string>(existing.Columns.Select(c => c.Header).Where(h => h.Length > 0), StringComparer.OrdinalIgnoreCase);
        TableColumn NewColumn(string header)
        {
            var name = header;
            for (var n = 2; name.Length > 0 && !names.Add(name); n++)
            {
                name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{header} ({n})");
            }

            var column = new TableColumn(next, ColumnLetter(next), name);
            next++;
            newColumns.Add(column);
            return column;
        }

        if (existing.HasHeader && added.HasHeader)
        {
            var byHeader = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var column in existing.Columns.Where(c => c.Header.Length > 0))
            {
                byHeader.TryAdd(ColumnSynonyms.Fold(column.Header), column.Index);
            }

            var taken = new HashSet<int>();
            foreach (var column in added.Columns)
            {
                var folded = ColumnSynonyms.Fold(column.Header);
                map[column.Index] = column.Header.Length > 0 && byHeader.TryGetValue(folded, out var index) && taken.Add(index)
                    ? index
                    : NewColumn(column.Header).Index;
            }

            return new ColumnPlacement(map, newColumns, prependHeader: false);
        }

        foreach (var column in added.Columns)
        {
            map[column.Index] = column.Index < existing.Columns.Count
                ? column.Index
                : NewColumn(existing.HasHeader && added.HasHeader ? column.Header : string.Empty).Index;
        }

        return new ColumnPlacement(map, newColumns, prependHeader: added.HasHeader && !existing.HasHeader);
    }

    /// <summary><paramref name="cells"/> at their mapped columns (trailing empty cells dropped).</summary>
    private static string[] Place(IReadOnlyList<string> cells, int[] map, int width)
    {
        var placed = new string[width];
        Array.Fill(placed, string.Empty);
        for (var i = 0; i < cells.Count && i < map.Length; i++)
        {
            if (map[i] >= 0 && map[i] < width)
            {
                placed[map[i]] = cells[i];
            }
        }

        return Trim(placed);
    }

    private static string Key(string[] cells) => string.Join('\u001F', cells);

    /// <summary>The cells without the trailing empty ones.</summary>
    private static string[] Trim(string[] cells)
    {
        var length = cells.Length;
        while (length > 0 && string.IsNullOrEmpty(cells[length - 1]))
        {
            length--;
        }

        return length == cells.Length ? cells : cells[..length];
    }

    /// <summary>One prepared line: normalized cells without trailing empty ones and the warnings of its cells.</summary>
    private sealed class Line(string[] cells)
    {
        public string[] Cells { get; } = cells;

        public List<(TableWarningCode Code, int Column)>? Flags { get; private set; }

        public bool Merged { get; set; }

        public bool IsEmpty => Cells.Length == 0;

        public void Flag(TableWarningCode code, int column) => (Flags ??= []).Add((code, column));

        public override string ToString() => $"Line: {Cells.Length} cells";
    }

    /// <summary>The prepared lines of one read and what was counted while reading them.</summary>
    private sealed class PreparedRead
    {
        public List<Line> Lines { get; } = [];

        /// <summary>Non-empty lines beyond what is read at all (counted into <see cref="TableWarningCode.TooManyRows"/>).</summary>
        public int OverflowRows { get; set; }

        /// <summary>The most cells of a line beyond <see cref="MaxColumns"/>.</summary>
        public int ExtraColumns { get; set; }

        /// <summary>Empty middle rows skipped by an earlier read (<see cref="Reinterpret"/>).</summary>
        public int SkippedRows { get; set; }

        /// <summary>Warnings of the whole read (other sheets, hidden rows).</summary>
        public List<TableWarning> Global { get; } = [];

        public override string ToString() => $"Prepared read: {Lines.Count} lines";
    }

    /// <summary>The placement of appended columns: added index → result index, the new columns, whether the added header becomes a row.</summary>
    private sealed class ColumnPlacement(int[] map, List<TableColumn> newColumns, bool prependHeader)
    {
        public int[] Map { get; } = map;

        public IReadOnlyList<TableColumn> NewColumns { get; } = newColumns;

        /// <summary>The lines of <paramref name="added"/> to append: its header first when it becomes a row, then its rows (with their numbers).</summary>
        public IEnumerable<(int? Number, IReadOnlyList<string> Cells)> AddedLines(InputTable added)
        {
            if (prependHeader)
            {
                yield return (null, added.Columns.Select(c => c.Header).ToArray());
            }

            foreach (var row in added.Rows)
            {
                yield return (row.Number, row.Cells);
            }
        }

        public override string ToString() => $"Column placement: {Map.Length} columns, {NewColumns.Count} new";
    }
}
