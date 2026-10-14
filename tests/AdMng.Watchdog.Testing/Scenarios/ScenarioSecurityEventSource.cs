using System.Runtime.CompilerServices;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Testing.Scenarios;

/// <summary>
/// <see cref="ISecurityEventSource"/> over records added by the test (raw event XML per DC host): queries filter by
/// event code and time window (<see cref="FailedLogonXml"/> records carry their time). <see cref="Queries"/> records
/// every query; <see cref="FailWith"/> makes reading throw <see cref="SecurityLogAccessException"/>.
/// </summary>
public sealed class ScenarioSecurityEventSource : ISecurityEventSource
{
    private readonly Lock _gate = new();
    private readonly List<(string Host, int Code, DateTimeOffset Time, string Xml)> _records = [];
    private readonly List<(string Host, SecurityEventQuery Query)> _queries = [];
    private int? _failure;

    public bool IsAvailable { get; set; } = true;

    /// <summary>Every query, in order.</summary>
    public IReadOnlyList<(string Host, SecurityEventQuery Query)> Queries
    {
        get
        {
            lock (_gate)
            {
                return [.. _queries];
            }
        }
    }

    /// <summary>Adds one record of <paramref name="host"/>'s Security log.</summary>
    public void Add(string host, int eventCode, DateTimeOffset timeCreated, string xml)
    {
        lock (_gate)
        {
            _records.Add((host, eventCode, timeCreated, xml));
        }
    }

    /// <summary>Reading fails with this Win32 error until <see cref="Recover"/>.</summary>
    public void FailWith(int win32Error)
    {
        lock (_gate)
        {
            _failure = win32Error;
        }
    }

    public void Recover()
    {
        lock (_gate)
        {
            _failure = null;
        }
    }

    public Task<SecurityLogInfo> GetLogInfoAsync(string host, string logName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(new SecurityLogInfo(1, _records.Count(r => string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase)), false, null));
        }
    }

    public async IAsyncEnumerable<string> ReadAsync(string host, SecurityEventQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<string> matches;
        lock (_gate)
        {
            _queries.Add((host, query));
            if (_failure is { } error)
            {
                throw new SecurityLogAccessException(host, error, "Simulated Security log failure.");
            }

            matches = [.. _records
                .Where(r => string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase))
                .Where(r => query.EventCodes.Count == 0 || query.EventCodes.Contains(r.Code))
                .Where(r => (query.From is null || r.Time >= query.From) && (query.To is null || r.Time <= query.To))
                .Take(query.MaxRecords)
                .Select(r => r.Xml)];
        }

        foreach (var xml in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return xml;
        }
    }
}
