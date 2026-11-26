using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using AdMng.Core.Csv;
using AdMng.Core.PowerShell;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PowerShell;

/// <summary>
/// One row of the grid of <c>Out-AdmGrid</c>: the text of its cells, and the distinguished name when the objects had one.
/// The view binds a cell to <c>row["c3"]</c> (the column keys of <see cref="PowerShellGridViewModel.ColumnKey"/>). Values the
/// directory returned: in memory only, for as long as the window shows them; <see cref="ToString"/> shows none of them.
/// </summary>
public sealed class PowerShellGridRow
{
    private string? _searchKey;

    internal PowerShellGridRow(IReadOnlyList<string?> cells, string? distinguishedName)
    {
        Cells = cells;
        DistinguishedName = distinguishedName;
    }

    public IReadOnlyList<string?> Cells { get; }

    /// <summary>The distinguished name of the object of this row; null when the objects had none (the row cannot be opened in the program).</summary>
    public string? DistinguishedName { get; }

    /// <summary>The first cell that has a value: what a screen reader calls the row.</summary>
    public string Summary => Cells.FirstOrDefault(cell => !string.IsNullOrEmpty(cell)) ?? string.Empty;

    /// <summary>The text of the cell of the column with this key ("c0", "c1", ...); empty for no value.</summary>
    public string this[string key] =>
        PowerShellGridViewModel.IndexOf(key) is var index and >= 0 && index < Cells.Count ? Cells[index] ?? string.Empty : string.Empty;

    /// <summary>The cells folded for the filter (lower case, no diacritics), made on first use.</summary>
    internal string SearchKey => _searchKey ??= TextFolding.Fold(string.Join('\n', Cells.Where(cell => !string.IsNullOrEmpty(cell))));

    /// <summary>The type only: the cells hold directory values.</summary>
    public override string ToString() => nameof(PowerShellGridRow);
}

/// <summary>
/// The grid window of <c>Out-AdmGrid</c> (track R, stage R1, decision D6): the objects of a pipeline as rows - sorted by a click
/// on a column, narrowed by a filter, copied, exported as CSV with protected formulas and opened in the program by their
/// distinguished name - in the lists' own column header and row styles. The rows are the plain text the command made of the
/// objects; nothing is written anywhere but the file the operator chooses (CLAUDE.md rule 1).
/// </summary>
/// <remarks>
/// Why it is not the object list of the main window: that list (<c>ObjectListViewModel</c>) is a view of a directory location - a
/// container, a search, a view - that it reads and refreshes by itself, with live updates and virtual lists; the rows of a
/// pipeline are not a location, and their cells are text the engine already formatted, which the list's attribute formatter
/// would format a second time. The columns, headers and sort glyphs are the list's (<c>ColumnViewModel</c>,
/// <c>b:DynamicColumns</c>), so the grid looks and sorts like it.
/// </remarks>
public sealed partial class PowerShellGridViewModel : WindowViewModelBase, IHasPageStatus
{
    /// <summary>The most property windows one "open" asks for: a selection of a thousand rows is not a thousand windows.</summary>
    public const int MaxOpened = 10;

    private readonly ICsvCodec _csv;
    private readonly PowerShellGridTable _table;
    private readonly PowerShellGridRow[] _all;
    private readonly string _domain;
    private CancellationTokenSource? _refresh;
    private int _sortColumn = -1;
    private bool _descending;
    private IReadOnlyList<PowerShellGridRow> _selection = [];

    public PowerShellGridViewModel(DialogServices services, ICsvCodec csv, PowerShellGridTable table)
        : base(services)
    {
        _csv = csv ?? throw new ArgumentNullException(nameof(csv));
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _domain = Session?.Info.DomainDnsName ?? string.Empty;
        Status = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        for (var index = 0; index < table.Columns.Count; index++)
        {
            Columns.Add(new ColumnViewModel(ColumnKey(index), table.Columns[index]));
        }

        var dn = table.DistinguishedNameColumn;
        _all = [.. table.Rows.Select(cells => new PowerShellGridRow(cells, dn >= 0 && !string.IsNullOrWhiteSpace(cells[dn]) ? cells[dn] : null))];
        Rows.ReplaceAll(_all);
        UpdateSummary();
        UpdateLasting();
    }

    /// <summary>The status line: the summary of the grid, an export that runs and how it ended, the end of the session.</summary>
    public PageStatusHolder Status { get; }

