using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Model;

/// <summary>Stored state of one watched attribute.</summary>
public sealed record SnapshotAttribute
{
    public required IReadOnlyList<string> Values { get; init; }       // canonical (section 9.4), comparison form
    public IReadOnlyList<string> DisplayValues { get; init; } = [];   // DN values: display DN (same order); empty = Values
    public AttributeValueKind Kind { get; init; }
    public int? Version { get; init; }                                // replication metadata dwVersion when known
    public DateTimeOffset? ChangedAt { get; init; }                   // originating time, else detection time
}

/// <summary>Snapshot of one object in scope: identity plus its watched attributes only (section 9).</summary>
public sealed record SnapshotObject
{
    public required Guid ObjectGuid { get; init; }
    public required string Dn { get; init; }
    public required WatchObjectKind Kind { get; init; }
    public IReadOnlyList<string> ObjectClasses { get; init; } = [];
    public string? Sid { get; init; }
    public string? SamAccountName { get; init; }
    public string? Name { get; init; }
    public string? DisplayName { get; init; }
    public long UsnCreated { get; init; }
    public long UsnChanged { get; init; }
    public DateTimeOffset? WhenCreated { get; init; }
    /// <summary>Only rows marked deleted by the DeletedObjectsScanner / reconciliation tombstone lookup. A later live
    /// appearance of such a GUID is ObjectRestored; out-of-scope or disappeared objects are removed instead (Removals).</summary>
    public bool IsDeleted { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    /// <summary>Settle window of a new object (10.6.3): the Created event is emitted when this time has passed.</summary>
    public DateTimeOffset? CreationPendingUntil { get; init; }
    public IReadOnlyDictionary<string, SnapshotAttribute> Attributes { get; init; } =
        new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The event model reference of this object.</summary>
    public ObjectRef ToRef() => new()
    {
        ObjectGuid = ObjectGuid,
        Dn = Dn,
        Name = Name,
        DisplayName = DisplayName,
        SamAccountName = SamAccountName,
        Sid = Sid,
        Kind = Kind,
    };
}
