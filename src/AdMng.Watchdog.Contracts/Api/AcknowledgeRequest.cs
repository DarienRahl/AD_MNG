namespace AdMng.Watchdog.Contracts.Api;

public sealed record AcknowledgeRequest
{
    public string? Comment { get; init; }      // ≤ 500 chars
}

public sealed record BulkAcknowledgeRequest
{
    public IReadOnlyList<long> Ids { get; init; } = [];   // 1..500
    public string? Comment { get; init; }                  // ≤ 500 chars
}
