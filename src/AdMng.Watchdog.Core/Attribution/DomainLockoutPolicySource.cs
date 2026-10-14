using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Domain-only <see cref="ILockoutPolicySource"/>: <c>lockOutObservationWindow</c> of the domain head (a negative
/// 100-ns interval), read at most once per <see cref="CacheDuration"/>; <see cref="DefaultWindow"/> (the AD default,
/// 30 minutes) when unreadable. PSO windows need the detection side's <c>LockoutPolicyResolver</c>.
/// </summary>
public sealed class DomainLockoutPolicySource(IDirectorySessionProvider sessions, TimeProvider timeProvider) : ILockoutPolicySource
{
    /// <summary>The AD default observation window.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(30);

    /// <summary>How long a read value is reused.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private static readonly string[] WindowAttribute = ["lockOutObservationWindow"];

    private (TimeSpan Window, DateTimeOffset ReadAt)? _cached;

    /// <inheritdoc/>
    public async Task<TimeSpan> GetObservationWindowAsync(ObjectRef account, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (_cached is { } cached && now - cached.ReadAt < CacheDuration)
        {
            return cached.Window;
        }

        var window = DefaultWindow;
        try
        {
            var session = await sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            var head = await session.GetAsync(session.Info.DefaultNamingContext, WindowAttribute, cancellationToken).ConfigureAwait(false);
            if (head?.GetInt64("lockOutObservationWindow") is { } raw && raw != 0 && AdConvert.FromInterval(raw) is { } interval && interval > TimeSpan.Zero)
            {
                window = interval;
            }
        }
        catch (DirectoryException)
        {
            // unreadable: default window, retried after the cache period
        }

        _cached = (window, now);
        return window;
    }
}
