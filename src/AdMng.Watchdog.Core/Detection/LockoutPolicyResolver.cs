using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Effective lockout duration and observation window of an account (section 10.6, review A20): the account's PSO
/// (<c>msDS-ResultantPSO</c> base read → <c>msDS-LockoutDuration</c>/<c>msDS-LockoutObservationWindow</c>) when readable,
/// otherwise the domain head's <c>lockoutDuration</c>/<c>lockOutObservationWindow</c> (absolute values). The domain
/// policy is cached per session for <see cref="DomainPolicyLifetime"/>. Also used by the attribution's lockout-source
/// lookup (observation window).
/// </summary>
public sealed class LockoutPolicyResolver
{
    /// <summary>How long the domain policy is reused before it is read again.</summary>
    public static readonly TimeSpan DomainPolicyLifetime = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private (IDirectorySession Session, LockoutPolicy Policy, DateTimeOffset ReadAt)? _domain;

    public LockoutPolicyResolver(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>The effective policy of <paramref name="accountDn"/>. Directory errors fall back to the domain policy.</summary>
    public async Task<LockoutPolicy> ResolveAsync(IDirectorySession session, string accountDn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(accountDn);
        try
        {
            var account = await session.GetAsync(accountDn, [WatchdogAttributes.ResultantPso], cancellationToken).ConfigureAwait(false);
            if (account?.GetString(WatchdogAttributes.ResultantPso) is { } psoDn)
            {
                var pso = await session.GetAsync(psoDn, [WatchdogAttributes.PsoLockoutDuration, WatchdogAttributes.PsoLockoutObservationWindow], cancellationToken)
                    .ConfigureAwait(false);
                if (pso?.GetInt64(WatchdogAttributes.PsoLockoutDuration) is { } duration)
                {
                    return new LockoutPolicy(Interval(duration), Window(pso.GetInt64(WatchdogAttributes.PsoLockoutObservationWindow)), IsKnown: true, FromPasswordSettings: true);
                }
            }
        }
        catch (DirectoryException ex) when (!ex.IsTransient)
        {
            // PSO container unreadable (permission.passwordSettings): the domain duration applies to everyone.
        }

        return await GetDomainPolicyAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The domain head's lockout policy (cached per session).</summary>
    public async Task<LockoutPolicy> GetDomainPolicyAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_domain is { } cached && ReferenceEquals(cached.Session, session) && now - cached.ReadAt < DomainPolicyLifetime)
            {
                return cached.Policy;
            }
        }

        var head = await session.GetAsync(session.Info.DefaultNamingContext,
            [WatchdogAttributes.LockoutDuration, WatchdogAttributes.LockoutObservationWindow], cancellationToken).ConfigureAwait(false);
        var policy = head?.GetInt64(WatchdogAttributes.LockoutDuration) is { } duration
            ? new LockoutPolicy(Interval(duration), Window(head.GetInt64(WatchdogAttributes.LockoutObservationWindow)), IsKnown: true)
            : LockoutPolicy.Unknown;
        lock (_gate)
        {
            _domain = (session, policy, now);
        }

        return policy;
    }

    /// <summary>Forgets the cached domain policy (the domain head changed).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _domain = null;
        }
    }

    private static TimeSpan? Interval(long value) => AdConvert.FromInterval(value);

    private static TimeSpan? Window(long? value) => value is { } v ? AdConvert.FromInterval(v) : null;
}
