namespace AdMng.Watchdog.Core.Detection;

/// <summary>Effective lockout policy of an account (section 10.6): from its PSO when readable, else the domain head.</summary>
/// <param name="Duration">Lockout duration; null = until an administrator unlocks (0 / 0x8000000000000000) or unknown.</param>
/// <param name="ObservationWindow">Bad-password observation window; null when unknown.</param>
/// <param name="IsKnown">False when neither the PSO nor the domain policy could be read.</param>
/// <param name="FromPasswordSettings">True when the values come from the account's resultant PSO.</param>
public sealed record LockoutPolicy(TimeSpan? Duration, TimeSpan? ObservationWindow, bool IsKnown, bool FromPasswordSettings = false)
{
    /// <summary>Unreadable policy.</summary>
    public static LockoutPolicy Unknown { get; } = new(null, null, IsKnown: false);
}
