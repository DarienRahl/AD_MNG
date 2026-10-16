namespace AdMng.Watchdog.Core.Model;

/// <summary>One value of a group's member list (from an Extended DN value).</summary>
public sealed record MemberReference
{
    /// <summary>Stable key: GUID "N" when known, else "sid:S-1-…", else "dn:&lt;normalized dn&gt;".</summary>
    public required string Key { get; init; }
    public Guid? ObjectGuid { get; init; }
    public string? Sid { get; init; }
    public required string Dn { get; init; }
}
