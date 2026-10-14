using AdMng.Core.Ldap;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>Inputs of <see cref="EventTagger"/> that do not come from the object itself.</summary>
internal sealed record TaggingContext
{
    /// <summary>True when the member key (GUID "N", sid:…, dn:…) is in the current PrivilegeState.</summary>
    public required Func<string, bool> IsPrivileged { get; init; }

    public WellKnownContainers? Containers { get; init; }
    public IReadOnlyList<DistinguishedName> ServiceAccountSubtrees { get; init; } = [];
    public IReadOnlyList<DistinguishedName> DisabledAccountsSubtrees { get; init; } = [];

    /// <summary>Events of a full scan (Resync / Reconciliation) – tag reconciliation.</summary>
    public bool Reconciliation { get; init; }
}
