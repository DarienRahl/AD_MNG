using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Core.Registration;

/// <summary>Registrations of Detection/ and Scans/ (owner W-detect): ChangeDetectionEngine, ScheduledScanJob and the
/// classes they use.</summary>
internal static class DetectionRegistration
{
    internal static void Add(IServiceCollection services)
    {
        // Scaffold: nothing registered yet.
    }
}
