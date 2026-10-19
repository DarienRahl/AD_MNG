using System.Diagnostics.Eventing.Reader;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Service.Attribution;

/// <summary>
/// Reads the Security (or ForwardedEvents) log of a DC through the remote Event Log API (spec 11): one
/// <see cref="EventLogSession"/> per call as the service identity (the gMSA in "Event Log Readers" on the DCs) with
/// <see cref="SessionAuthentication.Kerberos"/> – never Negotiate, which falls back to NTLM and would offer the gMSA's
/// credentials to whatever host a configuration names –, the structured query of <see cref="SecurityEventXPath"/>, raw
/// <c>EventRecord.ToXml()</c> in query order. A host that is not a host name (an IP literal) is refused before any
/// connection. RPC/ACL failures become <see cref="SecurityLogAccessException"/> with the Win32 error only (no message
/// text).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecurityEventSource : ISecurityEventSource
{
    /// <summary>Win32 ERROR_INVALID_NAME (123): the host is not a DNS host name.</summary>
    public const int InvalidHostNameError = 123;

    public bool IsAvailable => true;

    /// <summary>A Kerberos-only session to <paramref name="host"/> as the process identity.</summary>
    /// <exception cref="SecurityLogAccessException">The host is not a host name (Win32 123).</exception>
    public static EventLogSession OpenSession(string host)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        if (!HostNames.IsHostName(HostNames.Normalize(host)))
        {
            throw new SecurityLogAccessException(host, InvalidHostNameError, "The event log host must be a DNS host name.");
        }

        return new EventLogSession(host, null, null, (SecureString?)null, SessionAuthentication.Kerberos);
    }

    public Task<SecurityLogInfo> GetLogInfoAsync(string host, string logName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(logName);
        return Task.Run(
            () =>
            {
                try
                {
                    using var session = OpenSession(host);
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
        EventLogSession session;
        try
        {
            session = OpenSession(host);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            throw AccessFailed(host, ex);
        }

        using var ownedSession = session;
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
