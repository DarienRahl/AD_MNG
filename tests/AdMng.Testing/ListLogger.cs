using Microsoft.Extensions.Logging;

namespace AdMng.Testing;

/// <summary>An <see cref="ILogger{TCategoryName}"/> that keeps formatted messages in memory for assertions.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>All messages (including exception text) as one string - handy for "never logs X" assertions.</summary>
    public string AllText => string.Join(Environment.NewLine, Entries.Select(e => e.Message));

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var message = formatter(state, exception);
        if (exception is not null)
        {
            message += Environment.NewLine + exception;
        }

        lock (_entries)
        {
            _entries.Add((logLevel, message));
        }
    }
}
