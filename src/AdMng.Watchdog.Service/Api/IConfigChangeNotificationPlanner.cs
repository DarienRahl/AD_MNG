using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// Plans the notifications of a stored <see cref="WatchEventKind.WatchdogConfigurationChanged"/> event with the PREVIOUS
/// configuration (spec 17.2 step 5), so recipients that the save removed still learn about it; the event stays
/// unevaluated so the rule evaluation job plans it with the new configuration as well. Implemented by the rules area;
/// <see cref="NoConfigChangeNotificationPlanner"/> is registered when it does not provide one.
/// </summary>
public interface IConfigChangeNotificationPlanner
{
    Task PlanWithPreviousConfigurationAsync(WatchEvent stored, WatchdogConfiguration previous, CancellationToken cancellationToken);
}
