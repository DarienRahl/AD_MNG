namespace AdMng.Presentation.Infrastructure;

/// <summary>A list column for <see cref="ColumnFit.Fit"/>.</summary>
/// <param name="Width">
/// Preferred width of a column that is not a star column (shrunk towards <paramref name="MinWidth"/> while the star columns
/// would otherwise get less than their minimum); for a fixed column the width it keeps.
/// </param>
/// <param name="MinWidth">The narrowest width at which the column is still useful.</param>
/// <param name="Star">Above zero: the column shares the width the other columns leave, by this weight (at least its minimum).</param>
/// <param name="Priority">0: always shown. 1, 2, …: optional, shown in this order while their minimum width still fits.</param>
/// <param name="IsFixed">The operator's width (header gripper): kept as it is, never shrunk, stretched or hidden.</param>
public readonly record struct FitColumn(double Width, double MinWidth, double Star = 0, int Priority = 0, bool IsFixed = false);

/// <summary>
/// Fits the columns of a list into its visible width, so that the list never scrolls sideways while its columns fit at their
/// minimum widths: required columns are always shown; optional ones appear in their priority order while the list has room
/// for their minimum width – the first that does not fit hides the rest, so columns only appear as the list widens and
/// disappear in reverse order as it narrows; the star columns share what the others leave (by weight, each at least its
/// minimum), and the other columns give up width – proportionally, down to their minimum – before a star column gets less
/// than its minimum. Widths are whole pixels; together they fill the width exactly when there is a star column. Pure and
/// deterministic: the view applies it whenever the list's width changes.
/// </summary>
public static class ColumnFit
{
    private const double Epsilon = 0.01;

    /// <summary>The width of every column in <paramref name="columns"/>, null for a hidden one.</summary>
    /// <param name="available">The width the columns may take (the list's visible width without row chrome and scroll bar).</param>
    /// <param name="columns">The columns in display order.</param>
    public static IReadOnlyList<double?> Fit(double available, IReadOnlyList<FitColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var count = columns.Count;
        var result = new double?[count];
        if (count == 0)
        {
            return result;
        }

        available = double.IsFinite(available) && available > 0 ? available : 0;
        var visible = ChooseVisible(available, columns);

        // Fixed columns keep their width, the other columns that are not star columns start at their preferred width.
        var widths = new double[count];
        double used = 0, starMinimum = 0, slack = 0;
        for (var i = 0; i < count; i++)
        {
            if (!visible[i])
            {
                continue;
            }

            var column = columns[i];
            if (column.IsFixed)
            {
                widths[i] = Least(column);
                used += widths[i];
            }
            else if (column.Star > 0)
            {
                starMinimum += Minimum(column);
            }
            else
            {
                widths[i] = Math.Max(column.Width, Minimum(column));
                used += widths[i];
                slack += widths[i] - Minimum(column);
            }
        }

        // Too little room: the flexible columns give up width (proportionally, down to their minimum) first.
        var deficit = used + starMinimum - available;
        if (deficit > 0 && slack > 0)
        {
            var ratio = Math.Min(1, deficit / slack);
            for (var i = 0; i < count; i++)
            {
                if (visible[i] && IsFlexible(columns[i]))
                {
                    var shrink = (widths[i] - Minimum(columns[i])) * ratio;
                    widths[i] -= shrink;
                    used -= shrink;
                }
            }
        }

        var firstStar = ShareStars(available - used, columns, visible, widths);
        return Round(available, columns, visible, widths, firstStar);
    }

    /// <summary>Required (and fixed) columns, then the optional ones in priority order while their minimum fits.</summary>
    private static bool[] ChooseVisible(double available, IReadOnlyList<FitColumn> columns)
    {
        var visible = new bool[columns.Count];
        var budget = available;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].IsFixed || columns[i].Priority <= 0)
            {
                visible[i] = true;
                budget -= Least(columns[i]);
            }
        }

        var optional = Enumerable.Range(0, columns.Count)
            .Where(i => !visible[i])
            .OrderBy(i => columns[i].Priority)
            .ThenBy(i => i);
        foreach (var i in optional)
        {
            var least = Least(columns[i]);
            if (least > budget + Epsilon)
            {
                break;
            }

            visible[i] = true;
            budget -= least;
        }

        return visible;
    }

    /// <summary>
    /// The star columns share <paramref name="rest"/> by weight; one whose share is below its minimum gets the minimum and the
    /// others share what is left. Returns the index of the star column with the largest weight (-1: none).
    /// </summary>
    private static int ShareStars(double rest, IReadOnlyList<FitColumn> columns, bool[] visible, double[] widths)
    {
        var open = Enumerable.Range(0, columns.Count).Where(i => visible[i] && IsStar(columns[i])).ToList();
        var first = open.Count == 0 ? -1 : open.OrderByDescending(i => columns[i].Star).ThenBy(i => i).First();
        while (open.Count > 0)
        {
            var share = rest / open.Sum(i => columns[i].Star);
            var below = open.Where(i => columns[i].Star * share < Minimum(columns[i])).ToList();
            if (below.Count == 0)
            {
                foreach (var i in open)
                {
                    widths[i] = columns[i].Star * share;
                }

                break;
            }

            foreach (var i in below)
            {
                widths[i] = Minimum(columns[i]);
                rest -= widths[i];
                open.Remove(i);
            }
        }

        return first;
    }

    /// <summary>Whole pixels, never below a minimum; the pixels lost to rounding go to the widest-weighted star column.</summary>
    private static double?[] Round(double available, IReadOnlyList<FitColumn> columns, bool[] visible, double[] widths, int firstStar)
    {
        var result = new double?[columns.Count];
        double exact = 0, rounded = 0;
        for (var i = 0; i < columns.Count; i++)
        {
            if (!visible[i])
            {
                continue;
            }

            var column = columns[i];
            var width = column.IsFixed ? widths[i] : Math.Max(Math.Floor(widths[i] + Epsilon), Minimum(column));
            exact += widths[i];
            rounded += width;
            result[i] = width;
        }

        if (firstStar >= 0 && exact <= available + Epsilon)
        {
            var spare = Math.Floor(available + Epsilon) - rounded;
            if (spare > 0)
            {
                result[firstStar] += spare;
            }
        }

        return result;
    }

    private static bool IsStar(FitColumn column) => !column.IsFixed && column.Star > 0;

    private static bool IsFlexible(FitColumn column) => !column.IsFixed && column.Star <= 0;

    private static double Minimum(FitColumn column) => double.IsFinite(column.MinWidth) && column.MinWidth > 0 ? column.MinWidth : 0;

    /// <summary>The least width a shown column takes: a fixed column's own width, else its minimum.</summary>
    private static double Least(FitColumn column) =>
        column.IsFixed ? (double.IsFinite(column.Width) && column.Width > 0 ? column.Width : 0) : Minimum(column);
}
