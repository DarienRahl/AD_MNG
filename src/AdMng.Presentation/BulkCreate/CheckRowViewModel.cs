using AdMng.Core.Batch.Creation;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// One row of the check grid (docs/dev/phase3-bulk-create.md 2.7, owner B4): the include check box, the status, the Excel
/// row, <em>Uwagi</em> and the cells in the order of <see cref="BulkCheckViewModel.Columns"/>. A recompute updates the row in
/// place, keyed by <see cref="Number"/>.
/// </summary>
public sealed partial class CheckRowViewModel : ObservableObject
{
    private bool _updating;

    /// <param name="number">The wizard's row number.</param>
    internal CheckRowViewModel(int number)
    {
        Number = number;
    }

    /// <summary>The wizard's row number (<see cref="TableRow.Number"/>).</summary>
    public int Number { get; }

    /// <summary>The row of the Excel sheet (<em>Nr</em>: number + offset − 1).</summary>
    [ObservableProperty]
    public partial int ExcelRow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Severity), nameof(IsCreated), nameof(CanToggle))]
    public partial BulkRowStatus Status { get; set; }

    /// <summary>The status as the preview's severity (the glyph of <c>RowSeverityToGlyphConverter</c>; created rows show the info glyph).</summary>
    public RowSeverity Severity => Status switch
    {
        BulkRowStatus.Error => RowSeverity.Error,
        BulkRowStatus.Warning => RowSeverity.Warning,
        BulkRowStatus.Created => RowSeverity.Info,
        _ => RowSeverity.None,
    };

    /// <summary>The localized status (<c>BulkCheck.Status.&lt;status&gt;</c>).</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>The row was created by an execution of this window (its values can no longer be edited).</summary>
    public bool IsCreated => Status == BulkRowStatus.Created;

    /// <summary>The include check box can be changed (not for created rows).</summary>
    public bool CanToggle => Status != BulkRowStatus.Created;

    /// <summary>Unchecking adds the row to <see cref="BulkCheckViewModel.ExcludedRows"/>.</summary>
    [ObservableProperty]
    public partial bool IsIncluded { get; set; } = true;

    /// <summary><em>Uwagi</em>: the localized issues joined.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusToolTip))]
    public partial string Notes { get; set; } = string.Empty;

    /// <summary>The status glyph's tool tip: the status and the notes.</summary>
    public string StatusToolTip => Notes.Length == 0 ? StatusText : StatusText + " – " + Notes;

    /// <summary>The row may describe an existing account or another row of the same person (<em>Możliwe duplikaty</em>).</summary>
    [ObservableProperty]
    public partial bool IsPossibleDuplicate { get; set; }

    /// <summary>The cells in column order (bound by index).</summary>
    public IReadOnlyList<CheckCellViewModel> Cells { get; internal set; } = [];

    /// <summary>The cell of <paramref name="key"/>, or null.</summary>
    public CheckCellViewModel? Cell(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var cell in Cells)
        {
            if (string.Equals(cell.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return cell;
            }
        }

        return null;
    }

    /// <summary>Called when the operator checks or unchecks the row (not for updates made by the check step itself).</summary>
    internal Action<CheckRowViewModel, bool>? IncludedChanged { get; set; }

    /// <summary>Sets <see cref="IsIncluded"/> without telling <see cref="IncludedChanged"/> (a computation's own state).</summary>
    internal void SetIncluded(bool included)
    {
        _updating = true;
        try
        {
            IsIncluded = included;
        }
        finally
        {
            _updating = false;
        }
    }

    partial void OnIsIncludedChanged(bool value)
    {
        if (!_updating)
        {
            IncludedChanged?.Invoke(this, value);
        }
    }

    partial void OnStatusChanged(BulkRowStatus value) => OnPropertyChanged(nameof(StatusToolTip));

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(StatusToolTip));

    /// <summary>The row number and the status – never a value.</summary>
    public override string ToString() => $"Check row {Number}: {Status}";
}

