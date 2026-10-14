using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Core.Tests.Attribution.Fakes;

/// <summary>A fixed lockout observation window.</summary>
internal sealed class FixedLockoutPolicy(TimeSpan window) : ILockoutPolicySource
{
    public Task<TimeSpan> GetObservationWindowAsync(ObjectRef account, CancellationToken cancellationToken) => Task.FromResult(window);
}
