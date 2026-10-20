namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Facts fixed when the host is built and checked by <see cref="StartupChecks"/>.</summary>
public sealed record StartupContext
{
    /// <summary>Running under the Windows service control manager.</summary>
    public bool IsWindowsService { get; init; }

    /// <summary>Running as a systemd unit (Linux: <c>INVOCATION_ID</c> or <c>NOTIFY_SOCKET</c> is set –
    /// <see cref="SystemdSetup.IsSystemdService()"/>).</summary>
    public bool IsSystemdService { get; init; }

    /// <summary>Started by a service manager (Windows SCM or systemd): no lab configuration file
    /// (<c>ADMNG_WATCHDOG_CONFIG</c>), never the Development scheme, the bootstrap file must not be writable by the
    /// service identity.</summary>
    public bool IsManagedService => IsWindowsService || IsSystemdService;

    /// <summary>The listeners from <c>Hosting.Urls</c>.</summary>
    public IReadOnlyList<ListenerEndpoint> Endpoints { get; init; } = [];

    public WatchdogServer Server { get; init; } = WatchdogServer.Kestrel;

    /// <summary>The lab/dev configuration file named by <c>ADMNG_WATCHDOG_CONFIG</c>, when set.</summary>
    public string? ExtraConfigFile { get; init; }

    /// <summary>The bootstrap file (<see cref="ConfigurationSetup.BootstrapFilePath"/>).</summary>
    public string? BootstrapFile { get; init; }

    /// <summary><c>Hosting.Urls</c> could not be parsed (the message is a constant hint).</summary>
    public string? EndpointError { get; init; }
}
