using System.Globalization;
using System.Net;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Formatting;
using AdMng.Core.Model;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Tests.TestDoubles;

/// <summary>Runs dispatched actions inline, serialized by a lock (tests have no UI thread).</summary>
/// <remarks>
/// "On the UI thread" means inside a dispatched action, as in the app: a caller outside one goes through the lock, so
/// <c>RunAsync</c> from two pool threads (e.g. the tree merging children while the well-known containers are applied)
/// never runs two actions at once.
/// </remarks>
internal sealed class SynchronousUiDispatcher : IUiDispatcher
{
    private readonly Lock _gate = new();

    public int InvokeCount { get; private set; }

    public bool CheckAccess() => _gate.IsHeldByCurrentThread;

    public Task InvokeAsync(Action action)
    {
        lock (_gate)
        {
            InvokeCount++;
            action();
        }

        return Task.CompletedTask;
    }
}

/// <summary>Deterministic clock: timers fire only when the test calls <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;
    private long _timestamp;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public int ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            _timestamp += by.Ticks;
            due = [.. _timers.Where(t => t.Due is { } d && d <= _now)];
            foreach (var t in due)
            {
                t.Due = t.Period > TimeSpan.Zero ? _now + t.Period : null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Formatter double: values joined with "; " (the real AttributeFormatter lives in AdMng.Core).</summary>
internal sealed class PassThroughAttributeFormatter : IAttributeFormatter
{
    public string Format(string attribute, AttributeValues? values, CultureInfo culture) =>
        values is null ? string.Empty : string.Join("; ", values.Strings);

    public string FormatValue(string attribute, object value, CultureInfo culture) => value as string ?? Convert.ToHexString((byte[])value);

    public bool TryParse(string attribute, string input, CultureInfo culture, out object? value, out string? error)
    {
        value = input;
        error = null;
        return true;
    }
}

/// <summary>Connector double returning a prepared session (or throwing a prepared error).</summary>
internal sealed class FakeDirectoryConnector(InMemoryDirectorySession session) : IDirectoryConnector
{
    public string? ComputerDomain { get; set; } = "contoso.test";

    public Exception? ConnectError { get; set; }

    public ConnectionOptions? LastOptions { get; private set; }

    public NetworkCredential? LastCredential { get; private set; }

    public int ConnectCount { get; private set; }

    public IReadOnlyList<DomainControllerInfo> DomainControllers { get; set; } =
    [
        new("dc02.contoso.test", "Kraków", true, false, false),
        new("dc01.contoso.test", "Warszawa", true, false, true),
    ];

    public string? GetComputerDomain() => ComputerDomain;

    public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
        Task.FromResult(DomainControllers);

    public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken)
    {
        ConnectCount++;
        LastOptions = options;
        LastCredential = credential;
        if (ConnectError is { } error)
        {
            throw error;
        }

        return Task.FromResult<IDirectorySession>(session);
    }
}

internal sealed class FakePasswordInput(string password) : IPasswordInput
{
    public string Password { get; set; } = password;

    public int Reads { get; private set; }

    public bool Cleared { get; private set; }

    public string GetPassword()
    {
        Reads++;
        return Password;
    }

    public void Clear()
    {
        Cleared = true;
        Password = string.Empty;
    }
}

internal sealed class TempAppPaths : IAppPaths, IDisposable
{
    public string SettingsDirectory { get; } = Path.Combine(Path.GetTempPath(), "admng-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(SettingsDirectory))
        {
            Directory.Delete(SettingsDirectory, recursive: true);
        }
    }
}

internal sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettings Current { get; private set; } = new();

    public int SaveCount { get; private set; }

    public event EventHandler? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync(Action<AppSettings> change, CancellationToken cancellationToken = default)
    {
        change(Current);
        Current.Normalize();
        SaveCount++;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingNotificationService : INotificationService
{
    private readonly List<(NotificationSeverity Severity, string Message)> _items = [];

    public IReadOnlyList<(NotificationSeverity Severity, string Message)> Items
    {
        get
        {
            lock (_items)
            {
                return [.. _items];
            }
        }
    }

    public void Show(NotificationSeverity severity, string message)
    {
        lock (_items)
        {
            _items.Add((severity, message));
        }
    }

    /// <summary>Forgets the toasts shown so far (e.g. those of the setup).</summary>
    public void Clear()
    {
        lock (_items)
        {
            _items.Clear();
        }
    }
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    /// <summary>The last text was put there with <see cref="SetSecret"/>.</summary>
    public bool IsSecret { get; private set; }

    /// <summary>Simulates a clipboard held by another process: writes fail.</summary>
    public bool IsBusy { get; set; }

    public bool SetText(string text)
    {
        if (IsBusy)
        {
            return false;
        }

        Text = text;
        IsSecret = false;
        return true;
    }

    public bool SetSecret(string secret)
    {
        if (IsBusy)
        {
            return false;
        }

        Text = secret;
        IsSecret = true;
        return true;
    }
}

internal sealed class FakeFileDialogService : IFileDialogService
{
    public string? NextOpenPath { get; set; }

    public string? NextSavePath { get; set; }

    public string? LastDefaultFileName { get; private set; }

    public Task<string?> OpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult(NextOpenPath);

    public Task<string?> SaveFileAsync(string title, string defaultFileName, IReadOnlyList<FileFilter> filters)
    {
        LastDefaultFileName = defaultFileName;
        return Task.FromResult(NextSavePath);
    }
}

/// <summary>Minimal RFC 4180 writer with formula protection (the real codec lives in AdMng.Core).</summary>
internal sealed class SimpleCsvCodec : ICsvCodec
{
    public CsvTable Read(TextReader reader, char? delimiter = null) => throw new NotSupportedException();

    public void Write(TextWriter writer, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, char delimiter = ';', bool protectFormulas = true)
    {
        writer.WriteLine(string.Join(delimiter, headers.Select(h => Quote(h, delimiter, protectFormulas))));
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(delimiter, row.Select(c => Quote(c, delimiter, protectFormulas))));
        }
    }

    private static string Quote(string value, char delimiter, bool protect)
    {
        if (protect && value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.Contains(delimiter) || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
