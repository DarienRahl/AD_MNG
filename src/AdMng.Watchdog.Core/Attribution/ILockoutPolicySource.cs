using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>The effective lockout observation window of an account (section 11.4 #6b – the PSO's
/// <c>msDS-LockoutObservationWindow</c>, else the domain's <c>lockOutObservationWindow</c>). Implemented by the detection
/// side's <c>LockoutObservationWindowSource</c> over <c>LockoutPolicyResolver</c>.</summary>
public interface ILockoutPolicySource
{
    /// <summary>The observation window for <paramref name="account"/>.</summary>
    Task<TimeSpan> GetObservationWindowAsync(ObjectRef account, CancellationToken cancellationToken);
}
