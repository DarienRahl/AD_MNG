using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>Collects every formatted log message and its exception text (log hygiene assertions).</summary>
public sealed class TestLog : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly List<TestLogEntry> _entries = [];

    public IReadOnlyList<TestLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Everything logged, one entry per line (message, structured values and exception).</summary>
    public string Text => string.Join(Environment.NewLine, Entries.Select(e => e.ToString()));

    public ILogger<T> For<T>() => new Logger<T>(new SingleProviderFactory(this));

    public ILogger CreateLogger(string categoryName) => new Collector(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(TestLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    private sealed class Collector(TestLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            owner.Add(new TestLogEntry(category, logLevel, eventId, formatter(state, exception), values, exception?.ToString()));
        }
    }

    private sealed class SingleProviderFactory(TestLog provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }
}
