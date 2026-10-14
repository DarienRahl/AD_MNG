namespace AdMng.Watchdog.Core.Attribution;

/// <summary>Result of one bounded on-demand query (<see cref="SourceQueryGate"/>).</summary>
/// <typeparam name="T">The query's result type.</typeparam>
/// <param name="Completed">The query finished in time.</param>
/// <param name="Value">Its result (default when not completed).</param>
/// <param name="TimedOut">The per-query timeout elapsed (waiting for the DC slot included).</param>
/// <param name="ErrorCode">Win32 code of a log access failure; null otherwise.</param>
public readonly record struct SourceQueryResult<T>(bool Completed, T? Value, bool TimedOut, string? ErrorCode);
