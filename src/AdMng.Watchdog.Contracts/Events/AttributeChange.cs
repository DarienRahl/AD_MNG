namespace AdMng.Watchdog.Contracts.Events;

public enum AttributeValueKind { Text, Number, Boolean, FileTime, GeneralizedTime, Dn, Sid, Guid, Flags, Hash, Binary }

/// <summary>Old → new values of one attribute.</summary>
public sealed record AttributeChange
{
    public required string Attribute { get; init; }
    public AttributeValueKind ValueKind { get; init; }
    /// <summary>Canonical values (section 9.4); DN values are display DNs. Empty when Redacted.</summary>
    public IReadOnlyList<string> OldValues { get; init; } = [];
    public IReadOnlyList<string> NewValues { get; init; } = [];
    /// <summary>Multi-valued attributes: values added/removed (computed by the differ).</summary>
    public IReadOnlyList<string> Added { get; init; } = [];
    public IReadOnlyList<string> Removed { get; init; } = [];
    /// <summary>userAccountControl / groupType / gPOptions flag names set and cleared.</summary>
    public IReadOnlyList<string> FlagsAdded { get; init; } = [];
    public IReadOnlyList<string> FlagsRemoved { get; init; } = [];
    /// <summary>Secret or confidential attribute: values are never stored or sent.</summary>
    public bool Redacted { get; init; }
    /// <summary>Values were capped (Directory.MaxValuesPerAttribute / 200-character cap).</summary>
    public bool Truncated { get; init; }
    public int OldCount { get; init; }
    public int NewCount { get; init; }
}
