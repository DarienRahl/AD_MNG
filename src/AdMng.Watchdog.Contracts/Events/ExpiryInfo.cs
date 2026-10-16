namespace AdMng.Watchdog.Contracts.Events;

public enum ExpiryKind { Account, Password }

/// <summary>DaysLeft = calendar days (configured time zone) from today to the displayed last valid day
/// (<c>(local(expires) − 1 tick).Date</c>).</summary>
public sealed record ExpiryInfo(ExpiryKind Kind, DateTimeOffset ExpiresAt, DateOnly LastValidDay, int DaysLeft, int ThresholdDays);
