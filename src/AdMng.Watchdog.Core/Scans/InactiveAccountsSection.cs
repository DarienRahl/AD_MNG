namespace AdMng.Watchdog.Core.Scans;

/// <summary>One group of inactive accounts: the total count and the first rows (oldest logon first, never first).</summary>
/// <param name="Count">Number of matching accounts.</param>
/// <param name="ThresholdDays">The inactivity threshold of this group (Days or PrivilegedDays).</param>
/// <param name="Rows">At most <c>MaxRowsPerSection</c> rows.</param>
public sealed record InactiveAccountsSection(int Count, int ThresholdDays, IReadOnlyList<InactiveAccount> Rows)
{
    /// <summary>An empty section.</summary>
    public static InactiveAccountsSection Empty(int thresholdDays) => new(0, thresholdDays, []);
}
