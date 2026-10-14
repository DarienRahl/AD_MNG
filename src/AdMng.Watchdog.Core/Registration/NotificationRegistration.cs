using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Core.Registration;

/// <summary>Registrations of Notifications/, Formatting/ and Retention/ (owner W-notify): dispatch, outbox sender,
/// digest, heartbeat and retention jobs, signals, health registry, formatters and the classes they use.</summary>
internal static class NotificationRegistration
{
    internal static void Add(IServiceCollection services)
    {
        // Scaffold: nothing registered yet.
    }
}
