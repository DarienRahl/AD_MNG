namespace AdMng.Presentation.Settings;

/// <summary>A named event filter of the Watchdog tab.</summary>
/// <param name="Name">Name shown in the presets list.</param>
/// <param name="Query">The filter as an <c>EventQuery</c> query string, without <c>after</c>, <c>before</c> and <c>wait</c>.</param>
public sealed record WatchdogFilterPreset(string Name, string Query);
