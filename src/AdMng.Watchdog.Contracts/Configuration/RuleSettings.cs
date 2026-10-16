using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Contracts.Configuration;

public enum DeliveryHours { Default, Always, BusinessHours }   // Default: Always for ≥ High, else BusinessHours
public enum GroupFilter { Any, Privileged, WatchList, Explicit }

public sealed record RuleScope
{
    public IReadOnlyList<string> IncludeSubtrees { get; init; } = [];   // empty = whole domain
    public IReadOnlyList<string> ExcludeSubtrees { get; init; } = [];
}

public sealed record ActorFilter
{
    public IReadOnlyList<string> ExcludeActors { get; init; } = [];     // SIDs only (the GUI resolves names to SIDs)
    public bool ExcludeSelfChanges { get; init; }
}

public sealed record RuleDelivery
{
    public bool Immediate { get; init; }
    public IReadOnlyList<string> RecipientGroupIds { get; init; } = [];
    public IReadOnlyList<string> ChannelIds { get; init; } = [];
    /// <summary>Also every enabled Teams/webhook channel (the locked health rule ships with true).</summary>
    public bool AllEnabledChannels { get; init; }
    public bool IncludeInDigest { get; init; } = true;
    public DeliveryHours Hours { get; init; } = DeliveryHours.Default;
    public TimeSpan? AggregationWindow { get; init; }
    public int? MaxMessagesPerHour { get; init; }
    /// <summary>Per object: after an immediate notification about an object, further events of this rule for the same
    /// object within this period go to the digest only (lockouts: 1 h).</summary>
    public TimeSpan? RepeatSuppression { get; init; }
    /// <summary>Expiry kinds: also e-mail the object's manager (manager → mail, detail level Minimal). Off by default.</summary>
    public bool NotifyManager { get; init; }
}

public sealed record RuleExclusion
{
    public required string Id { get; init; }
    public Guid? ObjectGuid { get; init; }
    public string? SubtreeDn { get; init; }
    public string? Actor { get; init; }            // SID only
    public string? Attribute { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required string CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A notification rule (section 12).</summary>
public sealed record RuleSettings
{
    public required string Id { get; init; }                         // [a-z0-9-]{1,60}
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public bool BuiltIn { get; init; }
    public bool Locked { get; init; }                                // health rule: cannot be disabled/deleted
    public IReadOnlyList<WatchEventKind> Kinds { get; init; } = [];  // empty = any
    public IReadOnlyList<WatchObjectKind> ObjectKinds { get; init; } = [];
    public RuleScope Scope { get; init; } = new();
    public IReadOnlyList<string> Attributes { get; init; } = [];     // AttributesChanged etc.: any of these changed
    public IReadOnlyList<string> RequiredTags { get; init; } = [];   // event must carry ALL
    public IReadOnlyList<string> AnyTags { get; init; } = [];        // event must carry ANY (when non-empty)
    public GroupFilter Groups { get; init; } = GroupFilter.Any;      // membership kinds: which groups
    public IReadOnlyList<string> GroupDns { get; init; } = [];       // GroupFilter.Explicit
    public Severity? MinSeverity { get; init; }
    public Severity? Severity { get; init; }                         // override for this rule's notifications
    public ActorFilter Actors { get; init; } = new();
    public RuleDelivery Delivery { get; init; } = new();
    public IReadOnlyList<RuleExclusion> Exclusions { get; init; } = [];
}
