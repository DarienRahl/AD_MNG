namespace AdMng.Watchdog.Core.Attribution;

/// <summary>One log the engine ingests: a DC's Security log or a WEF collector's ForwardedEvents log (section 11.1).</summary>
/// <param name="Host">Normalized host name (cursor key).</param>
/// <param name="LogName">"Security" or "ForwardedEvents".</param>
public sealed record IngestionSource(string Host, string LogName);
