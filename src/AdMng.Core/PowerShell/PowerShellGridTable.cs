namespace AdMng.Core.PowerShell;

/// <summary>
/// The objects of a pipeline as plain data: what <c>Out-AdmGrid</c> and <c>Export-AdmList</c> hand to the window (docs/dev/r1-powershell-window.md 4.10).
/// The command turns the <c>PSObject</c>s into this on the pipeline thread - columns from their properties, every value as the
/// text the window shows - so no engine object ever reaches the UI thread. Rows are in memory only, for as long as the grid
/// shows them; nothing here is written anywhere.
/// </summary>
public sealed class PowerShellGridTable
{
    private readonly string[] _columns;
    private readonly IReadOnlyList<string?>[] _rows;

    /// <param name="columns">The column names (property names), unique ignoring case, none empty.</param>
    /// <param name="rows">The rows: one text (or null: no value) per column.</param>
    /// <param name="title">The title of the grid; null for the default.</param>
    /// <param name="distinguishedNameColumn">The column that holds the distinguished names, so that a row can be opened in the program; null when there is none.</param>
    /// <exception cref="ArgumentException">A column name is empty or repeated, a row is not as long as the columns, or <paramref name="distinguishedNameColumn"/> is not a column.</exception>
    public PowerShellGridTable(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows, string? title = null, string? distinguishedNameColumn = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        _columns = [.. columns];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in _columns)
        {
            if (string.IsNullOrWhiteSpace(column) || !seen.Add(column))
            {
                throw new ArgumentException("A column needs a name that no other column has.", nameof(columns));
            }
        }

        _rows = [.. rows];
        for (var index = 0; index < _rows.Length; index++)
        {
            if (_rows[index] is not { } row || row.Count != _columns.Length)
            {
                throw new ArgumentException($"Row {index} does not have one value per column.", nameof(rows));
            }
        }

        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (distinguishedNameColumn is not null)
        {
            DistinguishedNameColumn = Array.FindIndex(_columns, column => string.Equals(column, distinguishedNameColumn, StringComparison.OrdinalIgnoreCase));
            if (DistinguishedNameColumn < 0)
            {
                throw new ArgumentException("The distinguished name column is not one of the columns.", nameof(distinguishedNameColumn));
            }
        }
        else
        {
            DistinguishedNameColumn = -1;
        }
    }

    public IReadOnlyList<string> Columns => _columns;

    public IReadOnlyList<IReadOnlyList<string?>> Rows => _rows;

    public string? Title { get; }

    /// <summary>The index of the column of distinguished names, or -1.</summary>
    public int DistinguishedNameColumn { get; }

    /// <summary>The numbers only: the rows hold directory values.</summary>
    public override string ToString() => $"PowerShellGridTable({_columns.Length} columns, {_rows.Length} rows)";
}
