using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Storage;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Everything that must hold before the service serves a request or runs a job (spec 19.1) – runs in
/// <see cref="IHostedLifecycleService.StartingAsync"/>, i.e. before any hosted service (Kestrel, job hosts) starts:
/// bootstrap file guard (exit 6), data-folder ACL and key protection (exit 5), migrations (exit 3), listeners and the
/// TLS certificate (exit 4), Development-authentication guard (exit 7), readable secrets, the stored runtime
/// configuration loaded into <see cref="IWatchdogConfigProvider"/> (exit 3 when unreadable), runtime version. A failure is
/// logged as Application-log event 1010 and thrown as <see cref="StartupCheckException"/>.
/// </summary>
public sealed partial class StartupChecks : IHostedLifecycleService
{
    private readonly IServiceProvider _services;
    private readonly ServiceOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly StartupContext _context;
    private readonly DataDirectory _dataDirectory;
    private readonly IHealthRegistry _health;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StartupChecks> _logger;

    public StartupChecks(
        IServiceProvider services,
        IOptions<ServiceOptions> options,
        IHostEnvironment environment,
        StartupContext context,
        DataDirectory dataDirectory,
        IHealthRegistry health,
        TimeProvider timeProvider,
        ILogger<StartupChecks> logger)
    {
        _services = services;
        _options = options.Value;
        _environment = environment;
        _context = context;
        _dataDirectory = dataDirectory;
        _health = health;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            CheckBootstrapFile();
            CheckDataDirectory();
            RunMigrations();
            CheckListeners();
            CheckDevelopmentAuthentication();
            await CheckSecretsAsync(cancellationToken).ConfigureAwait(false);
            await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
            CheckRuntime(Environment.Version);
        }
        catch (StartupCheckException failure)
        {
            Log.StartupFailed(_logger, failure.ExitCode, failure.Message);
            throw;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var instance = _services.GetService<IWatchdogConfigProvider>()?.Current.Configuration.General.InstanceName ?? "AD Watchdog";
        Log.Started(_logger, ServiceVersion.Current, instance);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Reports the runtime check (public for tests).</summary>
    public void CheckRuntime(Version runtime)
    {
        if (!RuntimeVersionCheck.IsOutdated(runtime))
        {
            return;
        }

        var version = runtime.ToString(3);
        Log.RuntimeOutdated(_logger, version, RuntimeVersionCheck.Minimum.ToString(3));
        Report(RuntimeVersionCheck.HealthCode, HealthState.Warning, "The ASP.NET Core runtime is older than the supported minimum.", version);
    }

    private void CheckBootstrapFile()
    {
        if (!_context.IsWindowsService)
        {
            return;
        }

        if (_context.ExtraConfigFile is not null)
        {
            throw new StartupCheckException(StartupExitCodes.BootstrapFile, "ADMNG_WATCHDOG_CONFIG is not allowed for the Windows service.");
        }

        if (OperatingSystem.IsWindows() && _context.BootstrapFile is { } file && BootstrapFileGuard.IsWritableByCurrentIdentity(file))
        {
            throw new StartupCheckException(
                StartupExitCodes.BootstrapFile,
                "The service account can write its bootstrap configuration (config\\appsettings.json); grant it Read only.");
        }
    }

    private void CheckDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var problems = DataDirectoryAcl.Verify(_dataDirectory.ProtectedPaths());
            if (problems.Count > 0)
            {
                if (!_options.AllowInsecureDataDirectoryAcl)
                {
                    Log.AclProblem(_logger, problems.Count);
                    throw new StartupCheckException(
                        StartupExitCodes.DataDirectory,
                        "The data folder ACL allows accounts outside SYSTEM, Administrators and the service account (see the install guide).");
                }

                Report("storage.acl", HealthState.Warning, "The data folder ACL is outside the allow-list (AllowInsecureDataDirectoryAcl).", problems.Count.ToString(CultureInfo.InvariantCulture));
            }

            return;
        }

        // Linux/macOS (lab and development only): the Data Protection key ring is not encrypted at rest.
        if (_environment.IsProduction())
        {
            throw new StartupCheckException(StartupExitCodes.DataDirectory, "Unprotected secret keys are refused in Production; run the service on Windows.");
        }

