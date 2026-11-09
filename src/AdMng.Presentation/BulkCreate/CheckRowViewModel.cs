using AdMng.Core.Batch.Creation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// One row of the check grid (docs/dev/phase3-bulk-create.md 2.7, owner B4): the include check box, the status, the Excel
/// row, <em>Uwagi</em> and the cells in the order of <see cref="BulkCheckViewModel.Columns"/>. A recompute updates the row in
/// place, keyed by <see cref="Number"/>.
/// </summary>
/// <remarks>Contract stub: B4 fills and updates it.</remarks>
public sealed partial class CheckRowViewModel : ObservableObject
{
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
    public partial BulkRowStatus Status { get; set; }

    /// <summary>Unchecking adds the row to <see cref="BulkCheckViewModel.ExcludedRows"/>.</summary>
    [ObservableProperty]
    public partial bool IsIncluded { get; set; } = true;

    /// <summary><em>Uwagi</em>: the localized issues joined.</summary>
    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    /// <summary>The cells in column order (bound by index).</summary>
    public IReadOnlyList<CheckCellViewModel> Cells { get; internal set; } = [];

    /// <summary>The row number and the status – never a value.</summary>
    public override string ToString() => $"Check row {Number}: {Status}";
}

/// <summary>One cell of the check grid (2.7): the computed or edited value, its source and marks.</summary>
public sealed partial class CheckCellViewModel : ObservableObject
{
    /// <param name="key">The column key (<see cref="CheckColumnDescriptor.Key"/>).</param>
    internal CheckCellViewModel(string key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public string Key { get; }

    /// <summary>The shown value (the mask for a masked column).</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    [ObservableProperty]
    public partial BulkValueSource? Source { get; set; }

    /// <summary>The operator edited the cell (marked; <em>Przywróć wartość wyliczoną</em>).</summary>
    [ObservableProperty]
    public partial bool IsEdited { get; set; }

    /// <summary>The value was disambiguated, sanitized or cut (the reason in <see cref="ToolTip"/>).</summary>
    [ObservableProperty]
    public partial bool IsAdjusted { get; set; }

    [ObservableProperty]
    public partial string? ToolTip { get; set; }

    /// <summary>The flags only – never the value.</summary>
    public override string ToString() => $"Check cell ({Source?.ToString() ?? "none"}, edited={IsEdited}, adjusted={IsAdjusted})";
}
