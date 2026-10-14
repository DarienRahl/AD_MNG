namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>The daily digest (section 13.4).</summary>
public sealed record DigestSettings
{
    public bool Enabled { get; init; } = true;
    public TimeOnly Time { get; init; } = new(7, 30);
    public IReadOnlyList<DayOfWeek> Days { get; init; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<string> RecipientGroupIds { get; init; } = ["it-admins"];
    public IReadOnlyList<string> ChannelIds { get; init; } = [];
    public bool SendWhenEmpty { get; init; } = true;            // heartbeat
    public int MaxRowsPerSection { get; init; } = 50;
    public DayOfWeek WeeklySectionsDay { get; init; } = DayOfWeek.Monday;
    public int UpcomingExpiryDays { get; init; } = 14;
    /// <summary>The first business day after non-business days (weekend, holidays) covers the whole period since the
    /// last business-day digest; the digests of non-business days are still sent (heartbeat).</summary>
    public bool CoverNonBusinessDays { get; init; } = true;
}
