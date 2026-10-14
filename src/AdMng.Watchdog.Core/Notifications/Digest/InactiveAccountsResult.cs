namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>The inactive-accounts report (10.17): totals and the first rows (privileged accounts first, then the longest
/// inactive).</summary>
public sealed record InactiveAccountsResult
{
    public int Users { get; init; }

    public int Computers { get; init; }

    /// <summary>Privileged principals without a logon for <c>PrivilegedDays</c>.</summary>
    public int Privileged { get; init; }

    public IReadOnlyList<InactiveAccount> Rows { get; init; } = [];

    public int Total => Users + Computers;
}
