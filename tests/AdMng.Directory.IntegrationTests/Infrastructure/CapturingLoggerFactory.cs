using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>One captured log message.</summary>
internal sealed record LogRecord(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception, DateTimeOffset Timestamp);

/// <summary>In-memory logger factory for asserting on log output.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<LogRecord> _records = new();

    public IReadOnlyList<LogRecord> Records => [.. _records];

    public IReadOnlyList<LogRecord> WithEventId(int id) => [.. _records.Where(r => r.EventId.Id == id)];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _records);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogRecord> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            records.Enqueue(new LogRecord(category, logLevel, eventId, formatter(state, exception), exception, DateTimeOffset.UtcNow));
    }
}
