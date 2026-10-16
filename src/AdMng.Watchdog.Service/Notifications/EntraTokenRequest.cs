using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>An OAuth2 client-credentials token request against the fixed authority of <see cref="Cloud"/> (spec 15.6–15.7).
/// The authority host and the scope come from <see cref="EntraCloudInfo"/> – never free text.</summary>
public sealed record EntraTokenRequest
{
    public required EntraCloud Cloud { get; init; }

    /// <summary>Tenant GUID.</summary>
    public required string TenantId { get; init; }

    /// <summary>Application (client) GUID.</summary>
    public required string ClientId { get; init; }

    public required string Scope { get; init; }

    /// <summary>LocalMachine\My certificate (preferred over a client secret).</summary>
    public string? CertificateThumbprint { get; init; }

    public SecretValue? ClientSecret { get; init; }

    public override string ToString() =>
        $"EntraTokenRequest({Cloud}, client {ClientId}, {(CertificateThumbprint is null ? "secret" : "certificate")})";
}