    /// <summary>The columns, in the order of the table: the list's own column view model, so the header, sort glyph and width are the list's.</summary>
    public ObservableCollection<ColumnViewModel> Columns { get; } = [];

    /// <summary>The rows shown: filtered and sorted. Changed in one step.</summary>
    public BulkObservableCollection<PowerShellGridRow> Rows { get; } = [];

    /// <summary>The title of the command ("Results" when it gave none).</summary>
    public string Title => _table.Title ?? Localizer[L.PowerShellGrid.Title];

    public override string WindowTitle => string.IsNullOrEmpty(_domain)
        ? Title
        : Localizer.Format(L.PowerShellGrid.WindowTitle, Title, _domain);

    /// <summary>All the rows of the table, whatever the filter says.</summary>
    public int TotalRows => _all.Length;

    /// <summary>The objects had distinguished names: a row can be opened in the program.</summary>
    public bool CanOpenObjects => _table.DistinguishedNameColumn >= 0;

    /// <summary>What the operator typed to narrow the rows: a text that every shown row contains, ignoring case and diacritics.</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>The selected rows, as the list reports them.</summary>
    public IReadOnlyList<PowerShellGridRow> Selection => _selection;

    /// <summary>The key of a column: what the view binds a cell to (<c>row["c3"]</c>) - never the property name, which may hold anything.</summary>
    public static string ColumnKey(int index) => "c" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The index a key names, or -1.</summary>
    internal static int IndexOf(string key) =>
        key.Length > 1 && key[0] == 'c' && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : -1;

    /// <summary>The view reports the selection (the rows the list selects, in the order the list shows them).</summary>
    public void SetSelection(IEnumerable? items)
    {
        _selection = items is null ? [] : [.. items.OfType<PowerShellGridRow>()];
        OnPropertyChanged(nameof(Selection));
        OpenCommand.NotifyCanExecuteChanged();
        CopyRowsCommand.NotifyCanExecuteChanged();
        CopyRowsWithHeadersCommand.NotifyCanExecuteChanged();
        CopyDnCommand.NotifyCanExecuteChanged();
    }

    // ---- Filter and sort --------------------------------------------------------------------------------------

    partial void OnFilterTextChanged(string value) => _ = RefreshAsync();

    /// <summary>A click on a column header: sorts by it, ascending, then descending on the next click. Rows without a value go last.</summary>
    [RelayCommand]
    private Task SortAsync(ColumnViewModel? column)
    {
        var index = column is null ? -1 : Columns.IndexOf(column);
        if (index < 0)
        {
            return Task.CompletedTask;
        }

        _descending = index == _sortColumn && !_descending;
        _sortColumn = index;
        for (var i = 0; i < Columns.Count; i++)
        {
            Columns[i].SortDirection = i != index ? SortDirection.None : _descending ? SortDirection.Descending : SortDirection.Ascending;
        }

        return RefreshAsync();
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    /// <summary>Filters and sorts off the UI thread; a newer filter or sort replaces the one that still runs.</summary>
    private async Task RefreshAsync()
    {
        var mine = new CancellationTokenSource();
        Interlocked.Exchange(ref _refresh, mine)?.Cancel();
        var token = mine.Token;
        var filter = TextFolding.Fold(FilterText.Trim());
        var column = _sortColumn;
        var descending = _descending;
        var culture = Localizer.Culture;
        try
        {
            var shown = await Task.Run(() => Compute(filter, column, descending, culture, token), token).ConfigureAwait(true);
            if (token.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            Rows.ReplaceAll(shown);
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer filter or sort, or the window closed.
        }
    }

    private List<PowerShellGridRow> Compute(string filter, int column, bool descending, CultureInfo culture, CancellationToken token)
    {
        var rows = new List<PowerShellGridRow>(_all.Length);
        var counter = 0;
        foreach (var row in _all)
        {
            if ((++counter & 1023) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            if (filter.Length == 0 || row.SearchKey.Contains(filter, StringComparison.Ordinal))
            {
                rows.Add(row);
            }
        }

        token.ThrowIfCancellationRequested();
        if (column >= 0)
        {
            var comparer = new CellComparer(column, descending, culture);
            rows = [.. rows.OrderBy(row => row, comparer)];
        }

        return rows;
    }

    private void UpdateSummary()
    {
        var rows = PageStatusText.Count(Localizer, _all.Length, L.PowerShellGrid.RowsOne, L.PowerShellGrid.RowsFew, L.PowerShellGrid.RowsMany);
        var columns = PageStatusText.Count(Localizer, Columns.Count, L.PowerShellGrid.ColumnsOne, L.PowerShellGrid.ColumnsFew, L.PowerShellGrid.ColumnsMany);
        Status.SetIdle(Rows.Count == _all.Length
            ? Localizer.Format(L.PowerShellGrid.Summary, rows, columns)
            : Localizer.Format(L.PowerShellGrid.Shown, Rows.Count, _all.Length, columns));
    }

    /// <summary>The session of the window ended: the grid keeps showing what it has, but an object can no longer be opened.</summary>
    private void UpdateLasting() => Status.SetLasting(SessionEndedStatus);

    protected override void OnSessionEnded()
    {
        UpdateLasting();
        OpenCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(Title));
        UpdateSummary();
        UpdateLasting();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _refresh, null)?.Cancel();
        }
    }

