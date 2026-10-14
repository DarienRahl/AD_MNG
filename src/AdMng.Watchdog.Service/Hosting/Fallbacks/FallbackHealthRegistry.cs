using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Hosting.Fallbacks;

/// <summary><see cref="IHealthRegistry"/> used only when the Core registration provides none: the last report per
/// check code and per channel, in memory.</summary>
public sealed class FallbackHealthRegistry : IHealthRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HealthCheckDto> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChannelHealthDto> _channels = new(StringComparer.OrdinalIgnoreCase);
    private DirectoryStatusDto _directory = new() { State = HealthState.Unknown };
    private AttributionStatusDto _attribution = new();

    public IReadOnlyList<HealthCheckDto> Checks
    {
        get
        {
            lock (_gate)
            {
                return [.. _checks.Values.OrderBy(c => c.Code, StringComparer.Ordinal)];
            }
        }
    }

    public IReadOnlyList<ChannelHealthDto> Channels
    {
        get
        {
            lock (_gate)
            {
                return [.. _channels.Values.OrderBy(c => c.ChannelId, StringComparer.Ordinal)];
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

    public void Report(HealthCheckDto check)
    {
        ArgumentNullException.ThrowIfNull(check);
        lock (_gate)
        {
            _checks[check.Code] = check;
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
