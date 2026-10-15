using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Notifications.Digest;
using AdMng.Watchdog.Core.Retention;
using AdMng.Watchdog.Core.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Watchdog.Core.Registration;

/// <summary>Registrations of Notifications/, Formatting/ and Retention/ (owner W-notify): dispatch, outbox sender,
/// digest, heartbeat and retention jobs, signals, health registry, formatters and the classes they use.</summary>
/// <remarks>
/// Everything is a singleton registered with <c>TryAdd</c>, so a registration made before <c>AddWatchdogCore()</c>
/// wins (tests, the service, adapters of other areas). The jobs are registered once as themselves and once as
/// <see cref="IWatchdogJob"/>; <see cref="DigestJob"/> is also the <see cref="IDigestService"/>. Stores, transports
/// (<see cref="INotificationTransport"/>, one per channel type) and <see cref="IDirectorySessionProvider"/> come from
/// the service. The planners are the rules area's extension points: <see cref="NotificationPlanner"/> is the
/// <see cref="IRuleMatchPlanner"/>, <see cref="FollowUpPlanner"/> the <see cref="IAttributionFollowUpPlanner"/>,
/// <see cref="BusinessCalendarProvider"/> the <see cref="IBusinessHoursCalendar"/>; held notifications are re-matched by
/// <see cref="RuleEngineHeldMatcher"/> (the rules area's <c>RuleMatcher</c>).
/// </remarks>
internal static class NotificationRegistration
{
    internal static void Add(IServiceCollection services)
    {
        services.TryAddSingleton<IWatchdogSignals, WatchdogSignals>();
        services.TryAddSingleton<IHealthRegistry, HealthRegistry>();
        services.TryAddSingleton<IEventHtmlRenderer, EventHtmlRenderer>();

        AddPlanning(services);
        AddDelivery(services);
        AddDigest(services);
        AddJob<RetentionJob>(services);
    }

    private static void AddPlanning(IServiceCollection services)
    {
        services.TryAddSingleton<BusinessCalendarProvider>();
        services.TryAddSingleton<IBusinessHoursCalendar>(provider => provider.GetRequiredService<BusinessCalendarProvider>());
        services.TryAddSingleton<RolloutGate>();
        services.TryAddSingleton<NotificationTargetResolver>();
        services.TryAddSingleton<NotificationPlanner>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRuleMatchPlanner, NotificationPlanner>(
            provider => provider.GetRequiredService<NotificationPlanner>()));
        services.TryAddSingleton<FollowUpPlanner>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAttributionFollowUpPlanner, FollowUpPlanner>(
            provider => provider.GetRequiredService<FollowUpPlanner>()));
        services.TryAddSingleton<IHeldNotificationMatcher, RuleEngineHeldMatcher>();
        services.TryAddSingleton<ThrottlePolicy>();
        AddJob<NotificationDispatchJob>(services);
    }

    private static void AddDelivery(IServiceCollection services)
    {
        services.TryAddSingleton(_ => new RetryPolicy(Random.Shared));
        services.TryAddSingleton<ChannelHealthTracker>();
        services.TryAddSingleton<DeliveryFailureReporter>();
        AddJob<OutboxSenderJob>(services);
        AddJob<HeartbeatJob>(services);
    }

    private static void AddDigest(IServiceCollection services)
    {
        services.TryAddSingleton<IDigestDirectoryReports, LiveDirectoryReports>();
        services.TryAddSingleton<DigestBuilder>();
        AddJob<DigestJob>(services);
        services.TryAddSingleton<IDigestService>(provider => provider.GetRequiredService<DigestJob>());
    }

    private static void AddJob<TJob>(IServiceCollection services)
        where TJob : class, IWatchdogJob
    {
        if (services.Any(d => d.ServiceType == typeof(TJob)))
        {
            return;
        }

        services.AddSingleton<TJob>();
        services.AddSingleton<IWatchdogJob>(provider => provider.GetRequiredService<TJob>());
    }
}
