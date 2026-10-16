using AdMng.Watchdog.Contracts.Events;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// An event before it gets its identity, tags and severity (<see cref="EventFactory"/>): what the classifiers decide.
/// </summary>
internal sealed record EventDraft
{
    public required WatchEventKind Kind { get; init; }

    /// <summary>Step-1 severity when the event alone cannot tell it (PrivilegeGranted tier, privileged link target).</summary>
    public Severity? BaseSeverity { get; init; }

    public IReadOnlyList<AttributeChange> Changes { get; init; } = [];
    public ObjectRef? Member { get; init; }
    public IReadOnlyList<ObjectRef> Via { get; init; } = [];
    public LockoutInfo? Lockout { get; init; }
    public ExpiryInfo? Expiry { get; init; }
    public BurstInfo? Burst { get; init; }
    public string? PreviousDn { get; init; }

    /// <summary>Tags decided by the classifier (outOfScope, restored, otherDomain …) in addition to the tagger's.</summary>
    public IReadOnlyList<string> ExtraTags { get; init; } = [];

    /// <summary>Attributes whose replication metadata gives OriginatingTime/OriginatingDc (section 10.10).</summary>
    public IReadOnlyList<string> MetadataAttributes { get; init; } = [];

    /// <summary>Overrides the default attribution (NotApplicable kinds, sources of bursts).</summary>
    public EventAttribution? Attribution { get; init; }

    /// <summary>Overrides the event object (privilege events: the principal; bursts: the domain).</summary>
    public ObjectRef? Object { get; init; }

    /// <summary>Overrides the change set (privileges of a restored object inherit the ObjectRestored change set).</summary>
    public Guid? ChangeSetId { get; init; }
}
