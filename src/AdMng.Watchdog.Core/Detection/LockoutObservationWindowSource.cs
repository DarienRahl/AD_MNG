using AdMng.Core.Directory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The attribution side's <see cref="ILockoutPolicySource"/> (lockout sources, section 11.4 #6b) over
/// <see cref="LockoutPolicyResolver"/>: the account's resultant PSO window when readable, else the domain head's
/// <c>lockOutObservationWindow</c>; <see cref="DefaultWindow"/> (the AD default) when neither can be read.
/// </summary>
public sealed class LockoutObservationWindowSource : ILockoutPolicySource
{
    /// <summary>The AD default observation window (used when the policy cannot be read).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(30);

    private readonly LockoutPolicyResolver _resolver;
    private readonly IDirectorySessionProvider _sessions;

    public LockoutObservationWindowSource(LockoutPolicyResolver resolver, IDirectorySessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(sessions);
        _resolver = resolver;
        _sessions = sessions;
    }

    /// <inheritdoc/>
    public async Task<TimeSpan> GetObservationWindowAsync(ObjectRef account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        try
        {
            var session = await _sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            var policy = string.IsNullOrEmpty(account.Dn)
                ? await _resolver.GetDomainPolicyAsync(session, cancellationToken).ConfigureAwait(false)
                : await _resolver.ResolveAsync(session, account.Dn, cancellationToken).ConfigureAwait(false);
            return policy.ObservationWindow is { } window && window > TimeSpan.Zero ? window : DefaultWindow;
        }
        catch (DirectoryException)
        {
            return DefaultWindow;
        }
    }
}
