using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Environment of a validation run (section 8.3), built by the service.</summary>
public sealed record ConfigValidationContext
{
    /// <summary>Stored secret names → binding (built by the Service from ISecretStore.ListAsync).</summary>
    public IReadOnlyDictionary<string, string> StoredSecrets { get; init; } = new Dictionary<string, string>();
    public bool IsDevelopment { get; init; }
    /// <summary>Bootstrap Authorization.AllowTier0Suppression (8.3 tier0Suppression).</summary>
    public bool AllowTier0Suppression { get; init; }
    /// <summary>Bootstrap Notifications.OAuthSmtpHosts (XOAUTH2 destinations).</summary>
    public IReadOnlyList<string> OAuthSmtpHosts { get; init; } = ["smtp.office365.com"];

    /// <summary>Bootstrap <c>Watchdog:Retention:MinEventsDays</c>: the lowest <c>retention.eventsDays</c> an API admin may
    /// set (<c>retentionBelowFloor</c>, stage B design 14.2); 0 = no floor.</summary>
    public int MinEventsDays { get; init; }

    /// <summary>The attribution sources this host supports (<c>sourceUnsupportedOnHost</c>, stage B design 7.8); empty =
    /// every source.</summary>
    public IReadOnlyList<AttributionSourceKind> SupportedAttributionSources { get; init; } = [];
}

public interface IConfigurationValidator
{
    ConfigValidationResult Validate(WatchdogConfiguration configuration, ConfigValidationContext context);
}
