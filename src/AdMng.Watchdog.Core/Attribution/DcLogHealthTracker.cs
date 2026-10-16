using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Per-DC log facts of the attribution engine, kept in memory for the last 24 hours (section 11.1 status, 11.7 reasons):
/// last read and record id, read failures (Win32 code), overwritten records, on-demand source lookups (count, average
/// duration, timeouts) and attribution coverage (share of attributable changes attributed – NotApplicable kinds and
/// lockout resets are not counted). Thread-safe.
/// </summary>
public sealed class DcLogHealthTracker(TimeProvider timeProvider)
{
    /// <summary>Coverage below this share degrades the DC (HealthAttributionDegraded, 7).</summary>
    public const double MinimumCoverage = 0.5;

    /// <summary>Coverage is judged only from this many attributable changes on.</summary>
    public const int MinimumCoverageSamples = 10;

    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DcLogState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A successful read of <paramref name="host"/>.</summary>
    public void RecordRead(string host, long? lastRecordId)
    {
        lock (_gate)
        {
            var state = State(host);
            state.LastReadAt = timeProvider.GetUtcNow();
            state.LastRecordId = lastRecordId ?? state.LastRecordId;
            state.ErrorCode = null;
        }
    }

    /// <summary>A failed read (RPC/ACL); <paramref name="errorCode"/> is the Win32 code or an exception code.</summary>
    public void RecordFailure(string host, string errorCode)
    {
        lock (_gate)
        {
            var state = State(host);
            state.ErrorCode = errorCode;
            state.LastFailureAt = timeProvider.GetUtcNow();
        }
    }

    /// <summary>Records of <paramref name="host"/> were overwritten before they were read.</summary>
    public void RecordOverwritten(string host)
    {
        lock (_gate)
        {
            State(host).OverwrittenAt = timeProvider.GetUtcNow();
        }
    }

    /// <summary>Remembers how the DC names itself in System/Computer.</summary>
    public void RecordComputerName(string host, string computer)
    {
        lock (_gate)
        {
            State(host).Computer = computer;
        }
    }

    /// <summary>One on-demand source lookup (4624/4771/4776/4625).</summary>
    public void RecordLookup(string host, TimeSpan duration, bool timedOut)
    {
        lock (_gate)
        {
            State(host).Lookups.Add((timeProvider.GetUtcNow(), duration, timedOut));
        }
    }

    /// <summary>One finished attribution of an attributable change on <paramref name="host"/>.</summary>
    public void RecordOutcome(string host, bool attributed)
    {
        lock (_gate)
        {
            State(host).Outcomes.Add((timeProvider.GetUtcNow(), attributed));
        }
    }

    /// <summary>True when a read of <paramref name="host"/> failed at or after <paramref name="since"/>.</summary>
    public bool WasUnreachableSince(string host, DateTimeOffset since)
    {
        lock (_gate)
        {
            return _hosts.TryGetValue(Key(host), out var state) && state.LastFailureAt >= since;
        }
    }

    /// <summary>True when overwritten records were detected for <paramref name="host"/> at or after <paramref name="since"/>.</summary>
    public bool WasOverwrittenSince(string host, DateTimeOffset since)
    {
        lock (_gate)
        {
            return _hosts.TryGetValue(Key(host), out var state) && state.OverwrittenAt >= since;
        }
    }

    /// <summary>System/Computer of <paramref name="host"/> when seen, else the host itself.</summary>
    public string ComputerName(string host)
    {
        lock (_gate)
        {
            return _hosts.TryGetValue(Key(host), out var state) && state.Computer is { } computer ? computer : host;
        }
    }

    /// <summary>Attributed share of the last 24 hours; null without samples.</summary>
    public double? Coverage(string host)
    {
        lock (_gate)
        {
            if (!_hosts.TryGetValue(Key(host), out var state))
            {
                return null;
            }

            state.Prune(timeProvider.GetUtcNow() - Window);
            return state.Outcomes.Count == 0 ? null : state.Outcomes.Count(o => o.Attributed) / (double)state.Outcomes.Count;
        }
    }

    /// <summary>True when the coverage of <paramref name="host"/> is below <see cref="MinimumCoverage"/> over at least
    /// <see cref="MinimumCoverageSamples"/> changes.</summary>
    public bool IsCoverageLow(string host)
    {
        lock (_gate)
        {
            if (!_hosts.TryGetValue(Key(host), out var state))
            {
                return false;
            }

            state.Prune(timeProvider.GetUtcNow() - Window);
            return state.Outcomes.Count >= MinimumCoverageSamples
                && state.Outcomes.Count(o => o.Attributed) / (double)state.Outcomes.Count < MinimumCoverage;
        }
    }

    /// <summary>The status rows of <paramref name="hosts"/> (<see cref="AttributionStatusDto.DomainControllers"/>).</summary>
    public IReadOnlyList<DcLogStatusDto> Snapshot(IEnumerable<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var since = timeProvider.GetUtcNow() - Window;
        var rows = new List<DcLogStatusDto>();
        lock (_gate)
        {
            foreach (var host in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var state = State(host);
                state.Prune(since);
                var completed = state.Lookups.Where(l => !l.TimedOut).ToList();
                double? coverage = state.Outcomes.Count == 0 ? null : state.Outcomes.Count(o => o.Attributed) / (double)state.Outcomes.Count;
                rows.Add(new DcLogStatusDto
                {
                    Host = Key(host),
                    State = StateOf(state, coverage),
                    LastReadAt = state.LastReadAt,
                    LastRecordId = state.LastRecordId,
                    Coverage24h = coverage,
                    ErrorCode = state.ErrorCode,
                    SourceLookups24h = state.Lookups.Count,
                    SourceLookupAverage = completed.Count == 0 ? null : TimeSpan.FromTicks((long)completed.Average(l => l.Duration.Ticks)),
                    SourceLookupTimeouts24h = state.Lookups.Count(l => l.TimedOut),
                });
            }
        }

        return rows;
    }

    private static HealthState StateOf(DcLogState state, double? coverage)
    {
        if (state.ErrorCode is not null)
        {
            return HealthState.Error;
        }

        if (state.LastReadAt is null)
        {
            return HealthState.Unknown;
        }

        return coverage < MinimumCoverage && state.Outcomes.Count >= MinimumCoverageSamples ? HealthState.Warning : HealthState.Ok;
    }

    private static string Key(string host) => HostNames.Normalize(host);

    private DcLogState State(string host)
    {
        var key = Key(host);
        if (!_hosts.TryGetValue(key, out var state))
        {
            state = new DcLogState();
            _hosts[key] = state;
        }

        return state;
    }
}
