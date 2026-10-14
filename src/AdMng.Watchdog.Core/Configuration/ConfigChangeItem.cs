using System.Globalization;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Configuration;

/// <summary>What happened to one setting path (<see cref="ConfigChangeItem.Kind"/>).</summary>
public enum ConfigChangeKind
{
    /// <summary>A keyed element (rule, channel, recipient group) or an optional object (e.g. <c>teams</c>) was added.</summary>
    Added,
    /// <summary>A keyed element or an optional object was removed.</summary>
    Removed,
    /// <summary>A value or a list changed (lists carry added/removed counts).</summary>
    Modified,
    /// <summary>A secret was entered where none was set.</summary>
    SecretSet,
    /// <summary>A stored secret was replaced (<c>NewValue</c>).</summary>
    SecretReplaced,
    /// <summary>A stored secret was removed (<c>Clear</c>, or <c>IsSet</c> true → false).</summary>
    SecretCleared,
}

/// <summary>
/// One changed setting of a configuration save (<see cref="ConfigChangeAuditor.Compare"/>). Carries the path and counts
/// only – never values, secret values, recipient addresses or exclusion contents.
/// </summary>
public sealed record ConfigChangeItem
{
    /// <summary>JSON path with camelCase names; keyed collections by id, e.g. <c>smtp.host</c>,
    /// <c>rules[tier0-security].delivery.immediate</c>, <c>channels[teams-it].url</c>,
    /// <c>recipientGroups[it-admins].addresses</c>, <c>rules[lockouts].exclusions</c>.</summary>
    public required string Path { get; init; }

    public required ConfigChangeKind Kind { get; init; }

    /// <summary>Severity of this change alone (section 7 row of WatchdogConfigurationChanged).</summary>
    public required Severity Severity { get; init; }

    /// <summary>True for list settings (and the collapsed exclusion list): the counts below describe the change.</summary>
    public bool IsCollection { get; init; }

    /// <summary>Lists: entries added (case-insensitive multiset difference).</summary>
    public int AddedCount { get; init; }

    /// <summary>Lists: entries removed.</summary>
    public int RemovedCount { get; init; }

    /// <summary>Exclusion lists: exclusions with the same id whose content changed.</summary>
    public int ModifiedCount { get; init; }

    /// <summary>Culture-neutral description: <c>added</c>, <c>removed</c>, <c>changed</c>, <c>set</c>,
    /// <c>replaced</c>, <c>cleared</c>; lists <c>+2 -1</c> (exclusions <c>+1 -0 ~1</c>) or <c>reordered</c> when only the
    /// order or letter case changed.</summary>
    public string Description => Kind switch
    {
        ConfigChangeKind.Added => "added",
        ConfigChangeKind.Removed => "removed",
        ConfigChangeKind.SecretSet => "set",
        ConfigChangeKind.SecretReplaced => "replaced",
        ConfigChangeKind.SecretCleared => "cleared",
        _ when !IsCollection => "changed",
        _ when AddedCount == 0 && RemovedCount == 0 && ModifiedCount == 0 => "reordered",
        _ => string.Create(CultureInfo.InvariantCulture, $"+{AddedCount} -{RemovedCount}")
             + (ModifiedCount > 0 ? string.Create(CultureInfo.InvariantCulture, $" ~{ModifiedCount}") : string.Empty),
    };
}
