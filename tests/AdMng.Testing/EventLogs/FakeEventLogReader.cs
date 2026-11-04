using System.Runtime.CompilerServices;
using AdMng.Core.EventLogs;

namespace AdMng.Testing.EventLogs;

/// <summary>
/// A source opened by <see cref="FakeEventLogConnector"/>: a computer (records per channel) or a set of files (all
/// records of the files, <see cref="EventLogQuery.Channel"/> ignored, each record's host = its file name). Every call
/// after <see cref="DisposeAsync"/> throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class FakeEventLogReader : IEventLogReader
{
    /// <summary>The channel name used to validate a file query (a file reader ignores the query's channel).</summary>
    private const string FileValidationChannel = "Application";

    private readonly FakeEventLogConnector _connector;
    private readonly IReadOnlyList<string>? _files;
    private int _disposed;

    internal FakeEventLogReader(FakeEventLogConnector connector, string host, IReadOnlyList<string>? files)
    {
        _connector = connector;
        _files = files;
        Host = host;
        connector.ReaderOpened();
    }

    /// <inheritdoc/>
    public string Host { get; }

    /// <summary>A reader over files (<see cref="IEventLogConnector.OpenFiles"/>).</summary>
    public bool IsFileReader => _files is not null;

    /// <summary><see cref="DisposeAsync"/> was called.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_files is null ? _connector.Channels(Host) : _connector.FileChannels(_files));
    }

    /// <inheritdoc/>
    public Task<EventLogCoverage> GetCoverageAsync(string channel, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_files is null ? _connector.Coverage(Host, channel) : _connector.FileCoverage(Host, channel, _files));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<EventLogRecord> ReadAsync(EventLogQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Validate(_files is null ? query : query with { Channel = FileValidationChannel });
        var records = _files is null ? _connector.Snapshot(Host, query.Channel) : _connector.FileRecords(_files);
        await _connector.BeforeReadAsync(Host, query, cancellationToken).ConfigureAwait(false);

        var selected = EventLogMatcher.Apply(query, records);
        var batch = Math.Max(1, _connector.BatchSize);
        for (var i = 0; i < selected.Count; i++)
        {
            if (i % batch == 0)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }

            yield return _files is null ? selected[i] with { Host = Host } : selected[i];
        }
    }

    /// <inheritdoc/>
    public Task<string?> FormatMessageAsync(EventLogRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_connector.MessageFormatter(record));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _connector.ReaderDisposed();
        }

        return ValueTask.CompletedTask;
    }

    public override string ToString() => IsFileReader ? "event log files" : "event log reader";

    private static void Validate(EventLogQuery query)
    {
        try
        {
            _ = EventQueryXml.Build(query);
        }
        catch (ArgumentException ex)
        {
            throw new EventLogAccessException(EventLogAccessError.InvalidQuery, ex);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
