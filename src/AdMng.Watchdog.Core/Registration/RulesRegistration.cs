using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Core.Registration;

/// <summary>Registrations of Attribution/, Rules/ and Configuration/ (owner W-rules): AttributionEngine,
/// RuleEvaluationJob, the configuration provider/validator/defaults, rule preview and the classes they use.</summary>
internal static class RulesRegistration
{
    internal static void Add(IServiceCollection services)
    {
        // Scaffold: nothing registered yet.
    }
}
