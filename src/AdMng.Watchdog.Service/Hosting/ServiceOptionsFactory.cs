using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Creates <see cref="ServiceOptions"/> from the live (watched) configuration, but pins every setting the host consumed
/// when it was built to its start-up value: <c>DataDirectory</c>, <c>AllowInsecureDataDirectoryAcl</c>,
/// <c>Authentication</c>, <c>Hosting</c> except the two subnet lists, <c>Directory</c> and <c>Development</c>. So an edit of
/// the bootstrap file applies <c>Authorization</c> (incl. the emergency revocation <c>DeniedSids</c>),
/// <c>Notifications</c> and <c>Hosting.AllowedRemoteSubnets</c>/<c>MonitoringSubnets</c> immediately, and never leaves
/// the running host half-switched (e.g. Kerberos enforcement off while HTTP.sys still runs with its start-up scheme).
/// </summary>
public sealed class ServiceOptionsFactory : IOptionsFactory<ServiceOptions>
{
    private readonly OptionsFactory<ServiceOptions> _inner;
    private readonly ServiceOptions _startup;

    public ServiceOptionsFactory(
        IEnumerable<IConfigureOptions<ServiceOptions>> setups,
        IEnumerable<IPostConfigureOptions<ServiceOptions>> postConfigures,
        IEnumerable<IValidateOptions<ServiceOptions>> validations,
        StartupServiceOptions startup)
    {
        ArgumentNullException.ThrowIfNull(startup);
        _inner = new OptionsFactory<ServiceOptions>(setups, postConfigures, validations);
        _startup = startup.Value;
    }

    public ServiceOptions Create(string name) => Pin(_inner.Create(name), _startup);

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
}

/// <summary>The <see cref="ServiceOptions"/> the host was built with (the snapshot <see cref="ServiceOptionsFactory"/> pins).</summary>
/// <param name="Value">Bound from the configuration before any registration.</param>
public sealed record StartupServiceOptions(ServiceOptions Value);
