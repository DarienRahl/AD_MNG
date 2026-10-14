namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Bootstrap <c>Watchdog:Hosting:Certificate</c>: the Kestrel TLS certificate in LocalMachine\My (spec 19.3).</summary>
public sealed record CertificateOptions
{
    /// <summary>Wins over <see cref="Subject"/>.</summary>
    public string? Thumbprint { get; init; }

    /// <summary>Subject (CN or full subject) – the newest valid certificate with serverAuth EKU and a private key wins.</summary>
    public string? Subject { get; init; }

    public string Store { get; init; } = "My";

    public string Location { get; init; } = "LocalMachine";
}
