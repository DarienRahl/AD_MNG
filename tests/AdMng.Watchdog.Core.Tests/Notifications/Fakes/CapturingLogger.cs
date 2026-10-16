using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Tests.Notifications.Fakes;

/// <summary>An <see cref="ILogger{T}"/> that keeps every formatted entry (and whether an exception object was passed).</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message, bool HadException)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, bool HadException)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception), exception is not null));
        }
    }
}
