using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Storage;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Everything that must hold before the service serves a request or runs a job (spec 19.1, stage B design 11) – runs in
/// <see cref="IHostedLifecycleService.StartingAsync"/>, i.e. before any hosted service (Kestrel, job hosts) starts, and so
/// before systemd gets <c>READY=1</c>: bootstrap file guard (exit 6), placeholder role SIDs (exit 2), data-folder ACL or
/// POSIX owner/mode (exit 5) and key protection (Linux: the systemd credential, exit 9), keytab (Linux unit, exit 10),
/// migrations (exit 3), listeners and the TLS certificate (exit 4), Development-authentication guard (exit 7), readable
/// secrets, the stored runtime configuration loaded into <see cref="IWatchdogConfigProvider"/> (exit 3 when unreadable),
/// runtime version. A failure is logged as event 1010 (Application log / journald) and thrown as
/// <see cref="StartupCheckException"/>.
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
            CheckBootstrapSettings();
            CheckLdapConfiguration();
            CheckDataDirectory();
            CheckLinuxHost();
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
        if (!_context.IsManagedService)
        {
            return;
        }

        if (_context.ExtraConfigFile is not null)
        {
            throw new StartupCheckException(StartupExitCodes.BootstrapFile, "ADMNG_WATCHDOG_CONFIG is not allowed for the Windows service or the systemd unit.");
        }

        if (OperatingSystem.IsWindows() && _context.BootstrapFile is { } file && BootstrapFileGuard.IsWritableByCurrentIdentity(file))
        {
            throw new StartupCheckException(
                StartupExitCodes.BootstrapFile,
                "The service account can write its bootstrap configuration (config\\appsettings.json); grant it Read only.");
        }

        if (OperatingSystem.IsLinux() && _context.BootstrapFile is { } linuxFile
            && PosixPermissionCheck.CheckBootstrapFile(linuxFile) is { Count: > 0 } problems)
        {
            throw new StartupCheckException(
                StartupExitCodes.BootstrapFile,
                "The bootstrap file or its folder is not owned by root or is writable by the service user (" + string.Join(", ", problems)
                + "); expected /etc/admng-watchdog root:root 0755 and appsettings.json root:admng-watchdog 0640.");
        }
    }

    /// <summary>The shipped placeholders of the role groups (stage B design 14.1) stop the start like <c>--check-config</c>.</summary>
    private void CheckBootstrapSettings()
    {
        if (ServiceOptionsValidator.HasPlaceholderSids(_options))
        {
            throw new StartupCheckException(StartupExitCodes.InvalidSettings, ServiceOptionsValidator.PlaceholderSidMessage);
        }

        // Stage B design 6.2: the gMSA host group is obsolete on Linux (ignored by the detection, see ServiceRegistration).
        if (OperatingSystem.IsLinux() && (_options.Authorization.WatchdogHostGroupSids?.Length ?? 0) > 0)
        {
            Log.ObsoleteSetting(_logger, ServiceOptionsValidator.ObsoleteHostGroupMessage);
        }
    }

    /// <summary>Design 5.1/5.2 and 11 item 5: Kerberos LDAP on Linux needs an <c>LDAPCONF</c> that enforces sealing (or no
    /// SASL layer over LDAPS) – exit 2 otherwise. Applies to the systemd unit and to Production runs.</summary>
    private void CheckLdapConfiguration()
    {
        var directory = _options.Directory;
        if (!OperatingSystem.IsLinux() || directory.Disabled || directory.Authentication != AdMng.Core.Directory.AuthenticationMode.CurrentUser
            || !(_context.IsSystemdService || _environment.IsProduction()))
        {
            return;
        }

        if (LdapConfigCheck.Check(directory.Security, LdapConfigCheck.Inputs.FromEnvironment()) is { } reason)
        {
            throw new StartupCheckException(StartupExitCodes.InvalidSettings, reason);
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

        if (OperatingSystem.IsLinux())
        {
            CheckPosixDataDirectory();
            CheckDataProtectionKey();
            return;
        }

        // Other platforms (development only): the Data Protection key ring is not encrypted at rest.
        if (_environment.IsProduction())
        {
            throw new StartupCheckException(StartupExitCodes.DataDirectory, "Unprotected secret keys are refused in Production; run the service on Linux or Windows.");
        }

        Log.KeysUnprotected(_logger);
    }

    /// <summary>Item 1 of design 11: refused (exit 5) in Production unless <c>AllowInsecureDataDirectoryAcl</c>; elsewhere
    /// (lab, development, tests – no <c>UMask=0077</c>) and with the escape hatch a health Warning <c>storage.acl</c>.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void CheckPosixDataDirectory()
    {
        var problems = PosixPermissionCheck.CheckDataDirectory(_dataDirectory.ProtectedPaths(), PosixPermissionCheck.EffectiveUserId());
        if (problems.Count == 0)
        {
            return;
        }

        var list = string.Join(", ", problems);
        if (!_options.AllowInsecureDataDirectoryAcl && _environment.IsProduction())
        {
            Log.PosixProblem(_logger, list);
            throw new StartupCheckException(
                StartupExitCodes.DataDirectory,
                "The data folder is not private to the service user (owner, no group/other bits, no ACL, local file system): " + list + ".");
        }

        Log.PosixWarning(_logger, list);
        Report("storage.acl", HealthState.Warning, "The data folder is not private to the service user (AllowInsecureDataDirectoryAcl or not Production).", [.. problems]);
    }

    /// <summary>Design 10: the key ring is encrypted with the systemd credential <c>dp-key</c>; a Production start without
    /// it is refused (exit 9). Reports the credential variant (<c>host.credentialKey</c>, Status „Host”).</summary>
    private void CheckDataProtectionKey()
    {
        var keys = _services.GetService<DataProtectionKeySource>();
        string? variant = null;
        if (keys?.Key is null)
        {
            if (_environment.IsProduction())
            {
                throw new StartupCheckException(
                    StartupExitCodes.MissingCredential,
                    keys?.Problem == "invalid"
                        ? "The systemd credential dp-key does not hold a 32-byte key; recreate it (sudo admng-watchdog reset-secrets-key)."
                        : "The systemd credential dp-key is missing ($CREDENTIALS_DIRECTORY/dp-key); start the service through admng-watchdog.service.");
            }

            Log.KeysUnprotected(_logger);
        }
        else
        {
            variant = keys.ReadVariant();
            Report(HealthCheckCodes.HostCredentialKey, HealthState.Ok, "The secret key ring is encrypted with the systemd credential dp-key.", variant is null ? [] : [variant]);
        }

        _health.ReportHost(new HostStatusDto
        {
            OperatingSystem = OperatingSystemName(),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            CredentialKey = variant,
            Systemd = _context.IsSystemdService,
        });
    }

    /// <summary>Design 11 items 3 and 6 for the systemd unit: the keytab (exit 10) and the hardening warnings
    /// (<c>host.hardening</c>: umask, core dumps, readable credentials folder).</summary>
    private void CheckLinuxHost()
    {
        if (!OperatingSystem.IsLinux() || !_context.IsSystemdService)
        {
            return;
        }

        var keytab = KeytabPath(Environment.GetEnvironmentVariable("KRB5_KTNAME"));
        if (PosixPermissionCheck.CheckKeytab(keytab) is { Count: > 0 } problems)
        {
            throw new StartupCheckException(
                StartupExitCodes.Keytab,
                "The keytab must be owned by root, not writable by group or others and readable by the service (root:admng-keytab 0640): "
                + string.Join(", ", problems) + ".");
        }

        var hardening = PosixPermissionCheck.CheckHardening(
            PosixPermissionCheck.ReadUmask(), PosixPermissionCheck.CoreDumpsEnabled(), PosixPermissionCheck.DefaultCredentialsDirectory);
        if (hardening.Count > 0)
        {
            Log.HardeningWarning(_logger, string.Join(", ", hardening));
            Report(HealthCheckCodes.HostHardening, HealthState.Warning, "The unit's hardening is incomplete (umask 0077, no core dumps, credentials folder root-only).", [.. hardening]);
        }
    }

    /// <summary>The keytab of <c>KRB5_KTNAME</c> (<c>FILE:</c> prefix optional), else the package default.</summary>
    internal static string KeytabPath(string? krb5KtName)
    {
        var value = krb5KtName?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return KerberosBootstrapOptions.DefaultKeytabPath;
        }

        return value.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase) ? value[5..] : value;
    }

    private static string OperatingSystemName()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
            {
                if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                {
                    return line["PRETTY_NAME=".Length..].Trim('"');
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return System.Runtime.InteropServices.RuntimeInformation.OSDescription;
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
            var store = _services.GetRequiredService<IConfigStore>();
            if (OperatingSystem.IsLinux())
            {
                await MigrateConfigurationForLinuxAsync(store, cancellationToken).ConfigureAwait(false);
            }

            if (await ApplyRetentionFloorAsync(store, _options.Retention.MinEventsDays, cancellationToken).ConfigureAwait(false))
            {
                Log.RetentionRaisedToFloor(_logger, _options.Retention.MinEventsDays);
            }

            await _services.GetRequiredService<IWatchdogConfigProvider>().ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            SafeExceptionLog.LogError(_logger, ex, "configuration load", new EventId(EventLogIds.StorageProblem));
            throw new StartupCheckException(StartupExitCodes.Migration, "The stored configuration could not be read.");
        }
    }

    /// <summary>
    /// The configuration migration of stage B design 7.8: a stored schema version 1 document (Windows service 0.2.0) is
    /// saved once as version 2 – a Windows attribution source becomes OpenWec – with the audit summary
    /// <see cref="ConfigurationMigration.LinuxSummary"/>. Returns true when it saved.
    /// </summary>
    public static async Task<bool> MigrateConfigurationForLinuxAsync(IConfigStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var stored = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        if (stored?.Configuration is not { } configuration || ConfigurationMigration.ForLinux(configuration) is not { } migrated)
        {
            return false;
        }

        await store.SaveAsync(migrated, stored.Version, new PrincipalRef { Account = ConfigurationMigration.MigrationAccount },
            ConfigurationMigration.LinuxSummary, [], cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The retention floor of stage B design 14.2 for a stored document (both platforms): <c>retention.eventsDays</c>
    /// below <c>Watchdog:Retention:MinEventsDays</c> – e.g. the 365 days every 0.2.0 document carries – is raised to the
    /// floor and saved once with the audit summary <see cref="ConfigurationMigration.RetentionFloorSummary"/>. Returns true
    /// when it saved.
    /// </summary>
    public static async Task<bool> ApplyRetentionFloorAsync(IConfigStore store, int floor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var stored = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        if (stored?.Configuration is not { } configuration || ConfigurationMigration.RaiseEventsDaysToFloor(configuration, floor) is not { } raised)
        {
            return false;
        }

        await store.SaveAsync(raised, stored.Version, new PrincipalRef { Account = ConfigurationMigration.MigrationAccount },
            ConfigurationMigration.RetentionFloorSummary, [], cancellationToken).ConfigureAwait(false);
        return true;
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

        var settings = _options.Hosting.Certificate;
        var pem = CertificateSelector.IsPem(settings);
        if (pem && OperatingSystem.IsLinux()
            && PosixPermissionCheck.IsWorldReadable(string.IsNullOrWhiteSpace(settings.KeyPath) ? settings.Path! : settings.KeyPath))
        {
            throw new StartupCheckException(StartupExitCodes.Certificate, "The TLS key file must not be readable by everyone (expected root:admng-watchdog 0640).");
        }

        var certificate = _services.GetRequiredService<CertificateSelector>().Reload();
        if (certificate is null)
        {
            throw new StartupCheckException(
                StartupExitCodes.Certificate,
                pem
                    ? "No valid TLS certificate in Hosting.Certificate.Path/KeyPath (matching PEM certificate and key, serverAuth, not expired)."
                    : "No valid TLS certificate matches Hosting.Certificate (LocalMachine\\My, serverAuth, private key).");
        }

        Log.CertificateFingerprint(_logger, PemCertificateSource.Fingerprint(certificate));

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

        [LoggerMessage(EventId = 2004, Level = LogLevel.Warning, Message = "{Message}")]
        public static partial void ObsoleteSetting(ILogger logger, string message);

        [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "The secret key ring is not encrypted at rest (no systemd credential dp-key; lab/development only).")]
        public static partial void KeysUnprotected(ILogger logger);

        [LoggerMessage(EventId = EventLogIds.StorageProblem, Level = LogLevel.Error, Message = "The data folder is not private to the service user: {Problems}.")]
        public static partial void PosixProblem(ILogger logger, string problems);

        [LoggerMessage(EventId = EventLogIds.StorageProblem, Level = LogLevel.Warning, Message = "The data folder is not private to the service user: {Problems} (allowed outside Production or by AllowInsecureDataDirectoryAcl).")]
        public static partial void PosixWarning(ILogger logger, string problems);

        [LoggerMessage(EventId = EventLogIds.PermissionGap, Level = LogLevel.Warning, Message = "Unit hardening incomplete: {Problems} (expected UMask=0077, LimitCORE=0, /etc/admng-watchdog/credentials root 0700).")]
        public static partial void HardeningWarning(ILogger logger, string problems);

        [LoggerMessage(EventId = EventLogIds.ConfigurationChanged, Level = LogLevel.Warning,
            Message = "The stored event retention was below Watchdog:Retention:MinEventsDays and has been raised to {Days} days.")]
        public static partial void RetentionRaisedToFloor(ILogger logger, int days);

        [LoggerMessage(EventId = EventLogIds.CertificateFingerprint, Level = LogLevel.Information, Message = "TLS certificate SHA-256 fingerprint {Fingerprint}.")]
        public static partial void CertificateFingerprint(ILogger logger, string fingerprint);
    }
}
