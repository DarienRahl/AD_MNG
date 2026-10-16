using AdMng.Core.Ldap;
using AdMng.Core.Schema;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>What parse-time reduction (<see cref="SecurityEventReducer"/>) needs for one poll.</summary>
public sealed record SecurityEventReduction
{
    /// <summary><c>Directory.ExcludedSubtrees</c>, expanded and parsed.</summary>
    public IReadOnlyList<DistinguishedName> ExcludedSubtrees { get; init; } = [];

    /// <summary>The watched attribute set.</summary>
    public required IWatchedAttributeCatalog WatchedAttributes { get; init; }

    /// <summary>Schema of the directory session (confidential attributes); null = not loaded → no value is kept.</summary>
    public ISchemaCatalog? Schema { get; init; }
}
