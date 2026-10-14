using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Rollout mode (section 12.5): active while <c>!General.RolloutEnded &amp;&amp; General.RolloutDays &gt; 0 &amp;&amp;
/// now &lt; BaselineCompletedAt + RolloutDays</c>. The end derives from the baseline time (no service-initiated
/// configuration write) and the mode ends by itself – a forgotten mode never silences alerts indefinitely. While
/// active only <c>tier0-*</c>, <c>bursts</c> and <c>health</c> notify immediately; every other match goes to the digest.
/// </summary>
public static class RolloutMode
{
    /// <summary>End of rollout mode (<c>StatusDto.RolloutModeUntil</c>); null when off, ended early, or before the
    /// baseline completed.</summary>
    public static DateTimeOffset? Until(GeneralSettings? general, DateTimeOffset? baselineCompletedAt)
    {
        if (general is null || general.RolloutEnded || general.RolloutDays <= 0 || baselineCompletedAt is not { } baseline)
        {
            return null;
        }

        return baseline.AddDays(general.RolloutDays);
    }

    /// <summary>True while rollout mode is active at <paramref name="now"/>.</summary>
    public static bool IsActive(GeneralSettings? general, DateTimeOffset? baselineCompletedAt, DateTimeOffset now) =>
        Until(general, baselineCompletedAt) is { } until && now < until;

    /// <summary><see cref="Until"/> when still in the future at <paramref name="now"/>, else null (the status banner).</summary>
    public static DateTimeOffset? ActiveUntil(GeneralSettings? general, DateTimeOffset? baselineCompletedAt, DateTimeOffset now) =>
        Until(general, baselineCompletedAt) is { } until && now < until ? until : null;

    /// <summary>True for the rules that notify immediately even in rollout mode: <c>tier0-*</c>, <c>bursts</c>,
    /// <c>health</c>.</summary>
    public static bool AllowsImmediate(string? ruleId) =>
        Tier0Catalog.IsTier0Rule(ruleId)
        || string.Equals(ruleId, DefaultRules.Bursts, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ruleId, DefaultRules.Health, StringComparison.OrdinalIgnoreCase);
}
