using System.Threading.RateLimiting;
using AdMng.Core.Directory;
using AdMng.Directory;
using AdMng.Watchdog.Core;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Service.Api;
using AdMng.Watchdog.Service.Attribution;
using AdMng.Watchdog.Service.Directory;
using AdMng.Watchdog.Service.Identity;
using AdMng.Watchdog.Service.Notifications;
using AdMng.Watchdog.Service.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// The service's DI registrations (spec 19.1): bootstrap options, the SQLite stores (one <see cref="SqliteWatchdogStore"/>
/// behind every store interface), transports and the delivery tester, directory session and Security-log source, the
/// detection host options, API helpers – all registered BEFORE <c>AddWatchdogCore()</c>, whose TryAdd registrations then
/// only fill what the host leaves open – followed by the job hosts and start-up checks.
/// </summary>
public static class ServiceRegistration
{
    public static void Add(IServiceCollection services, IConfiguration configuration, DataDirectory dataDirectory, StartupContext context) =>
        Add(
            services,
            configuration,
            dataDirectory,
            context,
            configuration?.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>() ?? new ServiceOptions());

    /// <summary>The registrations; <paramref name="startupOptions"/> are the bootstrap settings the host was built with.</summary>
    public static void Add(IServiceCollection services, IConfiguration configuration, DataDirectory dataDirectory, StartupContext context, ServiceOptions startupOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(startupOptions);

        AddServiceOptions(services, configuration, startupOptions);
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

        AddStorage(services, dataDirectory);
        AddDelivery(services);
        AddDirectory(services, configuration);
        AddApi(services);
        AddWatchdogAccount(services, startupOptions);
        AddCollector(services, startupOptions);
        services.AddWatchdogCore();

        services.TryAddSingleton<IProcessTerminator, EnvironmentProcessTerminator>();
        services.AddSingleton<FatalErrorHandler>();
        services.AddSingleton<StorageGuardJob>();
        services.AddSingleton<IWatchdogJob>(provider => provider.GetRequiredService<StorageGuardJob>());

        // Linux: self-checks of the own computer account and its keytab (stage B design 4.7), hourly.
        if (OperatingSystem.IsLinux() && !startupOptions.Directory.Disabled && !string.IsNullOrWhiteSpace(startupOptions.Kerberos.ServicePrincipal))
        {
            services.TryAddSingleton(_ => ServiceAccountPaths.FromEnvironment());
            services.AddSingleton<ServiceAccountMonitor>();
            services.AddSingleton<IWatchdogJob>(provider => provider.GetRequiredService<ServiceAccountMonitor>());
        }

        // Start-up checks first (their StartingAsync runs before any StartAsync), then the job hosts.
        services.AddHostedService<StartupChecks>();
        services.AddSingleton<JobHostService>();
        services.AddHostedService(provider => provider.GetRequiredService<JobHostService>());
    }

    /// <summary>
    /// <see cref="ServiceOptions"/> bound to the (watched) "Watchdog" section; <see cref="ServiceOptionsFactory"/> keeps the
    /// settings the host was built with (listeners, server, Kerberos, certificate, data folder, directory, authentication)
    /// at <paramref name="startupOptions"/>, so <c>IOptionsMonitor</c> consumers see live changes only where they are safe.
    /// </summary>
    public static void AddServiceOptions(IServiceCollection services, IConfiguration configuration, ServiceOptions startupOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(startupOptions);
        services.AddOptions<ServiceOptions>().Bind(configuration.GetSection(ServiceOptions.SectionName));
        services.AddSingleton(new StartupServiceOptions(startupOptions));
        services.AddSingleton<IOptionsFactory<ServiceOptions>, ServiceOptionsFactory>();
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

        // The watchdog's own role and host groups are High-tier privileged roots (spec 10.8 step 1).
        services.TryAddSingleton(provider => DetectionHostOptionsFrom(provider.GetRequiredService<IOptions<ServiceOptions>>().Value));
    }

