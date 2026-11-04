using System.Diagnostics.Eventing.Reader;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using AdMng.Core.EventLogs;
using EventLogQuery = AdMng.Core.EventLogs.EventLogQuery;
using EventLogRecord = AdMng.Core.EventLogs.EventLogRecord;
using WindowsEventLogQuery = System.Diagnostics.Eventing.Reader.EventLogQuery;

namespace AdMng.EventLogs;

/// <summary>
/// <c>.evtx</c> files the operator chose (design note e-event-logs.md 3.1), read in place as the process identity – no
/// copy, nothing cached. Each file is queried with the structured query pointed at it (<see cref="ReaderSupport.ForFile"/>)
/// and read forward, the only direction every file kind supports; the files are merged by time and "newest first" is
/// applied after the read only with <see cref="EventLogQuery.MaxRecords"/> – without it every record is streamed oldest
/// first, so the caller's memory bound counts what it drops (<see cref="EventRecordMerge.Order"/>). The host of a record is
/// its file name, its channel the one the record names. Messages come from this computer's publisher metadata (or the
/// file's <c>LocaleMetaData</c>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFileEventLogReader : IEventLogReader
{
    private readonly IReadOnlyList<string> _paths;
    private readonly IReadOnlyList<string> _hosts;

    // Cancelled on dispose; deliberately not disposed, so an operation starting concurrently links to it safely.
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public WindowsFileEventLogReader(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfZero(paths.Count);
        _paths = paths;
        _hosts = [.. paths.Select(ReaderSupport.FileHost)];
        Host = ReaderSupport.FilesHost(paths);
    }

    public string Host { get; }

    /// <summary>The channels the files hold, as their first records name them (one record per file is read).</summary>
    public Task<IReadOnlyList<string>> GetChannelsAsync(CancellationToken cancellationToken) => RunAsync<IReadOnlyList<string>>(
        token =>
        {
            var names = new List<string?>();
            foreach (var path in _paths)
            {
                using var reader = new EventLogReader(new WindowsEventLogQuery(path, PathType.FilePath)) { BatchSize = 1 };
                using var registration = NativeEventLog.RegisterCancel(token, reader);
                using var record = reader.ReadEvent();
                names.Add(record?.LogName);
            }

            return ReaderSupport.Channels(names);
        },
        cancellationToken);

    /// <summary>
    /// The reach of all files together (<paramref name="channel"/> is only echoed): the oldest and newest record, record
    /// counts and sizes summed; mode, enablement and maximum size do not apply to a file.
    /// </summary>
    public Task<EventLogCoverage> GetCoverageAsync(string channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return RunAsync(
            _ =>
            {
                DateTimeOffset? oldest = null;
                DateTimeOffset? newest = null;
                var counts = new List<long?>();
                var sizes = new List<long?>();
                foreach (var path in _paths)
                {
                    var information = EventLogSession.GlobalSession.GetLogInformation(path, PathType.FilePath);
                    counts.Add(information.RecordCount);
                    sizes.Add(information.FileSize);
                    oldest = ReaderSupport.Earliest(oldest, NativeEventLog.FirstRecordTime(new WindowsEventLogQuery(path, PathType.FilePath)));

                    // Reverse reading works for .evtx (not for .evt / .etl, which give no newest time then).
                    newest = ReaderSupport.Latest(newest, NativeEventLog.FirstRecordTime(new WindowsEventLogQuery(path, PathType.FilePath) { ReverseDirection = true }));
                }

                return new EventLogCoverage
                {
                    Host = Host,
                    Channel = channel,
                    Oldest = oldest,
                    Newest = newest,
                    RecordCount = ReaderSupport.Sum(counts),
                    FileSizeBytes = ReaderSupport.Sum(sizes),
                };
            },
            cancellationToken);
    }

    public IAsyncEnumerable<EventLogRecord> ReadAsync(EventLogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return ReadCoreAsync(query, cancellationToken);
    }

    /// <summary>Re-reads the record by its number and time in the file(s) of its name and renders its message (null when that fails).</summary>
    public async Task<string?> FormatMessageAsync(EventLogRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.RecordId < 0)
        {
            return null;
        }

        var candidates = _paths.Where((_, index) => string.Equals(_hosts[index], record.Host, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        try
        {
            return await RunAsync(
                token =>
                {
                    foreach (var path in candidates)
                    {
                        var message = NativeEventLog.FormatMessage(new WindowsEventLogQuery(path, PathType.FilePath, ReaderSupport.RecordQuery(record)), record, token);
                        if (message is not null)
                        {
                            return message;
                        }
                    }

                    return null;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (EventLogAccessException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>One file read forward with its query; each record parsed with the file name as its host.</summary>
    private static IEnumerable<EventLogRecord> ReadFile(string path, string xml, string host, CancellationToken cancellationToken)
    {
        using var reader = new EventLogReader(new WindowsEventLogQuery(path, PathType.FilePath, xml)) { BatchSize = EventLogLimits.BatchSize };
        using var registration = NativeEventLog.RegisterCancel(cancellationToken, reader);
        foreach (var text in NativeEventLog.ReadXml(reader, cancellationToken))
        {
            if (EventXmlParser.TryParse(text, host, out var record))
            {
                yield return record;
            }
        }
    }

    private async IAsyncEnumerable<EventLogRecord> ReadCoreAsync(EventLogQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The file is the log: the structured query is built for a fixed channel and then pointed at each file.
        var xml = ReaderSupport.BuildQuery(query with { Channel = ReaderSupport.FileQueryChannel });
        await foreach (var record in RecordStream.RunAsync<EventLogRecord>(
            (writer, token) => Produce(query, xml, writer, token),
            EventLogLimits.BatchSize,
            _lifetime.Token,
            cancellationToken).ConfigureAwait(false))
        {
            yield return record;
        }
    }

    private void Produce(EventLogQuery query, string xml, ChannelWriter<EventLogRecord> writer, CancellationToken cancellationToken)
    {
        try
        {
            var sources = _paths.Select((path, index) => ReadFile(path, ReaderSupport.ForFile(xml, path), _hosts[index], cancellationToken)).ToList();
            RecordStream.Pump(EventRecordMerge.Order(sources, query.NewestFirst, query.MaxRecords), record => record, query.MaxRecords, writer, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (EventLogErrors.IsTranslatable(ex))
        {
            throw EventLogErrors.Translate(ex, file: true);
        }
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, T> call, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = stop.Token;
        return await NativeEventLog.RunAsync(
            _ =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    return call(token);
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
                catch (Exception ex) when (EventLogErrors.IsTranslatable(ex))
                {
                    throw EventLogErrors.Translate(ex, file: true);
                }
            },
            token).ConfigureAwait(false);
    }
}
