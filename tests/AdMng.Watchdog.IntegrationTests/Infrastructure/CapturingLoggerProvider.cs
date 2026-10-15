using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>Keeps every formatted log line (and the type of a logged exception) in memory, so a test can check that no
/// directory value reached the diagnostic log (CLAUDE.md rule 1).</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    /// <summary>All lines, one per row.</summary>
    public string Text => string.Join('\n', _lines);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var line = $"{logLevel} {category}[{eventId.Id}] {formatter(state, exception)}";
            lines.Enqueue(exception is null ? line : $"{line} ({exception.GetType().FullName})");
        }
    }
}