/// <summary>One cell of the check grid (2.7): the computed or edited value, its source and marks.</summary>
public sealed partial class CheckCellViewModel : ObservableObject
{
    private bool _updating;

    /// <param name="key">The column key (<see cref="CheckColumnDescriptor.Key"/>).</param>
    internal CheckCellViewModel(string key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public string Key { get; }

    /// <summary>The shown value (the mask for a masked column).</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>
    /// The text the cell's editor shows and commits (the value of an attribute cell; the source text of the manager and the
    /// groups cell – what the sheet or an earlier edit said). A commit by the operator stores an edit and recomputes.
    /// </summary>
    [ObservableProperty]
    public partial string EditText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial BulkValueSource? Source { get; set; }

    /// <summary>The cell can be edited (its column is editable and its row was not created).</summary>
    [ObservableProperty]
    public partial bool IsEditable { get; set; }

    /// <summary>The operator edited the cell (marked; <em>Przywróć wartość wyliczoną</em>).</summary>
    [ObservableProperty]
    public partial bool IsEdited { get; set; }

    /// <summary>The value was disambiguated, sanitized or cut (the reason in <see cref="ToolTip"/>).</summary>
    [ObservableProperty]
    public partial bool IsAdjusted { get; set; }

    [ObservableProperty]
    public partial string? ToolTip { get; set; }

    /// <summary>The cell's editor is open (set by the view): a recompute leaves <see cref="EditText"/> alone meanwhile.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    /// <summary>The candidates of an ambiguous manager (the cell's drop-down; <see cref="Option{T}.Value"/> is the DN).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCandidates))]
    public partial IReadOnlyList<Option<string>> Candidates { get; set; } = [];

    /// <summary>The drop-down of an ambiguous manager is shown.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>The candidate the operator picked in the drop-down (its DN becomes the cell's edit).</summary>
    [ObservableProperty]
    public partial string? ChosenCandidate { get; set; }

    /// <summary>The key under which an edit of this cell is stored (<see cref="BulkCreateInput.Edits"/>); null = read-only.</summary>
    internal string? EditKey { get; init; }

    /// <summary>The wizard's row number of the cell's row (the key of its edits).</summary>
    internal int RowNumber { get; init; }

    /// <summary>What <see cref="EditText"/> showed before the operator typed (an unchanged commit stores nothing).</summary>
    internal string ComputedEditText { get; private set; } = string.Empty;

    /// <summary>Called when the operator commits a text (<see cref="EditText"/>) or picks a candidate.</summary>
    internal Action<CheckCellViewModel, string>? Edited { get; set; }

    /// <summary>Updates the cell from a computation without raising <see cref="Edited"/>; the open editor keeps its text.</summary>
    internal void Update(string value, string editText, BulkValueSource? source, bool edited, bool adjusted, string? toolTip, bool editable)
    {
        _updating = true;
        try
        {
            Value = value;
            ComputedEditText = editText;
            if (!IsEditing)
            {
                EditText = editText;
            }

            Source = source;
            IsEdited = edited;
            IsAdjusted = adjusted;
            ToolTip = toolTip;
            IsEditable = editable;
            ChosenCandidate = null;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Sets the candidates without raising <see cref="Edited"/>.</summary>
    internal void SetCandidates(IReadOnlyList<Option<string>> candidates)
    {
        _updating = true;
        try
        {
            Candidates = candidates;
            ChosenCandidate = null;
        }
        finally
        {
            _updating = false;
        }
    }

    partial void OnEditTextChanged(string value)
    {
        if (!_updating && EditKey is not null)
        {
            Edited?.Invoke(this, value ?? string.Empty);
        }
    }

    partial void OnChosenCandidateChanged(string? value)
    {
        if (!_updating && EditKey is not null && value is { Length: > 0 } dn)
        {
            Edited?.Invoke(this, dn);
        }
    }

    /// <summary>The flags only – never the value.</summary>
    public override string ToString() => $"Check cell ({Source?.ToString() ?? "none"}, edited={IsEdited}, adjusted={IsAdjusted})";
}
