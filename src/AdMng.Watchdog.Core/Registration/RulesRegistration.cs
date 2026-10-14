using AdMng.Watchdog.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Watchdog.Core.Registration;

/// <summary>Registrations of Attribution/, Rules/ and Configuration/ (owner W-rules): AttributionEngine,
/// RuleEvaluationJob, the configuration provider/validator/defaults, rule preview and the classes they use.</summary>
internal static class RulesRegistration
{
    internal static void Add(IServiceCollection services)
    {
        // Shared Core classes implemented before the split (spec 25.6); the static helpers (SecretBindings,
        // TimeZoneResolver) need no registration.
        services.TryAddSingleton<ConfigRedactor>();
        services.TryAddSingleton<ConfigChangeAuditor>();
    }
}
