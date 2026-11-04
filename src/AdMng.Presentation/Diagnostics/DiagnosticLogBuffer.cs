using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Diagnostics;

/// <summary>An exception of a <see cref="DiagnosticLogEntry"/>: its type and HResult only (never its message or stack trace).</summary>
public readonly record struct DiagnosticException(string Type, int HResult);

/// <summary>
/// One log entry as the problem report keeps it (design note s-pilot-readiness.md 3): level, time, category, event id and
/// the message <b>template</b> – the <c>{OriginalFormat}</c> the code wrote, with its placeholders unfilled – and of an
/// exception (and its inner exceptions) only the type and HResult. The formatted message, the argument values, the scopes
/// and the exception messages are never read, so no directory value or secret can enter (CLAUDE.md rules 1 and 2).
/// </summary>
/// <param name="Time">When the entry was written (UTC).</param>
/// <param name="Level">The log level.</param>
/// <param name="Category">The logger category (a type name).</param>
/// <param name="EventId">The numeric event id (0 when none).</param>
/// <param name="EventName">The event name the code declared, or null.</param>
/// <param name="Template">The message template, or null when the entry carried none (an unstructured state).</param>
/// <param name="Exceptions">The exception and its inner exceptions, outermost first (at most <see cref="DiagnosticLogBuffer.MaxExceptionDepth"/>).</param>
public sealed record DiagnosticLogEntry(
    DateTimeOffset Time,
    LogLevel Level,
    string Category,
    int EventId,
    string? EventName,
    string? Template,
    IReadOnlyList<DiagnosticException> Exceptions);

/// <summary>
/// The in-memory diagnostic buffer of the problem report: the last <see cref="Capacity"/> entries of every logger of the
/// application (fed by <see cref="DiagnosticLoggerProvider"/>), oldest dropped first. Application-wide singleton – it holds
/// no directory values (see <see cref="DiagnosticLogEntry"/>) – and never written anywhere; the operator decides whether
/// the report that shows it is copied or saved. Thread-safe.
/// </summary>
public sealed class DiagnosticLogBuffer
{
    /// <summary>The number of entries kept by default (the problem report shows all of them).</summary>
    public const int DefaultCapacity = 200;

    /// <summary>The exception chain kept per entry: the exception and up to three inner exceptions.</summary>
    public const int MaxExceptionDepth = 4;

    /// <summary>The state key of the message template (Microsoft.Extensions.Logging, also source-generated loggers).</summary>
    private const string OriginalFormatKey = "{OriginalFormat}";

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly DiagnosticLogEntry?[] _entries;
    private int _next;
    private int _count;
    private long _total;

    public DiagnosticLogBuffer(TimeProvider time, int capacity = DefaultCapacity)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _entries = new DiagnosticLogEntry?[capacity];
    }

    /// <summary>The number of entries kept.</summary>
    public int Capacity => _entries.Length;

    /// <summary>The number of entries recorded since the start (also those already dropped).</summary>
    public long Total
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    /// <summary>The entries kept, oldest first.</summary>
    public IReadOnlyList<DiagnosticLogEntry> Snapshot()
    {
        lock (_gate)
        {
            var result = new DiagnosticLogEntry[_count];
            var first = (_next - _count + _entries.Length) % _entries.Length;
            for (var i = 0; i < _count; i++)
            {
                result[i] = _entries[(first + i) % _entries.Length]!;
            }

            return result;
        }
    }

    /// <summary>
    /// Keeps one log call: the template of <paramref name="state"/> (never formatted, never its values) and the type and
    /// HResult of <paramref name="exception"/> (never its message).
    /// </summary>
    internal void Record<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception)
    {
        var entry = new DiagnosticLogEntry(
            _time.GetUtcNow(),
            level,
            category,
            eventId.Id,
            string.IsNullOrEmpty(eventId.Name) ? null : eventId.Name,
            TemplateOf(state),
            ExceptionsOf(exception));
        lock (_gate)
        {
            _entries[_next] = entry;
            _next = (_next + 1) % _entries.Length;
            _count = Math.Min(_count + 1, _entries.Length);
            _total++;
        }
    }

    /// <summary>The <c>{OriginalFormat}</c> of a structured state (the last pair by convention); only that pair is read.</summary>
    private static string? TemplateOf<TState>(TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> { Count: > 0 } pairs)
        {
            return null;
        }

        var last = pairs[pairs.Count - 1];
        return last.Key == OriginalFormatKey ? last.Value as string : null;
    }

    private static DiagnosticException[] ExceptionsOf(Exception? exception)
    {
        if (exception is null)
        {
            return [];
        }

        var chain = new List<DiagnosticException>(MaxExceptionDepth);
        for (var current = exception; current is not null && chain.Count < MaxExceptionDepth; current = current.InnerException)
        {
            var type = current.GetType();
            chain.Add(new DiagnosticException(type.FullName ?? type.Name, current.HResult));
        }

        return [.. chain];
    }
}

/// <summary>
/// Feeds <see cref="DiagnosticLogBuffer"/> from Microsoft.Extensions.Logging (registered by the App next to the debugger
/// output). Never calls the formatter and ignores scopes; the minimum level is the logging configuration's.
/// </summary>
public sealed class DiagnosticLoggerProvider(DiagnosticLogBuffer buffer) : ILoggerProvider
{
    private readonly DiagnosticLogBuffer _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

    public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(_buffer, categoryName ?? string.Empty);

    public void Dispose()
    {
        // The buffer is a singleton of its own.
    }

    private sealed class DiagnosticLogger(DiagnosticLogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                buffer.Record(category, logLevel, eventId, state, exception);
            }
        }
    }
}
