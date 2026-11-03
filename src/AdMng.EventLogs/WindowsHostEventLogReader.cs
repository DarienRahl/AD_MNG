using System.Diagnostics.Eventing.Reader;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security;
using System.Threading.Channels;
using AdMng.Core.EventLogs;
using AdMng.Directory;
using EventLogMode = AdMng.Core.EventLogs.EventLogMode;
using EventLogQuery = AdMng.Core.EventLogs.EventLogQuery;
using EventLogRecord = AdMng.Core.EventLogs.EventLogRecord;
using WindowsEventLogQuery = System.Diagnostics.Eventing.Reader.EventLogQuery;

namespace AdMng.EventLogs;

/// <summary>
/// The event logs of one computer over MS-EVEN6 (design note e-event-logs.md 1, 3.2). Every operation opens its own
/// <see cref="EventLogSession"/> to the FQDN with <see cref="SessionAuthentication.Kerberos"/> – never Negotiate, which
/// could fall back to NTLM – as the window's Windows account or with a read-only copy of its in-memory password
/// (<see cref="SessionLogon"/>, disposed with the reader; the API copies it only while opening the session). One session
/// per operation lets a cancellation (<see cref="EventLogSession.CancelCurrentOperations"/>) stop exactly that operation.
/// Disposing the reader cancels what still runs. Nothing is logged or kept beyond the channel list.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHostEventLogReader : IEventLogReader
{
    private readonly SessionLogon _logon;

    // Cancelled on dispose; deliberately not disposed, so an operation starting concurrently links to it safely.
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<string>? _channels;
    private int _disposed;

    public WindowsHostEventLogReader(string host, SessionLogon logon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(logon);
        Host = host;
        _logon = logon;
    }

    public string Host { get; }

    /// <summary>Connects once (the channel list) so an unreachable host or a refused logon fails on open.</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken) =>
        _channels = await ReadChannelsAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetChannelsAsync(CancellationToken cancellationToken) =>
        _channels ??= await ReadChannelsAsync(cancellationToken).ConfigureAwait(false);

    public Task<EventLogCoverage> GetCoverageAsync(string channel, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return RunAsync(
            (session, _) =>
            {
                var information = session.GetLogInformation(channel, PathType.LogName);
                var (mode, enabled, maximum) = ReadConfiguration(session, channel);
                return new EventLogCoverage
                {
                    Host = Host,
                    Channel = channel,
                    Oldest = NativeEventLog.FirstRecordTime(new WindowsEventLogQuery(channel, PathType.LogName) { Session = session }),
                    Newest = NativeEventLog.FirstRecordTime(new WindowsEventLogQuery(channel, PathType.LogName) { Session = session, ReverseDirection = true }),
                    RecordCount = information.RecordCount,
                    FileSizeBytes = information.FileSize,
                    MaximumSizeBytes = maximum,
                    Mode = mode,
                    IsEnabled = enabled,
                    IsFull = information.IsLogFull ?? false,
                };
            },
            cancellationToken);
    }

    public IAsyncEnumerable<EventLogRecord> ReadAsync(EventLogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Channel);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return ReadCoreAsync(query, cancellationToken);
    }

    /// <summary>Re-reads the record by its number in its channel and renders its message (null when that fails).</summary>
    public Task<string?> FormatMessageAsync(EventLogRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!string.Equals(record.Host, Host, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(record.Channel) || record.RecordId < 0)
        {
            return Task.FromResult<string?>(null);
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return FormatCoreAsync(record, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
            _logon.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static (EventLogMode Mode, bool Enabled, long? Maximum) ReadConfiguration(EventLogSession session, string channel)
    {
        try
        {
            using var configuration = new EventLogConfiguration(channel, session);
            return (ReaderSupport.Mode((int)configuration.LogMode), configuration.IsEnabled, configuration.MaximumSizeInBytes);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            // The configuration may be closed to the account while the log is readable: the rest of the coverage stands.
            return (EventLogMode.Unknown, true, null);
        }
    }

    private Task<IReadOnlyList<string>> ReadChannelsAsync(CancellationToken cancellationToken) =>
        RunAsync((session, _) => ReaderSupport.Channels(session.GetLogNames()), cancellationToken);

    private async Task<string?> FormatCoreAsync(EventLogRecord record, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(
                (session, token) => NativeEventLog.FormatMessage(
                    new WindowsEventLogQuery(record.Channel, PathType.LogName, ReaderSupport.RecordQuery(record.RecordId)) { Session = session },
                    record,
                    token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (EventLogAccessException)
        {
            return null;
        }
    }

    private async IAsyncEnumerable<EventLogRecord> ReadCoreAsync(EventLogQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var xml = ReaderSupport.BuildQuery(query);
        await foreach (var record in RecordStream.RunAsync<EventLogRecord>(
            (writer, token) => Produce(query, xml, writer, token),
            EventLogLimits.BatchSize,
            _lifetime.Token,
            cancellationToken).ConfigureAwait(false))
        {
            yield return record;
        }
    }

    /// <summary>The producer of a read: the structured query on the target, newest first when asked, in batches.</summary>
    private void Produce(EventLogQuery query, string xml, ChannelWriter<EventLogRecord> writer, CancellationToken cancellationToken)
    {
        try
        {
            using var session = OpenSession();
            using var cancelSession = cancellationToken.Register(NativeEventLog.Cancel, session);
            cancellationToken.ThrowIfCancellationRequested();
            var logQuery = new WindowsEventLogQuery(query.Channel, PathType.LogName, xml) { Session = session, ReverseDirection = query.NewestFirst };
            using var reader = new EventLogReader(logQuery) { BatchSize = EventLogLimits.BatchSize };
            using var cancelReading = cancellationToken.Register(NativeEventLog.Cancel, reader);
            RecordStream.Pump(
                NativeEventLog.ReadXml(reader, cancellationToken),
                text => EventXmlParser.TryParse(text, Host, out var record) ? record : null,
                query.MaxRecords,
                writer,
                cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (EventLogErrors.IsTranslatable(ex))
        {
            throw EventLogErrors.Translate(ex, file: false);
        }
    }

    /// <summary>Runs <paramref name="call"/> with its own session on a dedicated thread; failures become <see cref="EventLogAccessException"/>.</summary>
    private async Task<T> RunAsync<T>(Func<EventLogSession, CancellationToken, T> call, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = stop.Token;
        return await NativeEventLog.RunAsync(
            _ =>
            {
                try
                {
                    using var session = OpenSession();
                    using var registration = token.Register(NativeEventLog.Cancel, session);
                    token.ThrowIfCancellationRequested();
                    return call(session, token);
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
                catch (Exception ex) when (EventLogErrors.IsTranslatable(ex))
                {
                    throw EventLogErrors.Translate(ex, file: false);
                }
            },
            token).ConfigureAwait(false);
    }

    /// <summary>A Kerberos-only session to the host as the window's logon (the password is copied only while opening).</summary>
    private EventLogSession OpenSession() => _logon.IsWindowsAccount
        ? new EventLogSession(Host, null, null, (SecureString?)null, SessionAuthentication.Kerberos)
        : new EventLogSession(Host, _logon.Domain, _logon.UserName, _logon.Password, SessionAuthentication.Kerberos);
}
