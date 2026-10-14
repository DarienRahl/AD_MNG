namespace AdMng.Watchdog.Testing.Stores;

/// <summary>One row of the <c>digest_runs</c> table of <see cref="InMemoryWatchdogStore"/>.</summary>
internal sealed record StoredDigestRun(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset CreatedAt,
    int EventCount,
    IReadOnlyList<long> OutboxIds);
