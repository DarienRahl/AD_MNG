using System.Net;

namespace AdMng.Watchdog.Client;

/// <summary>Connection of <see cref="IWatchdogClient"/> to one watchdog service.</summary>
public sealed record WatchdogClientOptions
{
    public required Uri BaseAddress { get; init; }             // https://admng-wd.contoso.com:8443/
    /// <summary>Null = process identity (CredentialCache.DefaultCredentials). Kept in memory only.</summary>
    public NetworkCredential? Credential { get; init; }
    /// <summary>Optional SHA-256 pin of the server certificate (in addition to normal validation failing).</summary>
    public string? PinnedCertificateSha256 { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Tests/Development only: allow http://localhost or 127.0.0.1.</summary>
    public bool AllowInsecureLoopback { get; init; }
    public override string ToString() => $"WatchdogClientOptions({BaseAddress})";
}
