using AdMng.Core.Templates;

namespace AdMng.Core.Batch.Creation;

/// <summary>Where the rows of an <see cref="InputTable"/> came from (docs/dev/phase3-bulk-create.md 2.1).</summary>
public enum TableSource
{
    /// <summary>The Unicode text of the clipboard (tab-separated, Excel's quoting rule).</summary>
    ClipboardText,

    /// <summary>The HTML format of the clipboard (CF_HTML), read only when the text is not enough.</summary>
    ClipboardHtml,

    /// <summary>A CSV or <c>.txt</c> file (decoded by Presentation).</summary>
    CsvFile,

    /// <summary>An <c>.xlsx</c> workbook (the first visible non-empty sheet).</summary>
    XlsxFile,

    /// <summary>Rows typed in the wizard's grid.</summary>
    Typed,
}

/// <summary>Whether the first line of a pasted range is a header (<em>Pierwszy wiersz zawiera nagłówki</em>).</summary>
public enum HeaderMode
{
    /// <summary>Detected (<see cref="TableTextReader.FindHeaderRow"/>), also below a title row.</summary>
    Auto,

    /// <summary>The first line (or the chosen header line) is the header.</summary>
    FirstRowIsHeader,

    /// <summary>Every line is data; columns are named by their Excel letters.</summary>
    NoHeader,
}

/// <summary>One column of an <see cref="InputTable"/>.</summary>
/// <param name="Index">0-based position in the table (the index of <see cref="TableRow.Cells"/>).</param>
/// <param name="Letter">The Excel letter of the position (<c>A</c>, <c>B</c> … <c>AA</c>, <see cref="TableTextReader.ColumnLetter"/>).</param>
/// <param name="Header">The header text after normalisation; empty when the table has no header (or the cell was empty).</param>
public sealed record TableColumn(int Index, string Letter, string Header)
{
    /// <summary>The header, or the Excel letter when there is none.</summary>
    public string Name => string.IsNullOrEmpty(Header) ? Letter : Header;

    /// <summary>Position and letter only – a header is pasted data (rule 1, 2.8).</summary>
    public override string ToString() => $"Column {Index} ({Letter})";
}

/// <summary>One data row of an <see cref="InputTable"/>.</summary>
/// <param name="Number">
/// The wizard's row number: 1-based and stable – appended rows continue the numbering, removed rows leave gaps – and the
/// <see cref="ObjectPlan.SourceRow"/> of the object planned from it.
/// </param>
/// <param name="Cells">The normalized cells (<see cref="CellText.Normalize"/>), possibly shorter than the column count.</param>
public sealed record TableRow(int Number, IReadOnlyList<string> Cells)
{
    /// <summary>The cell at 0-based <paramref name="column"/>, or <c>""</c> past the end.</summary>
    public string Cell(int column) => column >= 0 && column < Cells.Count ? Cells[column] ?? string.Empty : string.Empty;

    /// <summary>The row number and the cell count – never a cell.</summary>
    public override string ToString() => $"Row {Number}: {Cells.Count} cells";
}

/// <summary>Findings of reading a table (shown localized as <c>BulkCreate.Warning.&lt;code&gt;</c>, 2.6).</summary>
public enum TableWarningCode
{
    /// <summary>A cell shows a number in scientific notation (<c>4,86E+11</c>, Excel's display of a long number).</summary>
    ScientificNotation,

    /// <summary>A scientific-notation cell took its full value from the HTML format (<c>x:num</c>; info).</summary>
    RecoveredFromHtml,

    /// <summary>Empty rows in the middle of the range were skipped (<see cref="TableWarning.Count"/>).</summary>
    EmptyRowsSkipped,

    /// <summary>Rows longer than the header extended the table with header-less columns.</summary>
    RaggedRows,

    /// <summary>Two columns have the same header; the second is named <c>Header (2)</c>.</summary>
    DuplicateHeader,

    /// <summary>Merged cells (<c>colspan</c> / <c>rowspan</c> of the HTML format).</summary>
    MergedCells,

    /// <summary>More than <see cref="TableTextReader.MaxRows"/> rows; the rest was dropped.</summary>
    TooManyRows,

    /// <summary>More than <see cref="TableTextReader.MaxColumns"/> columns; the rest was dropped.</summary>
    TooManyColumns,

    /// <summary>A cell longer than <see cref="TableTextReader.MaxCellLength"/> was cut.</summary>
    CellTooLong,

