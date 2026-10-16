using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>Everything <see cref="ChangeClassifier"/> needs besides the diff; asynchronous lookups are done beforehand.</summary>
internal sealed record ClassificationInput
{
    public required RawObjectDiff Diff { get; init; }
    public required DirectorySettings Directory { get; init; }
    public required DateTimeOffset Now { get; init; }
    public ReplicationMetadata Metadata { get; init; } = ReplicationMetadata.Empty;

    /// <summary>Watermark W of this DC (null when it belongs to another DC's USN space – Resync).</summary>
    public long? Watermark { get; init; }

    /// <summary>Effective lockout policy (read when lockoutTime changed).</summary>
    public LockoutPolicy Lockout { get; init; } = LockoutPolicy.Unknown;

    /// <summary>Earlier AccountLockedOut events of this object since local midnight.</summary>
    public int LockoutsToday { get; init; }

    /// <summary>True when the member key is in the current PrivilegeState.</summary>
    public Func<string, bool> IsPrivileged { get; init; } = _ => false;

    /// <summary>NetBIOS names (upper case, without $) of the domain controllers – ComputerNameSpoofing.</summary>
    public IReadOnlySet<string> DomainControllerNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The new dNSHostName of a computer equals another computer's or a DC's (checked by the caller).</summary>
    public bool DnsHostNameConflict { get; init; }

    /// <summary>GPO DN (normalized upper case) → displayName, for GroupPolicyLinkChanged.</summary>
    public IReadOnlyDictionary<string, string> GroupPolicyNames { get; init; } = new Dictionary<string, string>();

    /// <summary>NTDS Settings DN → DC host name (FsmoRoleChanged); null when unknown.</summary>
    public Func<string, string?> DsaHostName { get; init; } = _ => null;
}
