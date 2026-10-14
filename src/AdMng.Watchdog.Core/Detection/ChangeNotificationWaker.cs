using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Optional change-notification wake-up (section 10.16): <c>WatchAsync</c> on up to four
/// <c>Directory.WakeUpContainers</c> (AD allows five per connection) raising <see cref="WatchdogSignal.PollRequested"/>.
/// Re-subscribed when the session or the configured containers change; never a source of events – the RootDSE check
/// every PollInterval stays the reliable trigger.
/// </summary>
public sealed class ChangeNotificationWaker : IAsyncDisposable
{
    /// <summary>At most this many subscriptions (AD allows five notification requests per connection).</summary>
    public const int MaxContainers = 4;

    private readonly IWatchdogSignals _signals;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IAsyncDisposable> _subscriptions = [];
    private IDirectorySession? _session;
    private string _key = string.Empty;

    public ChangeNotificationWaker(IWatchdogSignals signals)
    {
        _signals = signals;
    }

    /// <summary>Number of active subscriptions.</summary>
    public int ActiveSubscriptions => _subscriptions.Count;

    /// <summary>Makes the subscriptions match <paramref name="containers"/> on <paramref name="session"/>.</summary>
    public async Task EnsureAsync(IDirectorySession session, IReadOnlyList<string> containers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(containers);
        var wanted = containers.Where(c => DistinguishedName.TryParse(c, out var dn) && !dn.IsRoot).Take(MaxContainers).ToList();
        var key = string.Join('\n', wanted.Select(ValueCanonicalizer.NormalizeDn));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(session, _session) && key == _key)
            {
                return;
            }

            await DisposeSubscriptionsAsync().ConfigureAwait(false);
            _session = session;
            _key = key;
            foreach (var container in wanted)
            {
                try
                {
                    if (await session.WatchAsync(container, _ => _signals.Raise(WatchdogSignal.PollRequested), cancellationToken).ConfigureAwait(false) is { } subscription)
                    {
                        _subscriptions.Add(subscription);
                    }
                }
                catch (DirectoryException ex) when (!ex.IsTransient)
                {
                    // A container that cannot be watched only loses the early wake-up.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeSubscriptionsAsync().ConfigureAwait(false);
            _session = null;
            _key = string.Empty;
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private async Task DisposeSubscriptionsAsync()
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        _subscriptions.Clear();
    }
}
