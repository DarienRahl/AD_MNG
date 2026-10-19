using AdMng.Watchdog.Core.Diagnostics;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Creates <see cref="ServiceOptions"/> from the live (watched) configuration, but pins every setting the host consumed
/// when it was built to its start-up value: <c>DataDirectory</c>, <c>AllowInsecureDataDirectoryAcl</c>,
/// <c>Authentication</c>, <c>Hosting</c> except the two subnet lists, <c>Directory</c> and <c>Development</c>. So an edit of
/// the bootstrap file applies <c>Authorization</c> (incl. the emergency revocation <c>DeniedSids</c>),
/// <c>Notifications</c> and <c>Hosting.AllowedRemoteSubnets</c>/<c>MonitoringSubnets</c> immediately, and never leaves
/// the running host half-switched (e.g. Kerberos enforcement off while HTTP.sys still runs with its start-up scheme).
/// An edit that is valid JSON but holds a value that cannot be bound (an unknown enum name such as
/// <c>"ViewerDetailLevel": "Minimalny"</c>, <c>"tak"</c> for a flag) keeps the last good options and logs warning 2002
/// (type only, never the value): otherwise every reader of <c>CurrentValue</c> – each request, <c>/healthz</c>, the
/// directory and notification jobs – would throw until the file is fixed.
/// </summary>
public sealed partial class ServiceOptionsFactory : IOptionsFactory<ServiceOptions>
{
    private readonly OptionsFactory<ServiceOptions> _inner;
    private readonly ServiceOptions _startup;
    private readonly ILogger? _logger;
    private ServiceOptions? _lastGood;

    public ServiceOptionsFactory(
        IEnumerable<IConfigureOptions<ServiceOptions>> setups,
        IEnumerable<IPostConfigureOptions<ServiceOptions>> postConfigures,
        IEnumerable<IValidateOptions<ServiceOptions>> validations,
        StartupServiceOptions startup,
        ILogger<ServiceOptionsFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(startup);
        _inner = new OptionsFactory<ServiceOptions>(setups, postConfigures, validations);
        _startup = startup.Value;
        _logger = logger;
    }

    public ServiceOptions Create(string name)
    {
        ServiceOptions live;
        try
        {
            live = _inner.Create(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            // The binder names the setting AND its value in the message: log the type only.
            if (_logger is not null)
            {
                Log.CannotBind(_logger, SafeExceptionLog.ErrorCode(ex));
            }

            return Volatile.Read(ref _lastGood) ?? _startup;
        }

        var pinned = Pin(live, _startup);
        Volatile.Write(ref _lastGood, pinned);
        return pinned;
    }

    /// <summary>The live options with the start-up-only settings taken from <paramref name="startup"/>.</summary>
    public static ServiceOptions Pin(ServiceOptions live, ServiceOptions startup)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(startup);
        var liveHosting = live.Hosting ?? new HostingOptions();
        return live with
        {
            DataDirectory = startup.DataDirectory,
            AllowInsecureDataDirectoryAcl = startup.AllowInsecureDataDirectoryAcl,
            Authentication = startup.Authentication,
            Hosting = (startup.Hosting ?? new HostingOptions()) with
            {
                AllowedRemoteSubnets = liveHosting.AllowedRemoteSubnets ?? [],
                MonitoringSubnets = liveHosting.MonitoringSubnets ?? [],
            },
            Directory = startup.Directory,
            Development = startup.Development,
        };
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = ConfigurationSetup.ReloadFailedEventId, Level = LogLevel.Warning,
            Message = "The edited bootstrap file (appsettings.json in the config folder) has a setting value that cannot be applied ({ErrorCode}); the previous settings stay in effect until it is fixed (check the setting names and values, AdMng.Watchdog.exe --check-config).")]
        public static partial void CannotBind(ILogger logger, string errorCode);
    }
}

/// <summary>The <see cref="ServiceOptions"/> the host was built with (the snapshot <see cref="ServiceOptionsFactory"/> pins).</summary>
/// <param name="Value">Bound from the configuration before any registration.</param>
public sealed record StartupServiceOptions(ServiceOptions Value);