    /// <summary>
    /// The own computer account for the detection (stage B design 9.3: tag watchdogAccount, rule watchdog-self) whenever
    /// the bootstrap file names a Kerberos principal and the directory is watched – registered before
    /// <c>AddWatchdogCore()</c>, whose default knows no account.
    /// </summary>
    private static void AddWatchdogAccount(IServiceCollection services, ServiceOptions startupOptions)
    {
        if (startupOptions.Directory.Disabled || string.IsNullOrWhiteSpace(startupOptions.Kerberos.ServicePrincipal))
        {
            return;
        }

        services.TryAddSingleton<WatchdogAccountSource>();
        services.TryAddSingleton<IWatchdogAccountSource>(provider => provider.GetRequiredService<WatchdogAccountSource>());
    }

    /// <summary>
    /// The WEF tailer of the Linux service (stage B design 7.5) whenever the directory is watched: the job, the collector
    /// availability for the attribution engine (registered before <c>AddWatchdogCore()</c>, whose default has no
    /// collector) and the DC facts of the identity checks (7.6).
    /// </summary>
    private static void AddCollector(IServiceCollection services, ServiceOptions startupOptions)
    {
        if (!OperatingSystem.IsLinux() || startupOptions.Directory.Disabled || startupOptions.Collector.Kind != CollectorKind.OpenWec)
        {
            return;
        }

        AddLinuxCollector(services);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void AddLinuxCollector(IServiceCollection services)
    {
        services.TryAddSingleton<IHostAddressResolver, SystemHostAddressResolver>();
        services.TryAddSingleton<ICollectorDomainControllerSource, DirectoryCollectorDcSource>();
        services.TryAddSingleton<WecFileIngestionJob>();
        services.AddSingleton<IWatchdogJob>(provider => provider.GetRequiredService<WecFileIngestionJob>());
        services.TryAddSingleton<ISecurityEventCollector>(provider => provider.GetRequiredService<WecFileIngestionJob>());
    }

    /// <summary>The detection inputs of the bootstrap file: the Viewer, Admin and (Windows only – obsolete on Linux, stage B
    /// design 6.2) watchdog host group SIDs; on Linux the permission self-check also probes tokenGroups (6.2 step 3).</summary>
    public static DetectionHostOptions DetectionHostOptionsFrom(ServiceOptions options) => DetectionHostOptionsFrom(options, OperatingSystem.IsLinux());

    /// <summary><see cref="DetectionHostOptionsFrom(ServiceOptions)"/> for the given platform.</summary>
    public static DetectionHostOptions DetectionHostOptionsFrom(ServiceOptions options, bool linux)
    {
        ArgumentNullException.ThrowIfNull(options);
        var authorization = options.Authorization ?? new AuthorizationOptions();
        var hostGroups = linux ? [] : authorization.WatchdogHostGroupSids ?? [];
        return new DetectionHostOptions
        {
            ProbeTokenGroups = linux && options.Authentication == WatchdogAuthenticationMode.Negotiate,
            RoleGroupSids = [.. (authorization.ViewerSids ?? []).Concat(authorization.AdminSids ?? []).Concat(hostGroups)
                .Where(sid => !string.IsNullOrWhiteSpace(sid)).Select(sid => sid.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
    }

    private static void AddApi(IServiceCollection services)
    {
        services.AddSingleton<Auth.MachineAddresses>();
        services.AddSingleton<EventAccessLog>();
        services.AddSingleton<ConfigSecretsApplier>();
        services.AddSingleton<ConfigValidationService>();
        services.AddSingleton<ConfigChangeNotifier>();
        services.AddSingleton<WatchdogDomain>();
        services.TryAddSingleton<IConfigChangeNotificationPlanner, RuleEngineConfigChangePlanner>();
        services.TryAddSingleton<IExclusionSuggestionSource, DetectionExclusionSuggestions>();
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
