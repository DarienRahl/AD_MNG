using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Testing.Scenarios;

/// <summary>Recording <see cref="IHealthRegistry"/>: the latest check per code plus the full report history.</summary>
public sealed class ScenarioHealthRegistry : IHealthRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HealthCheckDto> _checks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChannelHealthDto> _channels = new(StringComparer.Ordinal);
    private readonly List<HealthCheckDto> _history = [];
    private DirectoryStatusDto _directory = new() { State = HealthState.Unknown };
    private AttributionStatusDto _attribution = new();

    public IReadOnlyList<HealthCheckDto> Checks
    {
        get
        {
            lock (_gate)
            {
                return [.. _checks.Values];
            }
        }
    }

    /// <summary>Every reported check, in order.</summary>
    public IReadOnlyList<HealthCheckDto> History
    {
        get
        {
            lock (_gate)
            {
                return [.. _history];
            }
        }
    }

    public IReadOnlyList<ChannelHealthDto> Channels
    {
        get
        {
            lock (_gate)
            {
                return [.. _channels.Values];
            }
        }
    }

    public DirectoryStatusDto Directory
    {
        get
        {
            lock (_gate)
            {
                return _directory;
            }
        }
    }

    public AttributionStatusDto Attribution
    {
        get
        {
            lock (_gate)
            {
                return _attribution;
            }
        }
    }

    /// <summary>The latest check of <paramref name="code"/>, or null.</summary>
    public HealthCheckDto? Get(string code)
    {
        lock (_gate)
        {
            return _checks.GetValueOrDefault(code);
        }
    }

    public void Report(HealthCheckDto check)
    {
        ArgumentNullException.ThrowIfNull(check);
        lock (_gate)
        {
            _checks[check.Code] = check;
            _history.Add(check);
        }
    }

    public void ReportChannel(ChannelHealthDto channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_gate)
        {
            _channels[channel.ChannelId] = channel;
        }
    }

    public void ReportDirectory(DirectoryStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _directory = status;
        }
    }

    public void ReportAttribution(AttributionStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _attribution = status;
        }
    }
}
