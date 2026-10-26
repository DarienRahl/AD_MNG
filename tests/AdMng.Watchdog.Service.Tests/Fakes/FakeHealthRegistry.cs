using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>Keeps the last report per check code / channel.</summary>
public sealed class FakeHealthRegistry : IHealthRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HealthCheckDto> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChannelHealthDto> _channels = new(StringComparer.OrdinalIgnoreCase);

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

    public DirectoryStatusDto Directory { get; private set; } = new() { State = HealthState.Unknown };

    public AttributionStatusDto Attribution { get; private set; } = new();

    public void Report(HealthCheckDto check)
    {
        lock (_gate)
        {
            _checks[check.Code] = check;
        }
    }

    public void ReportChannel(ChannelHealthDto channel)
    {
        lock (_gate)
        {
            _channels[channel.ChannelId] = channel;
        }
    }

    public void ReportDirectory(DirectoryStatusDto status) => Directory = status;

    public void ReportAttribution(AttributionStatusDto status) => Attribution = status;

    public ServiceAccountStatusDto? ServiceAccount { get; private set; }

    public CollectorStatusDto? Collector { get; private set; }

    public GpoContentStatusDto? GpoContent { get; private set; }

    public HostStatusDto? Host { get; private set; }

    public void ReportServiceAccount(ServiceAccountStatusDto status) => ServiceAccount = status;

    public void ReportCollector(CollectorStatusDto status) => Collector = status;

    public void ReportGpoContent(GpoContentStatusDto status) => GpoContent = status;

    public void ReportHost(HostStatusDto status) => Host = status;

    public ChannelHealthDto? Channel(string id)
    {
        lock (_gate)
        {
            return _channels.GetValueOrDefault(id);
        }
    }
}
