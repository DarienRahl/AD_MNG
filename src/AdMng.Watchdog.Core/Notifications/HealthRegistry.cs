using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// <see cref="IHealthRegistry"/>: the in-memory health state of the service (status endpoint, digest header, health
/// events). Checks are kept per <see cref="HealthCheckDto.Code"/> and channels per
/// <see cref="ChannelHealthDto.ChannelId"/> (both case-insensitive, the latest report wins); lists are ordered by code /
/// channel id. Thread-safe.
/// </summary>
public sealed class HealthRegistry : IHealthRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HealthCheckDto> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChannelHealthDto> _channels = new(StringComparer.OrdinalIgnoreCase);
    private DirectoryStatusDto _directory = new() { State = HealthState.Unknown };
    private AttributionStatusDto _attribution = new();
    private ServiceAccountStatusDto? _serviceAccount;
    private CollectorStatusDto? _collector;
    private GpoContentStatusDto? _gpoContent;
    private HostStatusDto? _host;

    /// <summary>Raised after every report (outside the lock); the argument is the code or channel id.</summary>
    public event EventHandler<string>? Changed;

    /// <inheritdoc/>
    public IReadOnlyList<HealthCheckDto> Checks
    {
        get
        {
            lock (_gate)
            {
                return [.. _checks.Values.OrderBy(c => c.Code, StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ChannelHealthDto> Channels
    {
        get
        {
            lock (_gate)
            {
                return [.. _channels.Values.OrderBy(c => c.ChannelId, StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public void Report(HealthCheckDto check)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentException.ThrowIfNullOrWhiteSpace(check.Code);
        lock (_gate)
        {
            _checks[check.Code] = check;
        }

        Changed?.Invoke(this, check.Code);
    }

    /// <inheritdoc/>
    public void ReportChannel(ChannelHealthDto channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel.ChannelId);
        lock (_gate)
        {
            _channels[channel.ChannelId] = channel;
        }

        Changed?.Invoke(this, channel.ChannelId);
    }

    /// <inheritdoc/>
    public void ReportDirectory(DirectoryStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _directory = status;
        }

        Changed?.Invoke(this, "directory");
    }

    /// <inheritdoc/>
    public void ReportAttribution(AttributionStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _attribution = status;
        }

        Changed?.Invoke(this, "attribution");
    }

    /// <inheritdoc/>
    public void ReportServiceAccount(ServiceAccountStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _serviceAccount = status;
        }

        Changed?.Invoke(this, "serviceAccount");
    }

    /// <inheritdoc/>
    public void ReportCollector(CollectorStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _collector = status;
        }

        Changed?.Invoke(this, "collector");
    }

    /// <inheritdoc/>
    public void ReportGpoContent(GpoContentStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _gpoContent = status;
        }

        Changed?.Invoke(this, "gpoContent");
    }

    /// <inheritdoc/>
    public void ReportHost(HostStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _host = status;
        }

        Changed?.Invoke(this, "host");
    }

    /// <summary>The latest report of one check, or null.</summary>
    public HealthCheckDto? GetCheck(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        lock (_gate)
        {
            return _checks.GetValueOrDefault(code);
        }
    }

    /// <summary>The latest health of one channel, or null when nothing was reported for it.</summary>
    public ChannelHealthDto? GetChannel(string channelId)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        lock (_gate)
        {
            return _channels.GetValueOrDefault(channelId);
        }
    }
}
