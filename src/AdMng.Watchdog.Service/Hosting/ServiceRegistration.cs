using System.Threading.RateLimiting;
using AdMng.Core.Directory;
using AdMng.Directory;
using AdMng.Watchdog.Core;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Api;
using AdMng.Watchdog.Service.Attribution;
using AdMng.Watchdog.Service.Directory;
using AdMng.Watchdog.Service.Hosting.Fallbacks;
using AdMng.Watchdog.Service.Notifications;
using AdMng.Watchdog.Service.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// The service's DI registrations (spec 19.1): bootstrap options, <c>AddWatchdogCore()</c> and the Core fallbacks, the
/// SQLite stores (one <see cref="SqliteWatchdogStore"/> behind every store interface), transports and the delivery
/// tester, directory session and Security-log source, API helpers, job hosts, start-up checks.
/// </summary>
public static class ServiceRegistration
{
    public static void Add(IServiceCollection services, IConfiguration configuration, DataDirectory dataDirectory, StartupContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(context);

        services.AddOptions<ServiceOptions>().Bind(configuration.GetSection(ServiceOptions.SectionName));
        services.Configure<HostOptions>(o =>
        {
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
            o.ShutdownTimeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton(dataDirectory);
        services.AddSingleton(context);
        services.AddSingleton<ServiceStartTime>();
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddRateLimiter(ConfigureRateLimiter);

        services.AddWatchdogCore();
        CoreFallbacks.Add(services);

        AddStorage(services, dataDirectory);
        AddDelivery(services);
        AddDirectory(services, configuration);
        AddApi(services);

        services.TryAddSingleton<IProcessTerminator, EnvironmentProcessTerminator>();
        services.AddSingleton<FatalErrorHandler>();
        services.AddSingleton<StorageGuardJob>();
        services.AddSingleton<IWatchdogJob>(provider => provider.GetRequiredService<StorageGuardJob>());

        // Start-up checks first (their StartingAsync runs before any StartAsync), then the job hosts.
        services.AddHostedService<StartupChecks>();
        services.AddSingleton<JobHostService>();
        services.AddHostedService(provider => provider.GetRequiredService<JobHostService>());
    }

    private static void AddStorage(IServiceCollection services, DataDirectory dataDirectory)
    {
        services.AddSingleton(_ => new SqliteDatabase(dataDirectory.Path));
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<SqliteWatchdogStore>();
        services.AddSingleton<IDetectionStore>(p => p.GetRequiredService<SqliteWatchdogStore>());
        services.AddSingleton<IEventStore>(p => p.GetRequiredService<SqliteWatchdogStore>());
        services.AddSingleton<INotificationQueueStore>(p => p.GetRequiredService<SqliteWatchdogStore>());
        services.AddSingleton<IOutboxStore>(p => p.GetRequiredService<SqliteWatchdogStore>());
        services.AddSingleton<ISecurityEventStore>(p => p.GetRequiredService<SqliteWatchdogStore>());
        services.AddSingleton<IExpiryNoticeStore>(p => p.GetRequiredService<SqliteWatchdogStore>());
        services.AddSingleton<IDigestRunStore>(p => p.GetRequiredService<SqliteWatchdogStore>());
        services.AddSingleton<IConfigStore>(p => p.GetRequiredService<SqliteWatchdogStore>());
        services.AddSingleton<ISecretStore>(p => p.GetRequiredService<SqliteWatchdogStore>());
        services.AddSingleton<IStoreMaintenance>(p => p.GetRequiredService<SqliteWatchdogStore>());
    }

    private static void AddDelivery(IServiceCollection services)
    {
        services.AddDeliveryHttpClient();
        services.TryAddSingleton<ICertificateStore, SystemCertificateStore>();
        services.TryAddSingleton<IEntraTokenClient, MsalEntraTokenClient>();
        services.AddSingleton<CertificateSelector>();
        services.AddSingleton<DestinationGuard>();
        services.AddSingleton<ChannelRateLimiters>();
        services.AddSingleton<ChannelHealthTracker>();
        services.AddSingleton<SmtpOAuthTokenProvider>();
        services.AddSingleton<TeamsTokenProvider>();
        services.AddSingleton<HttpChannelSender>();
        services.AddSingleton<SmtpEmailTransport>();
        services.AddSingleton<TeamsWorkflowTransport>();
        services.AddSingleton<WebhookTransport>();
        services.AddSingleton<INotificationTransport>(p => p.GetRequiredService<SmtpEmailTransport>());
        services.AddSingleton<INotificationTransport>(p => p.GetRequiredService<TeamsWorkflowTransport>());
        services.AddSingleton<INotificationTransport>(p => p.GetRequiredService<WebhookTransport>());
        services.TryAddSingleton<IDeliveryTester, DeliveryTester>();
    }

    private static void AddDirectory(IServiceCollection services, IConfiguration configuration)
    {
        var disabled = configuration.GetValue<bool>($"{ServiceOptions.SectionName}:Directory:Disabled");
        services.TryAddSingleton<IDirectoryConnector>(provider => new LdapDirectoryConnector(
            provider.GetRequiredService<ILoggerFactory>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<DirectoryConnectionOptionsFactory>();
        if (disabled)
        {
            services.TryAddSingleton<IDirectorySessionProvider, DisabledDirectorySessionProvider>();
        }
        else
        {
            services.TryAddSingleton<IDirectorySessionProvider, ServiceDirectorySessionProvider>();
        }

        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<ISecurityEventSource, WindowsSecurityEventSource>();
        }
        else
        {
            services.TryAddSingleton<ISecurityEventSource, NullSecurityEventSource>();
        }
    }

    private static void AddApi(IServiceCollection services)
    {
        services.AddSingleton<Auth.MachineAddresses>();
        services.AddSingleton<EventAccessLog>();
        services.AddSingleton<ConfigSecretsApplier>();
        services.AddSingleton<ConfigValidationService>();
        services.AddSingleton<ConfigChangeNotifier>();
        services.AddSingleton<WatchdogDomain>();
        services.TryAddSingleton<IConfigChangeNotificationPlanner, NoConfigChangeNotificationPlanner>();
        services.TryAddSingleton<IExclusionSuggestionSource, NoExclusionSuggestions>();
    }

    private static void ConfigureRateLimiter(Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var key = context.User.Identity?.IsAuthenticated == true
                ? "user:" + context.User.Identity.Name
                : "address:" + context.Connection.RemoteIpAddress;
            return RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = ApiLimits.ConcurrentRequestsPerCaller,
                QueueLimit = 16,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
        });
    }
}
