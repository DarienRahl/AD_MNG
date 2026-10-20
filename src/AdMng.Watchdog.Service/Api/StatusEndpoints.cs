using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Storage;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Api;

/// <summary><c>/healthz</c>, <c>/api/v1/me</c>, <c>/api/v1/status</c>, <c>/api/v1/catalog</c> (spec 17.2).</summary>
public static class StatusEndpoints
{
    public static void Map(IEndpointRouteBuilder api, IEndpointRouteBuilder root)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(root);
        root.MapGet(WatchdogRoutes.Health, Health).AllowAnonymous();
        api.MapGet(WatchdogRoutes.Me, Me);
        api.MapGet(WatchdogRoutes.Status, StatusAsync);
        api.MapGet(WatchdogRoutes.Catalog, () => ApiResults.Json(EventKindCatalog.All));
    }

    /// <summary>Liveness for monitoring: "ok" when the database answers, 503 otherwise. No data.</summary>
    private static IResult Health(SqliteWatchdogStore store)
    {
        try
        {
            store.Maintenance.UsedBytes();
            return Results.Text("ok", "text/plain");
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException)
        {
            return Results.Text("unavailable", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult Me(HttpContext context, IOptionsMonitor<ServiceOptions> options)
    {
        var settings = options.CurrentValue;
        var user = context.User;
        return ApiResults.Json(new MeDto
        {
            Account = CallerIdentity.Account(user),
            Sid = CallerIdentity.Sid(user),
            AuthenticationType = user.Identity?.AuthenticationType ?? "none",
            Roles = SidRoles.Evaluate(user, settings.Authorization),
            DetailLevel = CallerIdentity.DetailLevel(user, settings),
        });
    }

    private static async Task<IResult> StatusAsync(
        HttpContext context,
        IWatchdogConfigProvider configuration,
        IHealthRegistry health,
        IEventStore events,
        IOutboxStore outbox,
        IStoreMaintenance maintenance,
        IExclusionSuggestionSource suggestions,
        IDigestService digest,
        RolloutGate rollout,
        WatchdogDomain domain,
        ServiceStartTime started,
        TimeProvider timeProvider,
        IOptionsMonitor<ServiceOptions> options,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var config = configuration.Current.Configuration;
        var pending = await outbox.CountPendingByChannelAsync(cancellationToken).ConfigureAwait(false);
        var directory = health.Directory;
        var watchedDomain = await domain.GetAsync(cancellationToken).ConfigureAwait(false);
        var isAdmin = CallerIdentity.IsAdmin(context.User, options.CurrentValue);
        return ApiResults.Json(new StatusDto
        {
            ServiceVersion = ServiceVersion.Current,
            InstanceName = config.General.InstanceName,
            Domain = watchedDomain,
            StartedAt = started.StartedAt,
            Now = now,
            Directory = directory,
            Attribution = health.Attribution,
            Checks = health.Checks,
            Channels = [.. health.Channels.Select(c => c with { Pending = pending.GetValueOrDefault(c.ChannelId) })],
            Storage = await StorageAsync(maintenance, isAdmin, cancellationToken).ConfigureAwait(false),
            Last24Hours = await events.CountAsync(now.AddHours(-24), cancellationToken).ConfigureAwait(false),
            NextDigestAt = digest.NextScheduledAt,
            RolloutModeUntil = await rollout.ActiveUntilAsync(config, watchedDomain, now, cancellationToken).ConfigureAwait(false),
            Suggestions = suggestions.Current,
            ServiceAccount = health.ServiceAccount,
            Collector = health.Collector,
            GpoContent = health.GpoContent,
            Host = health.Host,
        });
    }

    private static async Task<StorageStatusDto> StorageAsync(IStoreMaintenance maintenance, bool isAdmin, CancellationToken cancellationToken)
    {
        var storage = await maintenance.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return isAdmin ? storage : storage with { DataDirectory = string.Empty };   // local paths only for Admins
    }

}
