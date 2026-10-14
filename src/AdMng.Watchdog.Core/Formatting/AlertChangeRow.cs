namespace AdMng.Watchdog.Core.Formatting;

/// <summary>One row of the changes table. <see cref="Before"/> null = the row has a single cell (<see cref="After"/>
/// spans both columns, e.g. "zmieniono (wartość ukryta)").</summary>
public sealed record AlertChangeRow(string Attribute, string? Before, string After);
