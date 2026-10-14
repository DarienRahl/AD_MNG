using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>One collected log entry.</summary>
public sealed record TestLogEntry(string Category, LogLevel Level, EventId EventId, string Message, string Values, string? Exception)
{
    public override string ToString() => $"{Level} {Category} [{EventId.Id}] {Message} | {Values} | {Exception}";
}
