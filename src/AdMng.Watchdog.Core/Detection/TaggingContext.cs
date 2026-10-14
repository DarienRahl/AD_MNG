using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>Inputs of <see cref="EventTagger"/> that do not come from the object itself.</summary>
internal sealed record TaggingContext
{
    /// <summary>True when the member key (GUID "N", sid:…, dn:…) is in the current PrivilegeState.</summary>
    public required Func<string, bool> IsPrivileged { get; init; }

    /// <summary>True for a privileged root group itself (it is not a principal of the PrivilegeState, yet adminCount=1
    /// on it is expected – no adminCountOrphan).</summary>
    public Func<SnapshotObject, bool> IsPrivilegedRoot { get; init; } = _ => false;

    public WellKnownContainers? Containers { get; init; }
    public IReadOnlyList<DistinguishedName> ServiceAccountSubtrees { get; init; } = [];
    public IReadOnlyList<DistinguishedName> DisabledAccountsSubtrees { get; init; } = [];

    /// <summary>Events of a full scan (Resync / Reconciliation) – tag reconciliation.</summary>
    public bool Reconciliation { get; init; }
}
