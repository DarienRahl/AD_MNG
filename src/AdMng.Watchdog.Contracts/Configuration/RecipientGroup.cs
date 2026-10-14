namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>A named list of e-mail recipients referenced by rules and the digest.</summary>
public sealed record RecipientGroup
{
    public required string Id { get; init; }                 // [a-z0-9-]{1,40}
    public required string Name { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Full;
}