        Log.KeysUnprotected(_logger);
    }

    private void RunMigrations()
    {
        try
        {
            _services.GetRequiredService<MigrationRunner>().Run();
        }
        catch (DatabaseVersionTooNewException)
        {
            throw new StartupCheckException(StartupExitCodes.Migration, "The database was created by a newer version of the service; install that version or restore a backup.");
        }
        catch (Exception ex) when (ex is not StartupCheckException and not OutOfMemoryException)
        {
            SafeExceptionLog.LogError(_logger, ex, "database migration", new EventId(EventLogIds.StorageProblem));
            throw new StartupCheckException(StartupExitCodes.Migration, "The database could not be opened or migrated.");
        }
    }

    /// <summary>Loads the stored runtime configuration before any job runs (otherwise the jobs would start with the
    /// defaults – SMTP off, no recipients – until the next save). An unreadable document stops the start: running with
    /// the defaults would silently mute every notification.</summary>
    private async Task LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _services.GetRequiredService<IWatchdogConfigProvider>().ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            SafeExceptionLog.LogError(_logger, ex, "configuration load", new EventId(EventLogIds.StorageProblem));
            throw new StartupCheckException(StartupExitCodes.Migration, "The stored configuration could not be read.");
        }
    }

    private void CheckListeners()
    {
        if (_context.EndpointError is { } error)
        {
            throw new StartupCheckException(StartupExitCodes.Certificate, error);
        }

        // For every server: HTTP.sys would otherwise serve the API and the event pages in clear text to the network.
        if (_context.Endpoints.Any(e => !e.Https && !e.IsLoopback))
        {
            throw new StartupCheckException(StartupExitCodes.Certificate, "Plain http is allowed only on loopback listeners (Hosting.Urls).");
        }

        // The certificate checks apply to Kestrel only (HTTP.sys uses the netsh sslcert binding).
        if (!IsKestrel() || !_context.Endpoints.Any(e => e.Https))
        {
            return;
        }

        var certificate = _services.GetRequiredService<CertificateSelector>().Reload();
        if (certificate is null)
        {
            throw new StartupCheckException(StartupExitCodes.Certificate, "No valid TLS certificate matches Hosting.Certificate (LocalMachine\\My, serverAuth, private key).");
        }

        if (!CertificateSelector.CanUsePrivateKey(certificate))
        {
            throw new StartupCheckException(
                StartupExitCodes.Certificate,
                "Grant the service account Read on the private key of the TLS certificate (certlm.msc → Manage Private Keys).");
        }

        // The 30-day expiry warning is (re-)evaluated by CertificateSelector.Reload on every reload.
    }

    private void CheckDevelopmentAuthentication()
    {
        if (DevelopmentAuthGuard.Check(_options, _environment.EnvironmentName, _context) is { } reason)
        {
            throw new StartupCheckException(StartupExitCodes.DevelopmentAuthentication, reason);
        }
    }

    private async Task CheckSecretsAsync(CancellationToken cancellationToken)
    {
        var secrets = _services.GetRequiredService<ISecretStore>();
        var unreadable = new List<string>();
        foreach (var name in (await secrets.ListAsync(cancellationToken).ConfigureAwait(false)).Keys)
        {
            try
            {
                await secrets.GetAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (SecretUnreadableException)
            {
                unreadable.Add(name);
            }
        }

        if (unreadable.Count > 0)
        {
            Log.SecretsUnreadable(_logger, unreadable.Count);
            Report(SecretUnreadableException.HealthCode, HealthState.Error, "Stored secrets cannot be decrypted (key ring or service account changed); enter them again.", [.. unreadable]);
        }
    }

    private bool IsKestrel()
    {
        if (_context.Server != WatchdogServer.Kestrel)
        {
            return false;
        }

        var server = _services.GetService<IServer>();
        return server?.GetType().Namespace?.StartsWith("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal) == true;
    }

    private void Report(string code, HealthState state, string detail, params string[] args) =>
        _health.Report(new HealthCheckDto { Code = code, State = state, Detail = detail, Args = args, CheckedAt = _timeProvider.GetUtcNow() });

    private static partial class Log
    {
        [LoggerMessage(EventId = EventLogIds.FatalStartupCheck, Level = LogLevel.Critical, Message = "Start-up check failed (exit code {ExitCode}): {Reason}")]
        public static partial void StartupFailed(ILogger logger, int exitCode, string reason);

        [LoggerMessage(EventId = EventLogIds.ServiceStarted, Level = LogLevel.Information, Message = "AdMng Watchdog {Version} started (instance {Instance}).")]
        public static partial void Started(ILogger logger, string version, string instance);

        [LoggerMessage(EventId = EventLogIds.StorageProblem, Level = LogLevel.Error, Message = "The data folder ACL has {Count} entries outside the allow-list.")]
        public static partial void AclProblem(ILogger logger, int count);

        [LoggerMessage(EventId = EventLogIds.RuntimeOrCertificate, Level = LogLevel.Warning, Message = "ASP.NET Core runtime {Version} is older than {Minimum}; install the current servicing update.")]
        public static partial void RuntimeOutdated(ILogger logger, string version, string minimum);

        [LoggerMessage(EventId = EventLogIds.StorageProblem, Level = LogLevel.Error, Message = "{Count} stored secret(s) cannot be decrypted; re-enter them in the configuration.")]
        public static partial void SecretsUnreadable(ILogger logger, int count);

        [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "The secret key ring is not encrypted at rest on this platform (lab/development only).")]
        public static partial void KeysUnprotected(ILogger logger);
    }
}
