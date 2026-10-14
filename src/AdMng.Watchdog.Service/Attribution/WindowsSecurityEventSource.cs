using System.Diagnostics.Eventing.Reader;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Attribution;

/// <summary>
/// Reads the Security (or ForwardedEvents) log of a DC through the remote Event Log API (spec 11): one
/// <see cref="EventLogSession"/> per call as the service identity (the gMSA in "Event Log Readers" on the DCs), the
/// structured query of <see cref="SecurityEventXPath"/>, raw <c>EventRecord.ToXml()</c> in query order. RPC/ACL failures
/// become <see cref="SecurityLogAccessException"/> with the Win32 error only (no message text).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecurityEventSource : ISecurityEventSource
{
    public bool IsAvailable => true;

    public Task<SecurityLogInfo> GetLogInfoAsync(string host, string logName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(logName);
        return Task.Run(
            () =>
            {
                try
                {
                    using var session = new EventLogSession(host);
                    var info = session.GetLogInformation(logName, PathType.LogName);
                    return new SecurityLogInfo(info.OldestRecordNumber, info.RecordCount, info.IsLogFull ?? false, info.FileSize);
                }
                catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
                {
                    throw AccessFailed(host, ex);
                }
            },
            cancellationToken);
    }

    public async IAsyncEnumerable<string> ReadAsync(string host, SecurityEventQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentNullException.ThrowIfNull(query);
        var xml = SecurityEventXPath.Build(query);
        using var session = new EventLogSession(host);
        var logQuery = new EventLogQuery(query.LogName, PathType.LogName, xml) { Session = session, ReverseDirection = query.NewestFirst };
        EventLogReader reader;
        try
        {
            reader = await Task.Run(() => new EventLogReader(logQuery), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            throw AccessFailed(host, ex);
        }

        using (reader)
        {
            var read = 0;
            while (read < query.MaxRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await Task.Run(() => ReadBatch(reader, host, Math.Min(256, query.MaxRecords - read)), cancellationToken).ConfigureAwait(false);
                foreach (var record in batch)
                {
                    yield return record;
                }

                read += batch.Count;
                if (batch.Count == 0)
                {
                    yield break;
                }
            }
        }
    }

    private static List<string> ReadBatch(EventLogReader reader, string host, int max)
    {
        var batch = new List<string>(max);
        try
        {
            while (batch.Count < max && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    batch.Add(record.ToXml());
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            throw AccessFailed(host, ex);
        }

        return batch;
    }

    private static SecurityLogAccessException AccessFailed(string host, Exception error)
    {
        int? win32 = error is EventLogException eventLog ? eventLog.HResult & 0xFFFF : error is UnauthorizedAccessException ? 5 : null;
        return new SecurityLogAccessException(host, win32, "The Security log could not be read.", error);
    }
}
