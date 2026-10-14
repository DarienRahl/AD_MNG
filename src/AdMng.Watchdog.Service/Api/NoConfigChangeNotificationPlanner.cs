using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Service.Api;

/// <summary>The inert <see cref="IConfigChangeNotificationPlanner"/>: the event is evaluated with the new configuration
/// only (by the rule evaluation job).</summary>
public sealed class NoConfigChangeNotificationPlanner : IConfigChangeNotificationPlanner
{
    public Task PlanWithPreviousConfigurationAsync(WatchEvent stored, WatchdogConfiguration previous, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
