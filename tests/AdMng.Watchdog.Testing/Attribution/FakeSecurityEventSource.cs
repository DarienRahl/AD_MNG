using System.Runtime.CompilerServices;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Attribution;

/// <summary>
/// In-memory <see cref="ISecurityEventSource"/>: per host and log a list of record XML (Appendix A fixtures or
/// <see cref="Fixtures.SecurityEvents.SecurityEventXml"/>), filtered like the Windows reader does – event ids, record id
/// cursor, time bounds, EventData equality filters, 5136 attribute suppression, newest-first order and the record cap.
/// Every query is also rendered with <see cref="SecurityEventQueryBuilder"/> (rejecting what the real reader would)
/// and recorded. Hosts can fail with <see cref="SecurityLogAccessException"/> (Win32 code), logs can be cleared (record
/// ids restart), and <see cref="BeforeRead"/> lets a test hold a read (timeouts, one query per DC).
/// </summary>
public sealed class FakeSecurityEventSource : ISecurityEventSource
{
    /// <summary>Win32 error 5 (access denied – not in Event Log Readers).</summary>
    public const int AccessDenied = 5;

    /// <summary>Win32 error 1722 (RPC server unavailable – firewall).</summary>
    public const int RpcUnavailable = 1722;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Host, string Log), List<StoredRecord>> _logs = [];
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Host, string Log), long> _nextRecordId = [];
    private readonly List<(string Host, SecurityEventQuery Query, string QueryXml)> _queries = [];
    private readonly SecurityEventParser _parser = new();

    public FakeSecurityEventSource(bool isAvailable = true) => IsAvailable = isAvailable;

    /// <inheritdoc/>
    public bool IsAvailable { get; set; }

    /// <summary>Invoked at the start of every read (after the query was recorded) – await a gate here.</summary>
    public Func<string, SecurityEventQuery, CancellationToken, Task>? BeforeRead { get; set; }

    /// <summary>Every query in call order (host, query, rendered QueryList XML).</summary>
    public IReadOnlyList<(string Host, SecurityEventQuery Query, string QueryXml)> Queries
    {
        get
        {
            lock (_gate)
            {
                return [.. _queries];
            }
        }
    }

    /// <summary>Adds a record (its EventRecordID is taken from the XML).</summary>
    public void Add(string host, string xml, string logName = "Security")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        var parsed = _parser.Parse(xml, host) ?? throw new ArgumentException("The fixture is not a valid event record.", nameof(xml));
        lock (_gate)
        {
            var key = Key(host, logName);
            if (!_logs.TryGetValue(key, out var records))
            {
                records = [];
                _logs[key] = records;
            }

            records.Add(new StoredRecord(xml, parsed.Event));
            _nextRecordId[key] = Math.Max(_nextRecordId.GetValueOrDefault(key, 1), parsed.Event.RecordId + 1);
        }
    }

    /// <summary>Adds a record built by <paramref name="create"/> with the next record id of that log; returns the id.</summary>
    public long Add(string host, Func<long, string> create, string logName = "Security")
    {
        ArgumentNullException.ThrowIfNull(create);
        long recordId;
        lock (_gate)
        {
            recordId = _nextRecordId.GetValueOrDefault(Key(host, logName), 1);
        }

        Add(host, create(recordId), logName);
        return recordId;
    }

    /// <summary>Sets the next record id of a log (e.g. a long-running DC log starting at 175 000).</summary>
    public void SetNextRecordId(string host, long next, string logName = "Security")
    {
        lock (_gate)
        {
            _nextRecordId[Key(host, logName)] = next;
        }
    }

    /// <summary>Removes every record of a log; the record ids restart at <paramref name="restartAt"/> (log cleared or
    /// recreated – a cursor regression).</summary>
    public void Clear(string host, long restartAt = 1, string logName = "Security")
    {
        lock (_gate)
        {
            _logs.Remove(Key(host, logName));
            _nextRecordId[Key(host, logName)] = restartAt;
        }
    }

    /// <summary>Drops the oldest records up to <paramref name="lastRecordId"/> (overwritten by a full log).</summary>
    public void Overwrite(string host, long lastRecordId, string logName = "Security")
    {
        lock (_gate)
        {
            if (_logs.TryGetValue(Key(host, logName), out var records))
            {
                records.RemoveAll(r => r.Event.RecordId <= lastRecordId);
            }
        }
    }

    /// <summary>Makes every call for <paramref name="host"/> fail with <paramref name="win32Error"/>.</summary>
    public void Fail(string host, int win32Error = RpcUnavailable)
    {
        lock (_gate)
        {
            _failures[host] = win32Error;
        }
    }

    /// <summary>Ends <see cref="Fail"/>.</summary>
    public void Recover(string host)
    {
        lock (_gate)
        {
            _failures.Remove(host);
        }
    }

    /// <inheritdoc/>
    public Task<SecurityLogInfo> GetLogInfoAsync(string host, string logName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfFailing(host);
            var records = _logs.GetValueOrDefault(Key(host, logName)) ?? [];
            return Task.FromResult(new SecurityLogInfo(
                records.Count == 0 ? null : records.Min(r => r.Event.RecordId),
                records.Count,
                IsLogFull: false,
                FileSizeBytes: records.Sum(r => (long)r.Xml.Length)));
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> ReadAsync(string host, SecurityEventQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var queryXml = SecurityEventQueryBuilder.Build(query);
        lock (_gate)
        {
            _queries.Add((host, query, queryXml));
            ThrowIfFailing(host);
        }

        if (BeforeRead is { } before)
        {
            await before(host, query, cancellationToken).ConfigureAwait(false);
        }

        List<string> result;
        lock (_gate)
        {
            ThrowIfFailing(host);
            var records = _logs.GetValueOrDefault(Key(host, query.LogName)) ?? [];
            var matching = records.Where(r => Matches(r.Event, query));
            matching = query.NewestFirst ? matching.OrderByDescending(r => r.Event.RecordId) : matching.OrderBy(r => r.Event.RecordId);
            result = [.. matching.Take(query.MaxRecords).Select(r => r.Xml)];
        }

        foreach (var xml in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return xml;
        }
    }

    private static bool Matches(SecurityEvent record, SecurityEventQuery query) =>
        (query.EventCodes.Count == 0 || query.EventCodes.Contains(record.EventCode))
        && (query.AfterRecordId is not { } after || record.RecordId > after)
        && (query.From is not { } from || record.TimeCreated >= from)
        && (query.To is not { } to || record.TimeCreated <= to)
        && query.DataEquals.All(f => string.Equals(record.Get(f.Name), f.Value, StringComparison.OrdinalIgnoreCase))
        && !(record.EventCode == SecurityEventCatalog.ObjectModified
             && query.SuppressAttributes.Contains(record.Get("AttributeLDAPDisplayName") ?? string.Empty, StringComparer.OrdinalIgnoreCase));

    private static (string, string) Key(string host, string logName) => (host.ToLowerInvariant(), logName);

    private void ThrowIfFailing(string host)
    {
        if (_failures.TryGetValue(host, out var error))
        {
            throw new SecurityLogAccessException(host, error, "The event log of the host cannot be read.");
        }
    }

    private sealed record StoredRecord(string Xml, SecurityEvent Event);
}
