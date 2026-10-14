namespace AdMng.Watchdog.Core.Formatting;

/// <summary>One labelled row of an alert ("Kto", "Skąd", …).</summary>
public sealed record AlertFact(string Label, string Value);
