using AdMng.Core.Computers;

namespace AdMng.Presentation.Settings;

/// <summary>A placement rule of the settings: the containers (portable DNs with <c>{domain}</c>, subtrees) a type may be in.</summary>
public sealed record ComputerPlacementSetting
{
    public ComputerType Type { get; init; }

    public List<string> Containers { get; init; } = [];
}

/// <summary>An operator's end-of-support date for a build and edition class (e.g. ESU bought for Windows 10).</summary>
public sealed record ComputerReleaseOverrideSetting
{
    public int Build { get; init; }

    public EditionClass Edition { get; init; }

    public DateOnly EndOfSupport { get; init; }
}

/// <summary>
/// Computers (K1): the review's parameters, reports and scope, placement rules, catalogue overrides, the remote options
/// and the restart defaults. Never computer names, results or remote output (K1 design note 8).
/// </summary>
public sealed class ComputerSettings
{
    public const int MaxRules = 50;
    public const int MaxOverrides = 200;
    public const int MaxRestartMessageLength = 500;
    public const int MaxRestartDelayMinutes = 1440;

    /// <summary>N days of the last run.</summary>
    public ComputerReportParameters Parameters { get; set; } = new();

    /// <summary>Reports the operator switched off (all others run).</summary>
    public List<ComputerReport> DisabledReports { get; set; } = [];

    /// <summary>Scope of the last run (the same scope kinds as the account review).</summary>
    public AccountReviewScope Scope { get; set; } = new();

    public List<ComputerPlacementSetting> PlacementRules { get; set; } = [];

    public List<ComputerReleaseOverrideSetting> ReleaseOverrides { get; set; } = [];

    /// <summary>WinRM connect/operation timeout in seconds (5–300).</summary>
    public int RemoteTimeoutSeconds { get; set; } = 30;

    /// <summary>WinRM over HTTPS (5986) instead of HTTP with Kerberos encryption (5985).</summary>
    public bool RemoteUseHttps { get; set; }

    /// <summary>A non-default WinRM port (null = 5985 / 5986).</summary>
    public int? RemotePort { get; set; }

    /// <summary>Also refresh the policy of logged-on users (scheduled task) – off until the pilot confirms it.</summary>
    public bool RefreshUserPolicy { get; set; }

    /// <summary>Default delay of "Uruchom ponownie…" in minutes.</summary>
    public int RestartDelayMinutes { get; set; } = 10;

    /// <summary>The operator's default restart message; null = the built-in text of the UI language.</summary>
    public string? RestartMessage { get; set; }

    /// <summary>Repairs values of a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        Parameters = (Parameters ?? new()).Normalized();
        DisabledReports = [.. (DisabledReports ?? []).Where(Enum.IsDefined).Distinct()];
        Scope ??= new();
        PlacementRules = [.. (PlacementRules ?? [])
            .Where(r => r is not null && Enum.IsDefined(r.Type))
            .Select(r => r with { Containers = [.. (r.Containers ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)] })
            .Where(r => r.Containers.Count > 0)
            .DistinctBy(r => r.Type)
            .Take(MaxRules)];
        ReleaseOverrides = [.. (ReleaseOverrides ?? [])
            .Where(o => o is not null && o.Build > 0 && Enum.IsDefined(o.Edition))
            .DistinctBy(o => (o.Build, o.Edition))
            .Take(MaxOverrides)];
        RemoteTimeoutSeconds = Math.Clamp(RemoteTimeoutSeconds, Core.Computers.Remote.RemoteOptions.MinTimeoutSeconds, Core.Computers.Remote.RemoteOptions.MaxTimeoutSeconds);
        RemotePort = RemotePort is > 0 and <= 65535 ? RemotePort : null;
        RestartDelayMinutes = Math.Clamp(RestartDelayMinutes, 0, MaxRestartDelayMinutes);
        RestartMessage = string.IsNullOrWhiteSpace(RestartMessage) ? null
            : RestartMessage.Length > MaxRestartMessageLength ? RestartMessage[..MaxRestartMessageLength] : RestartMessage;
    }

    /// <summary>The overrides as catalogue overrides.</summary>
    public IReadOnlyList<WindowsReleaseOverride> CatalogOverrides() =>
        [.. ReleaseOverrides.Select(o => new WindowsReleaseOverride(o.Build, o.Edition, o.EndOfSupport))];

    /// <summary>The remote options of the client.</summary>
    public Core.Computers.Remote.RemoteOptions RemoteOptions() => new()
    {
        Timeout = TimeSpan.FromSeconds(RemoteTimeoutSeconds),
        UseHttps = RemoteUseHttps,
        Port = RemotePort,
        RefreshUserPolicy = RefreshUserPolicy,
    };
}
