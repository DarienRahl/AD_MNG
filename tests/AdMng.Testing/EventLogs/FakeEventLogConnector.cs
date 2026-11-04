using AdMng.Core.Directory;
using AdMng.Core.EventLogs;

namespace AdMng.Testing.EventLogs;

/// <summary>One read the fake served: the host (or files' display name) and the query.</summary>
public sealed record FakeEventLogRead(string Host, EventLogQuery Query)
{
    public override string ToString() => $"read: {Query}";
}

/// <summary>
/// In-memory <see cref="IEventLogConnector"/> (design note e-event-logs.md 3, 6): hosts → channels → records, coverage per
/// host and channel (computed from the records unless set), access failures per host (<see cref="FailHost"/>) or channel
/// (<see cref="FailChannel"/>), <see cref="IsSupported"/> settable, operator files (<see cref="AddFile"/>) and a
/// deterministic rendered message. Queries are validated like the real connector (<see cref="EventQueryXml.Build"/>; a
/// rejected query is <see cref="EventLogAccessError.InvalidQuery"/>) and applied by <see cref="EventLogMatcher"/>; the
/// records come back with <see cref="EventLogRecord.Host"/> set to the name the host was opened with. Host names compare
/// case-insensitively (<see cref="EventHostNames"/>), channel names and file paths too. Thread-safe for parallel reads;
/// configure it before use.
/// </summary>
public sealed class FakeEventLogConnector : IEventLogConnector
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FakeHost> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EventLogRecord>> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unreadableFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _opened = [];
    private readonly List<FakeEventLogRead> _reads = [];
    private int _openReaders;

    /// <summary>Remote and file reads are possible (false: every open and read fails with
    /// <see cref="EventLogAccessError.NotSupported"/>).</summary>
    public bool IsSupported { get; set; } = true;

    /// <summary>The message <see cref="IEventLogReader.FormatMessageAsync"/> returns (null: cannot be rendered); default
    /// <see cref="DefaultMessage"/>.</summary>
    public Func<EventLogRecord, string?> MessageFormatter { get; set; } = DefaultMessage;

    /// <summary>Awaited before every read with the host and query (block on a task, delay, throw) – for progress and
    /// cancellation tests.</summary>
    public Func<string, EventLogQuery, CancellationToken, Task>? BeforeRead { get; set; }

    /// <summary>Records yielded between two yields to the scheduler (like the batches of a remote read).</summary>
    public int BatchSize { get; set; } = EventLogLimits.BatchSize;

    /// <summary>Hosts opened (as passed to <see cref="OpenComputerAsync"/>, also failed ones), in order.</summary>
    public IReadOnlyList<string> OpenedHosts
    {
        get
        {
            lock (_gate)
            {
                return [.. _opened];
            }
        }
    }

    /// <summary>Reads started (validated queries), in order.</summary>
    public IReadOnlyList<FakeEventLogRead> Reads
    {
        get
        {
            lock (_gate)
            {
                return [.. _reads];
            }
        }
    }

    /// <summary>Readers returned and not yet disposed.</summary>
    public int OpenReaders => Volatile.Read(ref _openReaders);

    /// <summary>A deterministic text naming the provider, the event ID and the values (Polish, like a rendered message).</summary>
    public static string DefaultMessage(EventLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var values = string.Join("; ", record.Data.Select(d => d.Key + ": " + d.Value));
        return $"Zdarzenie {record.EventId} ({record.Provider ?? "nieznany dostawca"}). {values}".TrimEnd();
    }

    /// <summary>Registers <paramref name="host"/> (reachable) with empty <paramref name="channels"/>.</summary>
    public FakeEventLogConnector AddHost(string host, params string[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        lock (_gate)
        {
            var entry = Host(host);
            foreach (var channel in channels)
            {
                entry.Channel(channel);
            }
        }

        return this;
    }

    /// <summary>Adds records to <paramref name="host"/>, each into the channel it names (<see cref="EventLogRecord.Channel"/>).</summary>
    public FakeEventLogConnector AddRecords(string host, IEnumerable<EventLogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            var entry = Host(host);
            foreach (var record in records)
            {
                ArgumentNullException.ThrowIfNull(record);
                entry.Channel(record.Channel).Records.Add(record);
            }
        }

        return this;
    }

    /// <summary>Adds records to the hosts they name (<see cref="EventLogRecord.Host"/>).</summary>
    public FakeEventLogConnector Add(params IEnumerable<EventLogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var group in records.GroupBy(r => EventHostNames.Normalize(r.Host)))
        {
            AddRecords(group.First().Host, group);
        }

        return this;
    }

    /// <summary>Replaces the computed coverage of <paramref name="host"/>/<paramref name="channel"/>.</summary>
    public FakeEventLogConnector SetCoverage(string host, string channel, EventLogCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        lock (_gate)
        {
            Host(host).Channel(channel).Coverage = coverage;
        }

        return this;
    }

    /// <summary><see cref="OpenComputerAsync"/> of <paramref name="host"/> fails with <paramref name="error"/>.</summary>
    public FakeEventLogConnector FailHost(string host, EventLogAccessError error, int? nativeErrorCode = null)
    {
        lock (_gate)
        {
            var entry = Host(host);
            entry.Failure = error;
            entry.NativeErrorCode = nativeErrorCode;
        }

        return this;
    }

    /// <summary>Reads and the coverage of <paramref name="channel"/> on <paramref name="host"/> fail with
    /// <paramref name="error"/> (e.g. <see cref="EventLogAccessError.AccessDenied"/> on Security only).</summary>
    public FakeEventLogConnector FailChannel(string host, string channel, EventLogAccessError error)
    {
        lock (_gate)
        {
            Host(host).Channel(channel).Failure = error;
        }

        return this;
    }

    /// <summary>Registers an <c>.evtx</c> file at <paramref name="path"/> with its records.</summary>
    public FakeEventLogConnector AddFile(string path, IEnumerable<EventLogRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var list))
            {
                _files[path] = list = [];
            }

            list.AddRange(records);
        }

        return this;
    }

    /// <summary>Registers a file the reader cannot open (<see cref="EventLogAccessError.FileUnreadable"/>).</summary>
    public FakeEventLogConnector AddUnreadableFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            _unreadableFiles.Add(path);
        }

        return this;
    }

    /// <inheritdoc/>
    public Task<IEventLogReader> OpenComputerAsync(string host, IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _opened.Add(host);
            if (!IsSupported)
            {
                throw new EventLogAccessException(EventLogAccessError.NotSupported);
            }

            if (!_hosts.TryGetValue(EventHostNames.Normalize(host), out var entry))
            {
                throw new EventLogAccessException(EventLogAccessError.Unreachable);
            }

            if (entry.Failure is { } failure)
            {
                throw new EventLogAccessException(failure) { NativeErrorCode = entry.NativeErrorCode };
            }
        }

        return Task.FromResult<IEventLogReader>(new FakeEventLogReader(this, host, files: null));
    }

    /// <inheritdoc/>
    public IEventLogReader OpenFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0 || paths.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one file path is required.", nameof(paths));
        }

        var name = string.Join(", ", paths.Select(FileName));
        return new FakeEventLogReader(this, name, [.. paths]);
    }

    internal void ReaderOpened() => Interlocked.Increment(ref _openReaders);

    internal void ReaderDisposed() => Interlocked.Decrement(ref _openReaders);

    internal IReadOnlyList<string> Channels(string host)
    {
        lock (_gate)
        {
            EnsureSupported();
            return [.. Entry(host).Channels.Values.Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

    internal EventLogCoverage Coverage(string host, string channel)
    {
        lock (_gate)
        {
            var entry = Readable(host, channel);
            if (entry.Coverage is { } coverage)
            {
                return coverage;
            }

            var records = entry.Records;
            return new EventLogCoverage
            {
                Host = host,
                Channel = entry.Name,
                Oldest = records.Count == 0 ? null : records.Min(r => r.TimeCreated),
                Newest = records.Count == 0 ? null : records.Max(r => r.TimeCreated),
                RecordCount = records.Count,
                FileSizeBytes = 69_632 + (records.Count * 1_024L),
                MaximumSizeBytes = 20_971_520,
                Mode = EventLogMode.Circular,
                IsEnabled = true,
                IsFull = false,
            };
        }
    }

    internal List<EventLogRecord> Snapshot(string host, string channel)
    {
        lock (_gate)
        {
            return [.. Readable(host, channel).Records];
        }
    }

    internal IReadOnlyList<string> FileChannels(IReadOnlyList<string> paths) =>
        [.. FileRecords(paths).Select(r => r.Channel).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    internal EventLogCoverage FileCoverage(string name, string channel, IReadOnlyList<string> paths)
    {
        var records = FileRecords(paths);
        return new EventLogCoverage
        {
            Host = name,
            Channel = channel,
            Oldest = records.Count == 0 ? null : records.Min(r => r.TimeCreated),
            Newest = records.Count == 0 ? null : records.Max(r => r.TimeCreated),
            RecordCount = records.Count,
            Mode = EventLogMode.Unknown,
        };
    }

    /// <summary>The records of the files, each with the file name as its host.</summary>
    internal List<EventLogRecord> FileRecords(IReadOnlyList<string> paths)
    {
        lock (_gate)
        {
            EnsureSupported();
            var records = new List<EventLogRecord>();
            foreach (var path in paths)
            {
                if (_unreadableFiles.Contains(path) || !_files.TryGetValue(path, out var list))
                {
                    throw new EventLogAccessException(EventLogAccessError.FileUnreadable);
                }

                var fileName = FileName(path);
                records.AddRange(list.Select(r => r with { Host = fileName }));
            }

            return records;
        }
    }

    internal async Task BeforeReadAsync(string host, EventLogQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _reads.Add(new FakeEventLogRead(host, query));
        }

        if (BeforeRead is { } hook)
        {
            await hook(host, query, cancellationToken).ConfigureAwait(false);
        }
    }

    internal void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new EventLogAccessException(EventLogAccessError.NotSupported);
        }
    }

    /// <summary>The file name of a Windows or POSIX path (the tests run on both).</summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    private FakeHost Host(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var key = EventHostNames.Normalize(host);
        if (!_hosts.TryGetValue(key, out var entry))
        {
            _hosts[key] = entry = new FakeHost();
        }

        return entry;
    }

    private FakeHost Entry(string host) =>
        _hosts.TryGetValue(EventHostNames.Normalize(host), out var entry) ? entry : throw new EventLogAccessException(EventLogAccessError.Unreachable);

    private FakeChannel Readable(string host, string channel)
    {
        EnsureSupported();
        if (!Entry(host).Channels.TryGetValue(channel, out var entry))
        {
            throw new EventLogAccessException(EventLogAccessError.ChannelNotFound);
        }

        return entry.Failure is { } failure ? throw new EventLogAccessException(failure) : entry;
    }

    private sealed class FakeHost
    {
        public Dictionary<string, FakeChannel> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);

        public EventLogAccessError? Failure { get; set; }

        public int? NativeErrorCode { get; set; }

        public FakeChannel Channel(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!Channels.TryGetValue(name, out var channel))
            {
                Channels[name] = channel = new FakeChannel(name);
            }

            return channel;
        }
    }

    private sealed class FakeChannel(string name)
    {
        public string Name { get; } = name;

        public List<EventLogRecord> Records { get; } = [];

        public EventLogCoverage? Coverage { get; set; }

        public EventLogAccessError? Failure { get; set; }
    }
}
