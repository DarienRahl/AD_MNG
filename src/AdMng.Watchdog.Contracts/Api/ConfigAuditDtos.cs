namespace AdMng.Watchdog.Contracts.Api;

/// <summary>One saved configuration change.</summary>
public sealed record ConfigAuditEntry
{
    public required long Id { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? BySid { get; init; }
    public required string ByAccount { get; init; }
    public required string Summary { get; init; }   // "rules: 2 changed (new-accounts, lockouts); channels: teams-it URL replaced"
    public long Version { get; init; }
}

public sealed record ConfigAuditPage
{
    public IReadOnlyList<ConfigAuditEntry> Items { get; init; } = [];
    public bool HasMore { get; init; }
}