    // ---- Open, copy, export -------------------------------------------------------------------------------------

    private bool CanOpen() => !IsSessionEnded && !IsDisposed && _selection.Any(row => row.DistinguishedName is not null);

    /// <summary><i>Open in the program</i>: the properties of the selected objects (at most <see cref="MaxOpened"/> windows).</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        var names = _selection.Select(row => row.DistinguishedName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxOpened).ToList();
        foreach (var name in names)
        {
            await Services.Dialogs.ShowPropertiesAsync(name).ConfigureAwait(true);
        }
    }

    private bool HasSelection() => _selection.Count > 0;

    /// <summary>Ctrl+C: the selected rows as tab-separated text, as a list copies them.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyRows() => Copy(includeHeaders: false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyRowsWithHeaders() => Copy(includeHeaders: true);

    /// <summary>
    /// The columns are copied as the program's list copies them (audit ps-commands-4): a column with a secret - a password, the LAPS
    /// password, a BitLocker recovery password, an attribute the schema marks confidential - is left out and the notification names it;
    /// a secret is copied only after the explicit reveal of the Attribute Editor.
    /// </summary>
    private void Copy(bool includeHeaders)
    {
        var secret = SecretColumns();
        var kept = Enumerable.Range(0, Columns.Count).Where(index => !secret.Contains(index)).ToList();
        if (secret.Count > 0)
        {
            Services.Notifications.Show(NotificationSeverity.Warning, Localizer.Format(L.Copy.SecretColumnsLeftOut, string.Join(", ", secret.Select(index => Columns[index].Header))));
        }

        if (kept.Count == 0)
        {
            return;
        }

        var text = ListClipboardText.Format(
            [.. kept.Select(index => Columns[index].Header)],
            _selection.Select(row => (IReadOnlyList<string>)[.. kept.Select(index => Cell(row, index))]),
            includeHeaders);
        ClipboardCopy.Text(Services.Clipboard, Services.Notifications, text, Localizer.Format(L.PowerShellGrid.CopiedRows, _selection.Count));
    }

    /// <summary>The columns whose name is that of a secret attribute (<see cref="AdMng.Core.Schema.SecretAttributes.IsSecret"/>).</summary>
    private List<int> SecretColumns() =>
        [.. Enumerable.Range(0, Columns.Count).Where(index => AdMng.Core.Schema.SecretAttributes.IsSecret(Columns[index].Header, Session?.Schema))];

    private static string Cell(PowerShellGridRow row, int index) => index < row.Cells.Count ? row.Cells[index] ?? string.Empty : string.Empty;

    /// <summary>Ctrl+Shift+C: the distinguished names of the selected rows, one per line.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyDn()
    {
        var names = _selection.Select(row => row.DistinguishedName).OfType<string>().ToList();
        ClipboardCopy.Text(Services.Clipboard, Services.Notifications, string.Join(Environment.NewLine, names), Localizer.Format(L.Shell.CopiedDn, names.Count));
    }

    private bool HasRows() => Rows.Count > 0;

    /// <summary>
    /// Writes the rows as they are shown (filtered, in the order of the sort) to a CSV file the operator chooses: formulas
    /// protected, the list separator of the regional settings, UTF-8 with a signature - the export of every list.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private async Task ExportAsync()
    {
        // Columns with secrets are written only after the question of the program's list export; declined, they are left out
        // (audit ps-commands-4: the grid wrote the LAPS and BitLocker recovery passwords without a word).
        var kept = Enumerable.Range(0, Columns.Count).ToList();
        var secret = SecretColumns();
        if (secret.Count > 0)
        {
            var message = Localizer.Format(L.Files.ExportSecretColumns, string.Join(", ", secret.Select(index => Columns[index].Header)));
            if (!await Services.Dialogs.ConfirmAsync(Localizer[L.Batch.ExportPasswordsTitle], message, destructive: true).ConfigureAwait(true))
            {
                kept.RemoveAll(secret.Contains);
                if (kept.Count == 0)
                {
                    return;
                }
            }
        }

        var title = Localizer[L.Commands.ExportList];
        var path = await Services.Files.SaveFileAsync(
            title,
            ListExportSnapshot.SuggestFileName(Title, Services.Time.GetLocalNow()),
            [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var headers = kept.Select(index => Columns[index].Header).ToList();
        var shown = Rows.ToList();
        using var writing = Status.Begin(
            Localizer[L.PowerShellGrid.Exporting], new PageStatusLog(L.PowerShellGrid.Exporting), canCancel: true, writes: OperationWrites.Other);
        var token = writing.Token;
        var started = new StrongBox<bool>();
        try
        {
            // The separator of the operator's region (';' in Poland), whatever the language of the program. Stop ends the write between
            // two rows (audit TI-3: it could end it only before the file was opened).
            var delimiter = CsvFiles.ListSeparator(Services.Regional.Culture);
            await CsvFiles.WriteAsync(
                path,
                writer =>
                {
                    started.Value = true;
                    _csv.Write(writer, headers, shown.Select(row => Row(row, kept, token)), delimiter, protectFormulas: true);
                },
                token).ConfigureAwait(true);
            writing.CompleteDone(Localizer.Format(L.Files.Exported, shown.Count, Path.GetFileName(path)), new PageStatusLog(L.Files.Exported, shown.Count));
        }
        catch (OperationCanceledException) when (writing.IsCancelling)
        {
            // Stopped: the part written is the operator's export, written by this window, and is deleted (audit TI-3: the line stayed at
            // "Stopping…" and a part of the file was left). A file the write never opened is not touched.
            var deleted = !started.Value || await RemovePartAsync(path).ConfigureAwait(true);
            var name = Path.GetFileName(path);
            writing.CompleteAttention(
                Localizer.Format(deleted ? L.PowerShellGrid.ExportStopped : L.PowerShellGrid.ExportStoppedLeft, name),
                new PageStatusLog(deleted ? L.PowerShellGrid.ExportStopped : L.PowerShellGrid.ExportStoppedLeft));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            writing.CompleteError(
                PageStatusText.Failure(Localizer[L.PowerShellGrid.ExportFailed], Errors.Translate(ex), Localizer[L.Feedback.RetryNext]),
                new PageStatusLog(L.PowerShellGrid.ExportFailed));
        }
    }

    /// <summary>The cells of the columns written, in order; a stop ends the write before the next row.</summary>
    private static IReadOnlyList<string> Row(PowerShellGridRow row, List<int> kept, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return [.. kept.Select(index => Cell(row, index))];
    }

    /// <summary>Deletes the part of a stopped export, on a pool thread; false when it could not be deleted.</summary>
    private static Task<bool> RemovePartAsync(string path) => Task.Run(() =>
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    });

    /// <summary>Compares the cells of one column the way a grid does: numbers as numbers, text in the language's order, no value last.</summary>
    private sealed class CellComparer(int column, bool descending, CultureInfo culture) : IComparer<PowerShellGridRow>
    {
        public int Compare(PowerShellGridRow? left, PowerShellGridRow? right)
        {
            var a = Cell(left);
            var b = Cell(right);
            if (a.Length == 0 || b.Length == 0)
            {
                // A row without a value goes last in both directions.
                return a.Length == b.Length ? 0 : a.Length == 0 ? 1 : -1;
            }

            var order = decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                ? x.CompareTo(y)
                : string.Compare(a, b, culture, CompareOptions.IgnoreCase);
            return descending ? -order : order;
        }

        private string Cell(PowerShellGridRow? row) => row is not null && column < row.Cells.Count ? row.Cells[column] ?? string.Empty : string.Empty;
    }
}
