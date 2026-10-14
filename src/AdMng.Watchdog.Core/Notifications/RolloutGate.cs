using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Rollout mode as the planner applies it (section 12.5, the rules area's <c>RolloutMode</c>): active while
/// <c>!General.RolloutEnded &amp;&amp; General.RolloutDays &gt; 0 &amp;&amp; now &lt; BaselineCompletedAt + RolloutDays</c>; while
/// active only rules whose id starts with <c>tier0-</c>, <c>bursts</c> and <c>health</c> notify immediately. The baseline
/// time comes from the health registry (reported by detection) or, before the first report, from the stored DC state.
/// </summary>
public sealed class RolloutGate
{
    private readonly IHealthRegistry _health;
    private readonly IDetectionStore _detection;

    public RolloutGate(IHealthRegistry health, IDetectionStore detection)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(detection);
        _health = health;
        _detection = detection;
    }

    /// <summary>True for the rules that notify immediately even in rollout mode.</summary>
    public static bool AllowsImmediate(string? ruleId) =>
        ruleId is not null
        && (ruleId.StartsWith("tier0-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ruleId, "bursts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ruleId, "health", StringComparison.OrdinalIgnoreCase));

    /// <summary>End of rollout mode for a baseline time, or null when the mode is off, ended or the baseline is unknown.</summary>
    public static DateTimeOffset? Until(GeneralSettings? general, DateTimeOffset? baselineCompletedAt) =>
        general is null || general.RolloutEnded || general.RolloutDays <= 0 || baselineCompletedAt is not { } baseline
            ? null
            : baseline.AddDays(general.RolloutDays);

    /// <summary>True while rollout mode is active at <paramref name="now"/> for the watched <paramref name="domain"/>.</summary>
    public async Task<bool> IsActiveAsync(WatchdogConfiguration configuration, string? domain, DateTimeOffset now, CancellationToken cancellationToken) =>
        await ActiveUntilAsync(configuration, domain, now, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>The end of rollout mode when it is active at <paramref name="now"/> (digest header), else null.</summary>
    public async Task<DateTimeOffset?> ActiveUntilAsync(WatchdogConfiguration configuration, string? domain, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var general = configuration.General;
        if (general is null || general.RolloutEnded || general.RolloutDays <= 0)
        {
            return null;
        }

        var baseline = _health.Directory.BaselineCompletedAt;
        if (baseline is null && !string.IsNullOrWhiteSpace(domain))
        {
            baseline = (await _detection.GetDcStateAsync(domain, cancellationToken).ConfigureAwait(false))?.BaselineCompletedAt;
        }

        return Until(general, baseline) is { } until && now < until ? until : null;
    }
}
