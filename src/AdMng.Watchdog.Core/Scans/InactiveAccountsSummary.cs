namespace AdMng.Watchdog.Core.Scans;

/// <summary>The weekly "Nieaktywne konta" digest section (section 10.17): users, computers and privileged principals.</summary>
/// <param name="Users">Enabled users without a logon for <c>Days</c>.</param>
/// <param name="Computers">Enabled computers without a logon for <c>Days</c>.</param>
/// <param name="Privileged">Enabled privileged principals (PrivilegeState) without a logon for <c>PrivilegedDays</c>.</param>
/// <param name="GeneratedAt">When the live queries ran.</param>
public sealed record InactiveAccountsSummary(
    InactiveAccountsSection Users,
    InactiveAccountsSection Computers,
    InactiveAccountsSection Privileged,
    DateTimeOffset GeneratedAt);
