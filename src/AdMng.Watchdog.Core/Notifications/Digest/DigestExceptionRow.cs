using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>"Aktywne wyjątki": a rule exclusion that is still in force (configuration data, not directory data).</summary>
public sealed record DigestExceptionRow(RuleSettings Rule, RuleExclusion Exclusion) : DigestRow
{
    /// <inheritdoc/>
    public override DateTimeOffset? Time => Exclusion.ExpiresAt;
}
