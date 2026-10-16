namespace AdMng.Presentation.Watchdog;

/// <summary>A titled group of check options (event kinds grouped by category).</summary>
/// <param name="Title">Localized group title.</param>
/// <param name="Options">The options of the group.</param>
public sealed record WatchdogOptionGroup<T>(string Title, IReadOnlyList<WatchdogCheckOption<T>> Options);
