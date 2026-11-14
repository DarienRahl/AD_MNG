using System.Collections;
using AdMng.Core.Batch.Creation;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// Step 1 <em>Dane</em> of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.6, owner B3): paste from Excel (text
/// first, the HTML format only when needed; appended unless empty or <em>Wklej i zastąp</em>; a repeated paste asks), a
/// CSV, <c>.txt</c> or <c>.xlsx</c> file read into memory, the header choice and the header line, the Excel row offset,
/// typed rows and columns, the summary and the localized reading warnings. Password-like columns are masked and read-only.
/// The output is <see cref="Table"/>; nothing is written anywhere (rule 1).
/// </summary>
/// <remarks>
/// Parsing, appending and re-reading the header run on the thread pool (rule 6); the commands are disabled meanwhile
/// (<see cref="IsBusy"/>). A cell edit, an added row or column, a removed row and an appended paste raise
/// <see cref="Changed"/> only (rows keep their numbers, so the check step keeps its edits and pins); a new table – another
/// header choice, <em>Wklej i zastąp</em>, <em>Wyczyść</em>, a file – raises <see cref="TableReplaced"/> first.
/// </remarks>
public sealed partial class BulkSourceViewModel : ObservableObject, IDisposable
{
    /// <summary>Files larger than this are refused (50 MiB).</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>What a password-like cell shows and copies.</summary>
    public const string Mask = "••••••••";

    /// <summary>Reading warnings listed one by one; the rest is counted (<see cref="L.BulkCreate.MoreWarnings"/>).</summary>
    internal const int ShownWarnings = 12;

    private InputTable? _pending;
    private int _pendingRepeated;
    private bool _syncing;
    private bool _disposed;

    /// <param name="services">The dialog services of the window (clipboard, file dialog, regional settings, dialogs).</param>
    public BulkSourceViewModel(DialogServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        var localizer = services.Localizer;
        HeaderModes = [.. Enum.GetValues<HeaderMode>().Select(m => new Option<HeaderMode>(m, localizer[BulkCreateTextKeys.HeaderModeKey(m)]))];
        localizer.CultureChanged += OnCultureChanged;
    }

    /// <summary>
    /// The table changed without losing the check step's state: a cell edit, an added row or column, an appended paste
    /// (rows keep their numbers, so edits and pins stay).
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The table was replaced: another header choice or header line, <em>Wklej i zastąp</em>, <em>Wyczyść</em>, a new file.
    /// The wizard drops the check step's edits, pins and created marks (<see cref="BulkCheckViewModel.OnInputReset"/>).
    /// </summary>
    public event EventHandler? TableReplaced;

    internal DialogServices Services { get; }

    /// <summary>The table the next steps work on (<see cref="InputTable.Empty"/> until something is pasted, loaded or typed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTable), nameof(HasHeader))]
    public partial InputTable Table { get; private set; } = InputTable.Empty;

    /// <summary>Something was pasted, loaded or typed (the empty-state hint is hidden).</summary>
    public bool HasTable => Table.Columns.Count > 0 || Table.Rows.Count > 0;

    /// <summary>The table has a header line (<em>Wiersz nagłówków</em> is offered).</summary>
    public bool HasHeader => Table.HasHeader;

    /// <summary>The rows of the source grid (editable cells; filled with <c>ReplaceAll</c>).</summary>
    public BulkObservableCollection<SourceRowViewModel> Rows { get; } = [];

    /// <summary>The columns of the source grid (header or letter, masked, read-only).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SourceColumnViewModel> Columns { get; private set; } = [];

    /// <summary><em>Pierwszy wiersz zawiera nagłówki</em>: <em>automatycznie / tak / nie</em>.</summary>
    public IReadOnlyList<Option<HeaderMode>> HeaderModes { get; }

    [ObservableProperty]
    public partial HeaderMode HeaderMode { get; set; } = HeaderMode.Auto;

    /// <summary><em>Wiersz nagłówków</em>: the 1-based line of the header in the pasted range; null = detected or none.</summary>
    [ObservableProperty]
    public partial int? HeaderLine { get; set; }

    /// <summary><em>Pierwszy wiersz danych w Excelu</em> (display only: the Excel row = number + offset − 1).</summary>
    [ObservableProperty]
    public partial int ExcelRowOffset { get; set; } = 2;

