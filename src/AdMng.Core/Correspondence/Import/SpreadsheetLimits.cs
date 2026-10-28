namespace AdMng.Core.Correspondence.Import;

/// <summary>Bounds of the tables read from files (a contact list is far below them; beyond them the file is refused or
/// the excess ignored, so a crafted file cannot exhaust memory).</summary>
internal static class SpreadsheetLimits
{
    /// <summary>Columns of a table: cells further right are ignored (also the cap of ODF repeated columns).</summary>
    public const int MaxColumns = 256;

    /// <summary>Non-empty cells of one file; more → <see cref="InvalidDataException"/>.</summary>
    public const int MaxCells = 4_000_000;

    /// <summary>Shared strings of a workbook; more → <see cref="InvalidDataException"/>.</summary>
    public const int MaxSharedStrings = 2_000_000;

    /// <summary>The 0-based column of a cell reference ("A1" → 0, "AB12" → 27), or -1 when it has no column letters.</summary>
    public static int ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return -1;
        }

        var column = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                column = (column * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
                letters++;
                if (letters > 3)
                {
                    return int.MaxValue;
                }
            }
            else
            {
                break;
            }
        }

        return letters == 0 ? -1 : column - 1;
    }

    /// <summary>Counts the non-empty cells of one file and refuses it beyond <see cref="MaxCells"/>.</summary>
    internal sealed class Budget
    {
        private int _cells;

        public void Cell()
        {
            if (++_cells > MaxCells)
            {
                throw new InvalidDataException("The file has too many cells.");
            }
        }
    }
}
