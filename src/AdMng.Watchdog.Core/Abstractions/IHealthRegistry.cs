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

    // Stage B Status sections (docs/dev/watchdog-linux-design.md 18 item 2): null until the first report.

    /// <summary>„Konto usługi” (4.7).</summary>
    ServiceAccountStatusDto? ServiceAccount { get; }
    void ReportServiceAccount(ServiceAccountStatusDto status);

    /// <summary>„Kolektor WEF” (7.9).</summary>
    CollectorStatusDto? Collector { get; }
    void ReportCollector(CollectorStatusDto status);

    /// <summary>„Zawartość GPO” (8).</summary>
    GpoContentStatusDto? GpoContent { get; }
    void ReportGpoContent(GpoContentStatusDto status);

    /// <summary>„Host” (10, 4.7 item 6, 5.1).</summary>
    HostStatusDto? Host { get; }
    void ReportHost(HostStatusDto status);
}