    /// <summary><em>Wiersze: 120, kolumny: 7 – nagłówki rozpoznane</em>.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>The localized reading warnings (<c>BulkCreate.Warning.&lt;code&gt;</c> with row and column).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>Reading or parsing runs (a busy indicator; the grid is not editable meanwhile).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PasteCommand), nameof(ReplaceWithPasteCommand), nameof(LoadFileCommand), nameof(AddRowCommand),
        nameof(AddColumnCommand), nameof(RemoveRowsCommand), nameof(ClearCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The localized problem of the last paste or file (clipboard busy, file too large …).</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>
    /// <em>Te wiersze są już wklejone</em>: every pasted row (or more than half of them) is already loaded; the operator
    /// chooses <see cref="AppendRepeatedCommand"/>, <see cref="ReplaceWithRepeatedCommand"/> or <see cref="CancelRepeatedCommand"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepeatedPaste))]
    [NotifyCanExecuteChangedFor(nameof(AppendRepeatedCommand), nameof(ReplaceWithRepeatedCommand), nameof(CancelRepeatedCommand))]
    public partial string? RepeatedPasteQuestion { get; private set; }

    public bool HasRepeatedPaste => RepeatedPasteQuestion is not null;

    /// <summary>The header of the column <see cref="AddColumnCommand"/> adds (typed by the operator).</summary>
    [ObservableProperty]
    public partial string NewColumnHeader { get; set; } = string.Empty;

    /// <summary>The background work started by a header choice (tests await it).</summary>
    internal Task PendingWork { get; private set; } = Task.CompletedTask;

    /// <summary>Ctrl+V: reads the clipboard table and appends it (an empty table is replaced; a repeated paste asks).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task PasteAsync() => PasteCoreAsync(replace: false);

    /// <summary><em>Wklej i zastąp</em>.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task ReplaceWithPasteAsync() => PasteCoreAsync(replace: true);

    /// <summary><em>Wczytaj plik…</em>: <c>.xlsx</c>, <c>.csv</c>, <c>.txt</c> (≤ <see cref="MaxFileBytes"/>), read on the thread pool.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task LoadFileAsync()
    {
        var localizer = Services.Localizer;
        var path = await Services.Files.OpenFileAsync(
            localizer[L.BulkCreate.OpenFileTitle],
            [new FileFilter(localizer[L.BulkCreate.FileFilter], "*.xlsx;*.csv;*.txt")]).ConfigureAwait(true);
        if (path is null || _disposed)
        {
            return;
        }

        if (Table.Rows.Count > 0
            && !await Services.Dialogs.ConfirmAsync(localizer[L.BulkCreate.LoadFileTitle], localizer.Format(L.BulkCreate.ConfirmReplaceWithFile, Table.Rows.Count), destructive: false).ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var header = HeaderMode;
            var ansiCodePage = Services.Regional.AnsiCodePage;
            var table = await Task.Run(() => ReadFileAsync(path, header, ansiCodePage)).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (table is null)
            {
                Error = localizer[L.BulkCreate.FileTooLarge];
                return;
            }

            if (table.Rows.Count == 0)
            {
                Error = localizer[L.BulkCreate.NoRowsRead];
                return;
            }

            await SetTableAsync(table, replaced: true).ConfigureAwait(true);
        }
        catch (InvalidDataException)
        {
            Error = localizer[L.BulkCreate.InvalidFile];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Services.Errors.Translate(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Adds an empty row (the next row number). An empty wizard starts a typed table with the usual columns
    /// (<em>Imię, Nazwisko, E-mail, Telefon komórkowy, Dział</em>) that the next step maps by itself.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddRow()
    {
        Error = null;
        if (Table.Columns.Count == 0)
        {
            var headers = Services.Localizer[L.BulkCreate.StarterHeaders].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var columns = headers.Select((h, i) => new TableColumn(i, TableTextReader.ColumnLetter(i), h)).ToList();
            var started = new InputTable(columns, [new TableRow(1, [.. headers.Select(_ => string.Empty)])], HasHeader: true, TableSource.Typed, [])
            {
                HeaderLine = 1,
            };
            ApplyTable(started, replaced: true, BuildRows(started));
            return;
        }

        var table = Table;
        var row = new TableRow(table.NextRowNumber, [.. table.Columns.Select(_ => string.Empty)]);
        var updated = table with { Rows = [.. table.Rows, row] };
        Table = updated;
        Rows.Add(new SourceRowViewModel(this, row, Rows.Count, Columns));
        RefreshSummary();
        RaiseChanged(replaced: false);
    }

    /// <summary>Adds a column with the header the operator typed (<paramref name="header"/>, else <see cref="NewColumnHeader"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddColumn(string? header)
    {
        Error = null;
        var text = CellText.Normalize(header ?? NewColumnHeader ?? string.Empty);
        var table = Table;
        var index = table.Columns.Count;
        if (index >= TableTextReader.MaxColumns)
        {
            Error = Services.Localizer.Format(L.BulkCreate.TooManyColumns, TableTextReader.MaxColumns);
            return;
        }

        var column = new TableColumn(index, TableTextReader.ColumnLetter(index), text);
        var updated = table with
        {
            Columns = [.. table.Columns, column],
            HasHeader = table.HasHeader || (table.Rows.Count == 0 && text.Length > 0),
            Source = table.Columns.Count == 0 ? TableSource.Typed : table.Source,
        };
        NewColumnHeader = string.Empty;
        ApplyTable(updated, replaced: false, BuildRows(updated));
    }

    /// <summary>Removes the selected rows (their numbers stay reserved).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveRows(IList? rows)
    {
        var numbers = rows?.OfType<SourceRowViewModel>().Select(r => r.Number).ToHashSet() ?? [];
        if (numbers.Count == 0)
        {
            return;
        }

        var table = Table;
        var updated = table with { Rows = [.. table.Rows.Where(r => !numbers.Contains(r.Number))], NextRowNumber = table.NextRowNumber };
        ApplyTable(updated, replaced: false, BuildRows(updated));
    }

    /// <summary><em>Wyczyść</em>: asks when rows are loaded.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ClearAsync()
    {
        var localizer = Services.Localizer;
        if (Table.Rows.Count > 0
            && !await Services.Dialogs.ConfirmAsync(localizer[L.BulkCreate.ClearTitle], localizer.Format(L.BulkCreate.ConfirmClear, Table.Rows.Count), destructive: true).ConfigureAwait(true))
        {
            return;
        }

        ClearPending();
        Error = null;
        ApplyTable(InputTable.Empty, replaced: true, []);
    }

    /// <summary><em>Dołącz mimo to</em>: the repeated rows are appended.</summary>
    [RelayCommand(CanExecute = nameof(HasRepeatedPaste))]
    private async Task AppendRepeatedAsync()
    {
        if (_pending is not { } pending)
        {
            return;
        }

        ClearPending();
        IsBusy = true;
        try
        {
            var existing = Table;
            var appended = await Task.Run(() => TableTextReader.Append(existing, pending)).ConfigureAwait(true);
            if (!_disposed)
            {
                await SetTableAsync(appended, replaced: false).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary><em>Zastąp</em>: the pasted rows replace the table.</summary>
    [RelayCommand(CanExecute = nameof(HasRepeatedPaste))]
    private async Task ReplaceWithRepeatedAsync()
    {
        if (_pending is not { } pending)
        {
            return;
        }

        ClearPending();
        await SetTableAsync(pending, replaced: true).ConfigureAwait(true);
    }

    /// <summary><em>Anuluj</em>: the paste is dropped.</summary>
    [RelayCommand(CanExecute = nameof(HasRepeatedPaste))]
    private void CancelRepeated() => ClearPending();

    private bool CanEdit() => !IsBusy && !_disposed;

    /// <summary>Drops the table (window closed; rule 1).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Services.Localizer.CultureChanged -= OnCultureChanged;
        ClearPending();
        Rows.Clear();
        Columns = [];
        Warnings = [];
        Table = InputTable.Empty;
        Changed = null;
        TableReplaced = null;
    }

    /// <summary>Counts only – never a cell or a header.</summary>
    public override string ToString() => $"Bulk create source: {Table}";

    // ---- Cells --------------------------------------------------------------------------------------------------

    /// <summary>A cell typed in the grid (normalized like a pasted one; a masked cell cannot be edited).</summary>
    internal void SetCell(SourceRowViewModel row, int column, string? value)
    {
        if (_disposed || IsBusy || column < 0 || column >= Table.Columns.Count || (column < Columns.Count && Columns[column].IsMasked))
        {
            return;
        }

        var text = CellText.Normalize(value ?? string.Empty);
        if (string.Equals(row.Cell(column), text, StringComparison.Ordinal))
        {
            return;
        }

        var table = Table;
        var index = row.Index;
        if (index < 0 || index >= table.Rows.Count || table.Rows[index].Number != row.Number)
        {
            index = IndexOf(table, row.Number);
            if (index < 0)
            {
                return;
            }
        }

        var cells = new string[Math.Max(table.Columns.Count, table.Rows[index].Cells.Count)];
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = table.Rows[index].Cell(i);
        }

        cells[column] = text;
        var rows = table.Rows.ToArray();
        rows[index] = new TableRow(row.Number, cells);
        Table = table with { Rows = rows };
        row.Update(rows[index]);
        RaiseChanged(replaced: false);
    }

    // ---- Paste ----------------------------------------------------------------------------------------------------

    private async Task PasteCoreAsync(bool replace)
    {
        var localizer = Services.Localizer;
        Error = null;
        ClearPending();
        IsBusy = true;
        try
        {
            // The clipboard is read on the UI thread (STA); the text is parsed on the thread pool.
            var content = Services.Clipboard.ReadTable();
            if (content is null || (string.IsNullOrWhiteSpace(content.Text) && content.CfHtml is not { Length: > 0 } && string.IsNullOrWhiteSpace(content.CfHtmlText)))
            {
                Error = localizer[L.BulkCreate.ClipboardEmpty];
                return;
            }

            var existing = Table;
            var append = !replace && existing.Rows.Count > 0;
            var header = append && HeaderMode != HeaderMode.NoHeader ? HeaderMode.Auto : HeaderMode;
            var (added, repeated) = await Task.Run(() =>
            {
                var read = TableTextReader.ReadClipboard(content.Text, content.CfHtml, content.CfHtmlText, header);
                return (read, append ? TableTextReader.CountRepeatedRows(existing, read) : 0);
            }).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (added.Rows.Count == 0)
            {
                Error = localizer[L.BulkCreate.NoRowsRead];
                return;
            }

            if (!append)
            {
                await SetTableAsync(added, replaced: true).ConfigureAwait(true);
                return;
            }

            if (repeated > 0 && (repeated == added.Rows.Count || repeated * 2 > added.Rows.Count))
            {
                _pending = added;
                _pendingRepeated = repeated;
                RepeatedPasteQuestion = RepeatedText();
                return;
            }

            var appended = await Task.Run(() => TableTextReader.Append(existing, added)).ConfigureAwait(true);
            if (!_disposed)
            {
                await SetTableAsync(appended, replaced: false).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string RepeatedText() => _pending is { } pending
        ? Services.Localizer.Format(L.BulkCreate.RepeatedPaste, _pendingRepeated, pending.Rows.Count)
        : string.Empty;

    private void ClearPending()
    {
        _pending = null;
        _pendingRepeated = 0;
        RepeatedPasteQuestion = null;
    }

    private static async Task<InputTable?> ReadFileAsync(string path, HeaderMode header, int ansiCodePage)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
        {
            return null;
        }

        if (string.Equals(info.Extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            return TableTextReader.ReadXlsx(bytes, header);
        }

        var text = await CsvFiles.ReadTextAsync(path, ansiCodePage).ConfigureAwait(false);
        return TableTextReader.ReadDelimited(text, null, header, TableSource.CsvFile);
    }

    // ---- Header ---------------------------------------------------------------------------------------------------

    partial void OnHeaderModeChanged(HeaderMode value)
    {
        if (_syncing || _disposed || !HasTable)
        {
            return;
        }

        PendingWork = ReinterpretAsync(value, null);
    }

    partial void OnHeaderLineChanged(int? value)
    {
        if (_syncing || _disposed || !HasTable)
        {
            return;
        }

        var mode = value is not null && HeaderMode == HeaderMode.NoHeader ? HeaderMode.FirstRowIsHeader : HeaderMode;
        PendingWork = ReinterpretAsync(mode, value);
    }

    private async Task ReinterpretAsync(HeaderMode mode, int? line)
    {
        var table = Table;
        Error = null;
        IsBusy = true;
        try
        {
            var lines = table.TitleRows.Count + (table.HasHeader ? 1 : 0) + table.Rows.Count;
            if (line is { } chosen && (chosen < 1 || chosen > lines))
            {
                Error = Services.Localizer.Format(L.BulkCreate.HeaderLineOutOfRange, chosen, lines);
                return;
            }

            var reread = await Task.Run(() => TableTextReader.Reinterpret(table, mode, line)).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (mode != HeaderMode)
            {
                Sync(() => HeaderMode = mode);
            }

            await SetTableAsync(reread, replaced: true).ConfigureAwait(true);
        }
        catch (ArgumentOutOfRangeException)
        {
            Error = Services.Localizer.Format(L.BulkCreate.HeaderLineOutOfRange, line ?? 0, table.TitleRows.Count + (table.HasHeader ? 1 : 0) + table.Rows.Count);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnExcelRowOffsetChanged(int value)
    {
        if (value < 1)
        {
            ExcelRowOffset = 1;
            return;
        }

        foreach (var row in Rows)
        {
            row.NotifyExcelRow();
        }

        RefreshWarnings();
    }

    // ---- Table ----------------------------------------------------------------------------------------------------

    /// <summary>Builds the grid rows on the thread pool (10 000 rows stay responsive) and shows the table.</summary>
    private async Task SetTableAsync(InputTable table, bool replaced)
    {
        var columns = ColumnsOf(table);
        var rows = await Task.Run(() => BuildRows(table, columns)).ConfigureAwait(true);
        if (!_disposed)
        {
            // Idle before the change events: the wizard re-evaluates Dalej on them (CanGoNext reads IsBusy), and a WPF
            // button asks CanExecute only then – a later IsBusy = false would leave Dalej disabled until the next edit.
            IsBusy = false;
            ApplyTable(table, replaced, rows, columns);
        }
    }

    private IReadOnlyList<SourceRowViewModel> BuildRows(InputTable table) => BuildRows(table, ColumnsOf(table));

    private List<SourceRowViewModel> BuildRows(InputTable table, IReadOnlyList<SourceColumnViewModel> columns)
    {
        var rows = new List<SourceRowViewModel>(table.Rows.Count);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            rows.Add(new SourceRowViewModel(this, table.Rows[i], i, columns));
        }

        return rows;
    }

    private void ApplyTable(InputTable table, bool replaced, IReadOnlyList<SourceRowViewModel> rows, IReadOnlyList<SourceColumnViewModel>? columns = null)
    {
        Table = table;
        Columns = columns ?? (rows.Count > 0 ? rows[0].ColumnSet : ColumnsOf(table));
        Rows.ReplaceAll(rows);
        if (replaced)
        {
            Sync(() =>
            {
                HeaderLine = table.HasHeader ? table.HeaderLine ?? 1 : null;
                ExcelRowOffset = table.HasHeader ? (table.HeaderLine ?? 1) + 1 : 1;
            });
            foreach (var row in Rows)
            {
                row.NotifyExcelRow();
            }
        }

        RefreshSummary();
        RaiseChanged(replaced);
    }

    private static IReadOnlyList<SourceColumnViewModel> ColumnsOf(InputTable table) =>
        [.. table.Columns.Select(c => new SourceColumnViewModel(c, c.Header.Trim().Length > 0 && ColumnSynonyms.IsPasswordLike(c.Header)))];

    private static int IndexOf(InputTable table, int number)
    {
        for (var i = 0; i < table.Rows.Count; i++)
        {
            if (table.Rows[i].Number == number)
            {
                return i;
            }
        }

        return -1;
    }

    private void RefreshSummary()
    {
        var table = Table;
        var localizer = Services.Localizer;
        Summary = !HasTable
            ? string.Empty
            : localizer.Format(
                !table.HasHeader ? L.BulkCreate.SummaryNoHeader
                : HeaderMode == HeaderMode.Auto ? L.BulkCreate.SummaryHeaderDetected
                : L.BulkCreate.SummaryHeader,
                table.Rows.Count,
                table.Columns.Count);
        RefreshWarnings();
    }

    private void RefreshWarnings()
    {
        var table = Table;
        var localizer = Services.Localizer;
        var culture = localizer.Culture;
        var texts = new List<string>();
        foreach (var warning in table.Warnings.Take(ShownWarnings))
        {
            var row = warning.Row is { } number
                ? (number + ExcelRowOffset - 1).ToString(culture)
                : Math.Max(1, ExcelRowOffset - 1).ToString(culture);
            var column = warning.Column is { } index && index >= 0 && index < table.Columns.Count ? table.Columns[index].Name : string.Empty;
            texts.Add(localizer.Format(BulkCreateTextKeys.WarningKey(warning.Code), row, column, warning.Count.ToString(culture)));
        }

        if (table.Warnings.Count > ShownWarnings)
        {
            texts.Add(localizer.Format(L.BulkCreate.MoreWarnings, table.Warnings.Count - ShownWarnings));
        }

        Warnings = texts;
    }

    private void RaiseChanged(bool replaced)
    {
        if (_disposed)
        {
            return;
        }

        if (replaced)
        {
            TableReplaced?.Invoke(this, EventArgs.Empty);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Sync(Action change)
    {
        _syncing = true;
        try
        {
            change();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RefreshSummary();
        if (_pending is not null)
        {
            RepeatedPasteQuestion = RepeatedText();
        }
    }
}

/// <summary>One row of the source grid (2.6): the wizard's row number and its editable cells.</summary>
/// <remarks>
/// The grid binds the cells through the indexer (<c>[2]</c>, two-way): reading gives the cell (the mask for a password-like
/// column), writing stores a typed value in the table (<see cref="BulkSourceViewModel.SetCell"/>).
/// </remarks>
public sealed partial class SourceRowViewModel : ObservableObject
{
    private readonly BulkSourceViewModel? _owner;
    private IReadOnlyList<string> _raw;

    /// <param name="number">The wizard's row number (<see cref="TableRow.Number"/>).</param>
    /// <param name="cells">The normalized cells in column order.</param>
    internal SourceRowViewModel(int number, IReadOnlyList<string> cells)
    {
        Number = number;
        _raw = cells ?? throw new ArgumentNullException(nameof(cells));
        Cells = cells;
        ColumnSet = [];
    }

    internal SourceRowViewModel(BulkSourceViewModel owner, TableRow row, int index, IReadOnlyList<SourceColumnViewModel> columns)
    {
        _owner = owner;
        Number = row.Number;
        Index = index;
        ColumnSet = columns;
        _raw = row.Cells;
        Cells = Display(row.Cells, columns);
    }

    public int Number { get; }

    /// <summary>The Excel row (<see cref="Number"/> + <see cref="BulkSourceViewModel.ExcelRowOffset"/> − 1).</summary>
    public int ExcelRow => _owner is null ? Number : Number + _owner.ExcelRowOffset - 1;

    /// <summary>The cells in column order (the grid binds them by index; masked columns show the mask instead).</summary>
    public IReadOnlyList<string> Cells { get; private set; }

    /// <summary>The cell shown at <paramref name="column"/> (two-way for the grid; masked cells cannot be written).</summary>
    public string this[int column]
    {
        get => column >= 0 && column < Cells.Count ? Cells[column] : string.Empty;
        set => _owner?.SetCell(this, column, value);
    }

    /// <summary>The position in <see cref="BulkSourceViewModel.Rows"/> when the grid was filled.</summary>
    internal int Index { get; }

    internal IReadOnlyList<SourceColumnViewModel> ColumnSet { get; }

    /// <summary>The real cell (never shown for a masked column).</summary>
    internal string Cell(int column) => column >= 0 && column < _raw.Count ? _raw[column] ?? string.Empty : string.Empty;

    internal void Update(TableRow row)
    {
        _raw = row.Cells;
        Cells = Display(row.Cells, ColumnSet);
        OnPropertyChanged(nameof(Cells));
        OnPropertyChanged("Item[]");
    }

    internal void NotifyExcelRow() => OnPropertyChanged(nameof(ExcelRow));

    /// <summary>The row number and the cell count – never a cell.</summary>
    public override string ToString() => $"Source row {Number}: {Cells.Count} cells";

    private static string[] Display(IReadOnlyList<string> cells, IReadOnlyList<SourceColumnViewModel> columns)
    {
        var width = Math.Max(cells.Count, columns.Count);
        var display = new string[width];
        for (var i = 0; i < width; i++)
        {
            var masked = i < columns.Count && columns[i].IsMasked;
            var cell = i < cells.Count ? cells[i] ?? string.Empty : string.Empty;
            display[i] = masked && cell.Length > 0 ? BulkSourceViewModel.Mask : cell;
        }

        return display;
    }
}

/// <summary>One column of the source grid (2.6): header or Excel letter, masked and read-only for password-like headers.</summary>
public sealed partial class SourceColumnViewModel : ObservableObject
{
    /// <param name="column">The table column.</param>
    /// <param name="isMasked">A password-like column (2.1): cells show <c>••••••••</c>, read-only, copied as the mask.</param>
    internal SourceColumnViewModel(TableColumn column, bool isMasked)
    {
        Column = column ?? throw new ArgumentNullException(nameof(column));
        IsMasked = isMasked;
    }

    public TableColumn Column { get; }

    public int Index => Column.Index;

    /// <summary>The header, or the Excel letter without one.</summary>
    public string Name => Column.Name;

    /// <summary>The Excel letter (shown above the header).</summary>
    public string Letter => Column.Letter;

    public bool IsMasked { get; }

    public bool IsReadOnly => IsMasked;

    /// <summary>Position, letter and the mask flag – never the header.</summary>
    public override string ToString() => $"Source column {Column.Index} ({Column.Letter}, masked={IsMasked})";
}
