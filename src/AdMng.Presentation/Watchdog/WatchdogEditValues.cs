namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Turns editor fields back into configuration values without changing what the operator did not touch: lists keep their
/// stored order (check boxes are shown in catalog order), and a value shown in a coarser unit (minutes, a local date)
/// keeps its stored precision while its text is unchanged. Otherwise opening an editor would already make the
/// configuration "dirty" and every save would rewrite values nobody edited.
/// </summary>
public static class WatchdogEditValues
{
    /// <summary>
    /// The <paramref name="selected"/> values in the order of <paramref name="original"/>, followed by the newly selected
    /// ones in the order given.
    /// </summary>
    public static IReadOnlyList<T> KeepOrder<T>(IReadOnlyList<T> original, IEnumerable<T> selected, IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(selected);
        comparer ??= EqualityComparer<T>.Default;
        var chosen = selected.ToList();
        var kept = original.Where(o => chosen.Contains(o, comparer)).Distinct(comparer).ToList();
        kept.AddRange(chosen.Where(s => !kept.Contains(s, comparer)).Distinct(comparer));
        return kept;
    }

    /// <summary>
    /// <paramref name="original"/> while <paramref name="text"/> still shows it (<paramref name="format"/>), otherwise the
    /// parsed text.
    /// </summary>
    public static T KeepUnchanged<T>(T original, string? text, Func<T, string> format, Func<string, T> parse)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(parse);
        var typed = (text ?? string.Empty).Trim();
        return string.Equals(format(original), typed, StringComparison.Ordinal) ? original : parse(typed);
    }
}
