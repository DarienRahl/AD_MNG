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
    private ServiceAccountStatusDto? _serviceAccount;
    private CollectorStatusDto? _collector;
    private GpoContentStatusDto? _gpoContent;
    private HostStatusDto? _host;

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

    public ServiceAccountStatusDto? ServiceAccount
    {
        get
        {
            lock (_gate)
            {
                return _serviceAccount;
            }
        }
    }

    public CollectorStatusDto? Collector
    {
        get
        {
            lock (_gate)
            {
                return _collector;
            }
        }
    }

    public GpoContentStatusDto? GpoContent
    {
        get
        {
            lock (_gate)
            {
                return _gpoContent;
            }
        }
    }

    public HostStatusDto? Host
    {
        get
        {
            lock (_gate)
            {
                return _host;
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

    public void ReportServiceAccount(ServiceAccountStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _serviceAccount = status;
        }
    }

    public void ReportCollector(CollectorStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _collector = status;
        }
    }

    public void ReportGpoContent(GpoContentStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _gpoContent = status;
        }
    }

    public void ReportHost(HostStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _host = status;
        }
    }
}
