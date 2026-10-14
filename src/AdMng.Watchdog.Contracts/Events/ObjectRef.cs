namespace AdMng.Watchdog.Contracts.Events;

/// <summary>The directory object an event is about (state at detection time).</summary>
public sealed record ObjectRef
{
    public Guid? ObjectGuid { get; init; }
    public required string Dn { get; init; }
    public string? Name { get; init; }               // RDN value
    public string? DisplayName { get; init; }
    public string? SamAccountName { get; init; }
    public string? Sid { get; init; }
    public WatchObjectKind Kind { get; init; }
}

/// <summary>A security principal (actor, ambiguous candidate, API caller).</summary>
public sealed record PrincipalRef
{
    public string? Sid { get; init; }
    public string? Account { get; init; }            // DOMAIN\name
    public string? DisplayName { get; init; }
    public string? Dn { get; init; }
}
