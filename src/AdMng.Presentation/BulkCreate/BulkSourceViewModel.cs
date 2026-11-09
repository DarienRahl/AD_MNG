using System.Collections;
using AdMng.Core.Batch.Creation;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
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
/// <remarks>Contract stub: the properties have their defaults and the commands throw until B3 writes them.</remarks>
public sealed partial class BulkSourceViewModel : ObservableObject, IDisposable
{
    /// <summary>Files larger than this are refused (50 MiB).</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <param name="services">The dialog services of the window (clipboard, file dialog, regional settings, dialogs).</param>
    public BulkSourceViewModel(DialogServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        var localizer = services.Localizer;
        HeaderModes = [.. Enum.GetValues<HeaderMode>().Select(m => new Option<HeaderMode>(m, localizer[BulkCreateTextKeys.HeaderModeKey(m)]))];
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
    public partial InputTable Table { get; private set; } = InputTable.Empty;

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
    public partial bool IsBusy { get; private set; }

    /// <summary>The localized problem of the last paste or file (clipboard busy, file too large …).</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Ctrl+V: reads the clipboard table and appends it (an empty table is replaced; a repeated paste asks).</summary>
    [RelayCommand]
    private Task PasteAsync() => throw new NotImplementedException("F3 B3: BulkSourceViewModel.Paste (2.6).");

    /// <summary><em>Wklej i zastąp</em>.</summary>
    [RelayCommand]
    private Task ReplaceWithPasteAsync() => throw new NotImplementedException("F3 B3: BulkSourceViewModel.ReplaceWithPaste (2.6).");

    /// <summary><em>Wczytaj plik…</em>: <c>.xlsx</c>, <c>.csv</c>, <c>.txt</c> (≤ <see cref="MaxFileBytes"/>), read on the thread pool.</summary>
    [RelayCommand]
    private Task LoadFileAsync() => throw new NotImplementedException("F3 B3: BulkSourceViewModel.LoadFile (2.6).");

    [RelayCommand]
    private void AddRow() => throw new NotImplementedException("F3 B3: BulkSourceViewModel.AddRow (2.6).");

    /// <summary>Adds a column with the header the operator typed.</summary>
    [RelayCommand]
    private void AddColumn(string? header) => throw new NotImplementedException("F3 B3: BulkSourceViewModel.AddColumn (2.6).");

    /// <summary>Removes the selected rows (their numbers stay reserved).</summary>
    [RelayCommand]
    private void RemoveRows(IList? rows) => throw new NotImplementedException("F3 B3: BulkSourceViewModel.RemoveRows (2.6).");

    /// <summary><em>Wyczyść</em>.</summary>
    [RelayCommand]
    private void Clear() => throw new NotImplementedException("F3 B3: BulkSourceViewModel.Clear (2.6).");

    /// <summary>Drops the table (window closed; rule 1). Contract: nothing to drop yet.</summary>
    public void Dispose()
    {
    }

    /// <summary>Counts only – never a cell or a header.</summary>
    public override string ToString() => $"Bulk create source: {Table}";

    // Keeps the events referenced until B3 raises them.
    private void RaiseChanged(bool replaced)
    {
        if (replaced)
        {
            TableReplaced?.Invoke(this, EventArgs.Empty);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnTableChanged(InputTable value) => RaiseChanged(replaced: false);
}

/// <summary>One row of the source grid (2.6): the wizard's row number and its editable cells.</summary>
public sealed partial class SourceRowViewModel : ObservableObject
{
    /// <param name="number">The wizard's row number (<see cref="TableRow.Number"/>).</param>
    /// <param name="cells">The normalized cells in column order.</param>
    internal SourceRowViewModel(int number, IReadOnlyList<string> cells)
    {
        Number = number;
        Cells = cells ?? throw new ArgumentNullException(nameof(cells));
    }

    public int Number { get; }

    /// <summary>The cells in column order (the grid binds them by index; masked columns show the mask instead).</summary>
    public IReadOnlyList<string> Cells { get; private set; }

    /// <summary>The row number and the cell count – never a cell.</summary>
    public override string ToString() => $"Source row {Number}: {Cells.Count} cells";
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

    public bool IsMasked { get; }

    public bool IsReadOnly => IsMasked;

    /// <summary>Position, letter and the mask flag – never the header.</summary>
    public override string ToString() => $"Source column {Column.Index} ({Column.Letter}, masked={IsMasked})";
}
