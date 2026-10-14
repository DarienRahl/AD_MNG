namespace AdMng.Watchdog.Contracts.Configuration;

public sealed record GeneralSettings
{
    public string InstanceName { get; init; } = "AD Watchdog";
    public string Language { get; init; } = "pl";                 // notifications: "pl" | "en"
    /// <summary>IANA or Windows id, resolved by TimeZoneResolver (works on NLS-mode hosts such as Windows Server 2019).</summary>
    public string TimeZone { get; init; } = "Europe/Warsaw";
    public string? PublicBaseUrl { get; init; }                   // https://admng-wd.contoso.com:8443 – links; null = no links
    /// <summary>Rollout mode length after the baseline (12.5); 0 = off.</summary>
    public int RolloutDays { get; init; } = 7;
    /// <summary>Set by an admin to end rollout mode early.</summary>
    public bool RolloutEnded { get; init; }
}