    /// <summary>A password-like column (2.1): never mapped, masked, unreachable from patterns.</summary>
    PasswordLikeColumn,

    /// <summary>The workbook has other non-empty sheets that were not read.</summary>
    OtherSheetsIgnored,

    /// <summary>Hidden rows of the sheet were skipped (<see cref="TableWarning.Count"/>).</summary>
    HiddenRowsIgnored,

    /// <summary>An Excel error value (<c>#N/D!</c>, <c>#VALUE!</c> …) was emptied.</summary>
    FormulaError,

    /// <summary>Lines above the detected header (a title, blank lines) were skipped (<see cref="TableWarning.Count"/>).</summary>
    TitleRowsSkipped,
}

/// <summary>A reading finding with its position and count – never a cell value.</summary>
/// <param name="Code">What was found.</param>
/// <param name="Row">The row number (<see cref="TableRow.Number"/>) when it concerns one row.</param>
/// <param name="Column">The 0-based column index when it concerns one column.</param>
/// <param name="Count">How many rows, columns or cells it concerns.</param>
public sealed record TableWarning(TableWarningCode Code, int? Row = null, int? Column = null, int Count = 1)
{
    public override string ToString() => $"{Code} row={Row?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} column={Column?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} count={Count}";
}

/// <summary>
/// A pasted or loaded table (2.1): columns with their headers or Excel letters, rows with stable numbers and the reading
/// findings. Lives only in memory of the wizard (rule 1); its <see cref="ToString"/> prints counts only.
/// </summary>
/// <param name="Columns">The columns in order (<see cref="TableColumn.Index"/> = position).</param>
/// <param name="Rows">The data rows in order.</param>
/// <param name="HasHeader">The table has a header line (column names are the headers).</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Warnings">What reading it found.</param>
public sealed record InputTable(
    IReadOnlyList<TableColumn> Columns,
    IReadOnlyList<TableRow> Rows,
    bool HasHeader,
    TableSource Source,
    IReadOnlyList<TableWarning> Warnings)
{
    private readonly int? _nextRowNumber;

    /// <summary>A table without columns and rows.</summary>
    public static InputTable Empty { get; } = new([], [], false, TableSource.Typed, []);

    /// <summary>
    /// The number the next appended or typed row gets: one above the highest row number (1 for an empty table), unless set
    /// higher – a table whose last rows were removed keeps their numbers reserved this way.
    /// </summary>
    public int NextRowNumber
    {
        get
        {
            var highest = 0;
            foreach (var row in Rows)
            {
                highest = Math.Max(highest, row.Number);
            }

            return Math.Max(highest + 1, _nextRowNumber ?? 1);
        }

        init => _nextRowNumber = value;
    }

    /// <summary>The 1-based line of the header in the pasted range (title rows counted); null without a header.</summary>
    public int? HeaderLine { get; init; }

    /// <summary>
    /// The lines above the header (a title such as <em>Nowi pracownicy – listopad 2026</em>, blank lines), kept only for
    /// <see cref="TableTextReader.Reinterpret"/>; they are never mapped.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> TitleRows { get; init; } = [];

    /// <summary>The cell of the row at 0-based position <paramref name="row"/> of <see cref="Rows"/> and the 0-based column, <c>""</c> past the end.</summary>
    public string Cell(int row, int column) => row >= 0 && row < Rows.Count ? Rows[row].Cell(column) : string.Empty;

    /// <summary>
    /// The column named <paramref name="nameOrLetter"/>: by header first (trimmed and case-insensitive, an exact match before
    /// a diacritics-insensitive one), then by Excel letter; null when none matches.
    /// </summary>
    public TableColumn? FindColumn(string nameOrLetter)
    {
        ArgumentNullException.ThrowIfNull(nameOrLetter);
        var name = nameOrLetter.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var exact = Columns.FirstOrDefault(c => c.Header.Length > 0 && string.Equals(c.Header.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var folded = Diacritics.Remove(name);
        var unaccented = Columns.FirstOrDefault(c => c.Header.Length > 0 && string.Equals(Diacritics.Remove(c.Header.Trim()), folded, StringComparison.OrdinalIgnoreCase));
        return unaccented ?? Columns.FirstOrDefault(c => string.Equals(c.Letter, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Counts only – never a header or a cell (rule 1, 2.8).</summary>
    public override string ToString() =>
        $"{Source}: {Rows.Count} rows, {Columns.Count} columns, header={HasHeader}, title rows={TitleRows.Count}, warnings={Warnings.Count}";
}
