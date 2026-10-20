using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Tests.Rules.Fakes;

/// <summary>In-memory <see cref="IHealthRegistry"/> for the rules and attribution tests (the real one is W-notify's):
/// keeps the last report per check code / channel id.</summary>
internal sealed class FakeHealthRegistry : IHealthRegistry
{
    private readonly Dictionary<string, HealthCheckDto> _checks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChannelHealthDto> _channels = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

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

    public List<AttributionStatusDto> AttributionReports { get; } = [];

    public HealthCheckDto? Find(string code)
    {
        lock (_gate)
        {
            return _checks.GetValueOrDefault(code);
        }
    }

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

    public ServiceAccountStatusDto? ServiceAccount { get; private set; }

    public CollectorStatusDto? Collector { get; private set; }

    public GpoContentStatusDto? GpoContent { get; private set; }

    public HostStatusDto? Host { get; private set; }

    public void ReportServiceAccount(ServiceAccountStatusDto status) => ServiceAccount = status;

    public void ReportCollector(CollectorStatusDto status) => Collector = status;

    public void ReportGpoContent(GpoContentStatusDto status) => GpoContent = status;

    public void ReportHost(HostStatusDto status) => Host = status;

    public void ReportAttribution(AttributionStatusDto status)
    {
        lock (_gate)
        {
            Attribution = status;
            AttributionReports.Add(status);
        }
    }
}
