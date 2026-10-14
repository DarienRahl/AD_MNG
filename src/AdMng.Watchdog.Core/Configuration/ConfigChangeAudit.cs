using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Configuration;

/// <summary>Result of <see cref="ConfigChangeAuditor.Compare"/>: the severity of the WatchdogConfigurationChanged event,
/// the changed settings and a culture-neutral summary for the configuration audit entry.</summary>
public sealed record ConfigChangeAudit
{
    /// <summary>Maximum of the item severities, at least <see cref="Severity.Medium"/> (every save raises the event).</summary>
    public required Severity Severity { get; init; }

    /// <summary>Changed settings, most severe first (then document order).</summary>
    public IReadOnlyList<ConfigChangeItem> Items { get; init; } = [];

    /// <summary><c>path description; path description; …</c> (at most <see cref="ConfigChangeAuditor.MaxSummaryItems"/>
    /// items, then <c>(+N more)</c>), or <c>no changes</c>. Contains no values, secrets or addresses – suitable for
    /// <c>IConfigStore.SaveAsync(summary)</c>.</summary>
    public required string Summary { get; init; }

    public bool HasChanges => Items.Count > 0;
}
