using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Presentation.Watchdog;

/// <summary>Number of events of one severity in the last 24 hours (status card).</summary>
/// <param name="Severity">The severity (badge colour).</param>
/// <param name="Text">Localized severity name.</param>
/// <param name="Count">Events of that severity.</param>
public sealed record WatchdogSeverityCountViewModel(Severity Severity, string Text, int Count);
