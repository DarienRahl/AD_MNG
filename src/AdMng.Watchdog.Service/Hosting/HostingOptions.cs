namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Bootstrap <c>Watchdog:Hosting</c> (spec 8.4, 19.2–19.3).</summary>
public sealed record HostingOptions
{
    public WatchdogServer Server { get; init; } = WatchdogServer.Kestrel;

    /// <summary>One or more URLs separated by ';' (https in production; http only on loopback in Development).</summary>
    public string Urls { get; init; } = "https://localhost:8443";

    public CertificateOptions Certificate { get; init; } = new();

    /// <summary>Reject every non-Kerberos identity (NTLM) – default true.</summary>
    public bool RequireKerberos { get; init; } = true;

    /// <summary>Optional CIDR allow-list checked on <c>Connection.RemoteIpAddress</c>.</summary>
    public string[] AllowedRemoteSubnets { get; init; } = [];

    /// <summary>CIDRs allowed to call <c>/healthz</c> anonymously (besides loopback and the machine's own addresses).</summary>
    public string[] MonitoringSubnets { get; init; } = [];
}
