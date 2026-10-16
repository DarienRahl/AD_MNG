using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>One parsed event-log record: the event and the provider that wrote it (kept only for
/// <see cref="EventSourceGuard"/>, never stored).</summary>
/// <param name="Provider"><c>System/Provider/@Name</c>.</param>
/// <param name="Event">The parsed event (all EventData, not yet reduced).</param>
public sealed record ParsedSecurityRecord(string Provider, SecurityEvent Event);
