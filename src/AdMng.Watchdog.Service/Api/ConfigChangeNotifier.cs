using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// Records a configuration save as a <see cref="WatchEventKind.WatchdogConfigurationChanged"/> health event (spec 17.2
/// step 5): created by <see cref="ConfigChangeAuditor"/> (changed paths only, attribution Resolved = the caller), appended,
/// planned with the previous configuration (<see cref="IConfigChangeNotificationPlanner"/>), the pipeline woken up;
/// Application-log event 1006 with the version and the caller account.
/// </summary>
public sealed partial class ConfigChangeNotifier(
    ConfigChangeAuditor auditor,
    IEventStore events,
    IConfigChangeNotificationPlanner planner,
    IWatchdogSignals signals,
    ILogger<ConfigChangeNotifier> logger)
{
    public async Task<long> NotifyAsync(
        ConfigChangeAudit audit,
        PrincipalRef changedBy,
        long version,
        string? domain,
        string? sourceIpAddress,
        WatchdogConfiguration previous,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(changedBy);
        ArgumentNullException.ThrowIfNull(previous);
        var watchEvent = auditor.CreateEvent(audit, changedBy, domain, sourceIpAddress);
        var id = await events.AppendAsync(watchEvent, cancellationToken).ConfigureAwait(false);
        await planner.PlanWithPreviousConfigurationAsync(watchEvent with { Id = id }, previous, cancellationToken).ConfigureAwait(false);
        signals.Raise(WatchdogSignal.EventsStored);
        Log.ConfigurationChanged(logger, version, changedBy.Account ?? "unknown");
        return id;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = EventLogIds.ConfigurationChanged, Level = LogLevel.Information, Message = "Configuration version {Version} saved by {Account}.")]
        public static partial void ConfigurationChanged(ILogger logger, long version, string account);
    }
}
