namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Bootstrap <c>Watchdog:Hosting:Certificate</c>: the Kestrel TLS certificate – in LocalMachine\My (Windows, spec
/// 19.3) or as PEM files (Linux, stage B design 6.3: <see cref="Path"/> / <see cref="KeyPath"/>).</summary>
public sealed record CertificateOptions
{
    /// <summary>The shipped Linux value of <see cref="Path"/>.</summary>
    public const string DefaultLinuxPath = "/etc/admng-watchdog/tls/admng-wd.crt";

    /// <summary>The shipped Linux value of <see cref="KeyPath"/>.</summary>
    public const string DefaultLinuxKeyPath = "/etc/admng-watchdog/tls/admng-wd.key";

    /// <summary>Linux: PEM certificate chain, leaf first (root:admng-watchdog 0644).</summary>
    public string? Path { get; init; }

    /// <summary>Linux: PEM private key of <see cref="Path"/> (root:admng-watchdog 0640, never world-readable – exit code 4).</summary>
    public string? KeyPath { get; init; }

    /// <summary>Wins over <see cref="Subject"/>.</summary>
    public string? Thumbprint { get; init; }

    /// <summary>Subject (CN or full subject) – the newest valid certificate with serverAuth EKU and a private key wins.</summary>
    public string? Subject { get; init; }

    public string Store { get; init; } = "My";

    public string Location { get; init; } = "LocalMachine";
}
