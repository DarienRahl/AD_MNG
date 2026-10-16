using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>In-memory health state of the service (status endpoint, health events).</summary>
public interface IHealthRegistry
{
    void Report(HealthCheckDto check);
    void ReportChannel(ChannelHealthDto channel);
    IReadOnlyList<HealthCheckDto> Checks { get; }
    IReadOnlyList<ChannelHealthDto> Channels { get; }
    DirectoryStatusDto Directory { get; }
    void ReportDirectory(DirectoryStatusDto status);
    AttributionStatusDto Attribution { get; }
    void ReportAttribution(AttributionStatusDto status);
}
