using System.Globalization;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Wraps the Application-log provider (spec 19.8): a Warning or Error whose category, event id and text equal one written
/// within the last <see cref="Window"/> is suppressed, and the next one written carries the number of suppressed
/// repeats. The Application log is shared with every application on the server – a DC outage would otherwise write the
/// same per-cycle warnings thousands of times a day and push other entries (1001, 1010, other products) out of it.
/// Distinct entries, Critical entries, Information (the access log) and the answers to an administrator's action
/// (<see cref="NeverThrottledEventIds"/>) always pass.
/// </summary>
public sealed class ThrottlingLoggerProvider : ILoggerProvider
{
    /// <summary>Minimum distance between two identical entries.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Warnings that answer an administrator's action and are never suppressed: 2002 – an edit of the bootstrap file
    /// could not be loaded. A second malformed edit within the window (say a corrected emergency <c>DeniedSids</c> entry
    /// with another syntax error) yields the same text; suppressing it would leave only the entry of the first attempt,
    /// older than the edit, and the administrator would take the silence for success.
    /// </summary>
    public static IReadOnlySet<int> NeverThrottledEventIds { get; } = new HashSet<int> { ConfigurationSetup.ReloadFailedEventId };

    private const int MaxTrackedEntries = 512;

    private readonly ILoggerProvider _inner;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _written = new(StringComparer.Ordinal);

    public ThrottlingLoggerProvider(ILoggerProvider inner, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _inner = inner;
        _timeProvider = timeProvider;
    }

    public ILogger CreateLogger(string categoryName) => new ThrottlingLogger(this, categoryName, _inner.CreateLogger(categoryName));

    public void Dispose() => _inner.Dispose();

    /// <summary>Null when the entry is suppressed, else the number of repeats suppressed since it was last written.</summary>
    private int? Admit(string key)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_written.TryGetValue(key, out var entry) && now - entry.WrittenAt < Window)
            {
                entry.Suppressed++;
                return null;
            }

            if (_written.Count >= MaxTrackedEntries)
            {
                foreach (var stale in _written.Where(e => now - e.Value.WrittenAt >= Window).Select(e => e.Key).ToList())
                {
                    _written.Remove(stale);
                }

                if (_written.Count >= MaxTrackedEntries)
                {
                    _written.Clear();
                }
            }

            var suppressed = entry?.Suppressed ?? 0;
            _written[key] = new Entry { WrittenAt = now };
            return suppressed;
        }
    }

    private sealed class Entry
    {
        public DateTimeOffset WrittenAt { get; init; }

        public int Suppressed { get; set; }
    }

    private sealed class ThrottlingLogger(ThrottlingLoggerProvider owner, string category, ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel is not (LogLevel.Warning or LogLevel.Error) || NeverThrottledEventIds.Contains(eventId.Id) || !inner.IsEnabled(logLevel))
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
                return;
            }

            var text = formatter(state, exception);
            if (owner.Admit(string.Concat(category, "|", eventId.Id.ToString(CultureInfo.InvariantCulture), "|", text)) is not { } suppressed)
            {
                return;
            }

            if (suppressed == 0)
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
                return;
            }

            var note = string.Create(CultureInfo.InvariantCulture, $" (repeated {suppressed} times since the previous entry)");
            inner.Log(logLevel, eventId, state, exception, (s, e) => formatter(s, e) + note);
        }
    }
}
