using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Services;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Live updates of the Watchdog tab (D9, cursor polling with long-poll): while the tab is visible it repeats
/// <c>GetEventsAsync(after = cursor, wait = 25)</c> with the feed's filter and refreshes the status every 30 s; while the
/// tab is open in the background it only reads <c>/status</c> every 60 s (tab header badge). Failures back off
/// exponentially from 2 s to 60 s and are reported, never thrown. Events are raised on the UI thread. Stops on dispose.
/// </summary>
public sealed class WatchdogPoller : IDisposable
{
    /// <summary>Long-poll wait of one request (the service caps it at 30 s).</summary>
    public const int WaitSeconds = 25;

    /// <summary>Most events per poll answer.</summary>
    public const int PageLimit = 500;

    public static readonly TimeSpan VisibleStatusInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan HiddenStatusInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly IWatchdogClient _client;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _wake;
    private Task _completion = Task.CompletedTask;
    private bool _isVisible;
    private long? _cursor;
    private volatile bool _disposed;

    public WatchdogPoller(IWatchdogClient client, TimeProvider time, IUiDispatcher dispatcher)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>New events of the live feed (ascending ids, already filtered by the service).</summary>
    public event EventHandler<EventPage>? EventsReceived;

    /// <summary>A fresh <c>/status</c> answer (badge, rollout banner, status sub-tab).</summary>
    public event EventHandler<StatusDto>? StatusReceived;

    /// <summary>A poll failed; the poller retries after <see cref="CurrentBackoff"/>.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>A poll succeeded after failures.</summary>
    public event EventHandler? Recovered;

    /// <summary>Switches between live polling (visible) and the badge-only status refresh (hidden).</summary>
    public bool IsVisible
    {
        get => Volatile.Read(ref _isVisible);
        set
        {
            if (Volatile.Read(ref _isVisible) != value)
            {
                Volatile.Write(ref _isVisible, value);
                Wake();
            }
        }
    }

    /// <summary>The newest event id the feed knows; null = the feed is not loaded (no live polling yet).</summary>
    public long? Cursor
    {
        get
        {
            lock (_gate)
            {
                return _cursor;
            }
        }

        set
        {
            lock (_gate)
            {
                _cursor = value;
            }
        }
    }

    /// <summary>Filter of the live feed (paging keys are replaced by the poller).</summary>
    public Func<EventQuery>? Filter { get; set; }

    public bool IsRunning => !_completion.IsCompleted;

    /// <summary>Back-off before the next attempt (zero while polls succeed).</summary>
    public TimeSpan CurrentBackoff { get; private set; }

    /// <summary>The running loop (completes after <see cref="Stop"/>; tests await it).</summary>
    public Task Completion => _completion;

    /// <summary>Starts the loop (no-op when running).</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stop is not null)
            {
                return;
            }

            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _completion = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Stops the loop and cancels the running request.</summary>
    public void Stop()
    {
        CancellationTokenSource? stop;
        lock (_gate)
        {
            stop = _stop;
            _stop = null;
        }

        stop?.Cancel();
    }

    /// <summary>Cancels the current wait or long-poll so that the next round uses the current filter and visibility.</summary>
    public void Wake()
    {
        CancellationTokenSource? wake;
        lock (_gate)
        {
            wake = _wake;
        }

        try
        {
            wake?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The round ended meanwhile.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }

    private async Task RunAsync(CancellationToken stop)
    {
        DateTimeOffset? lastStatus = null;
        while (!stop.IsCancellationRequested)
        {
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_gate)
            {
                _wake = wake;
            }

            try
            {
                lastStatus = await RunRoundAsync(lastStatus, wake.Token).ConfigureAwait(false);
                if (CurrentBackoff > TimeSpan.Zero)
                {
                    CurrentBackoff = TimeSpan.Zero;
                    Raise(() => Recovered?.Invoke(this, EventArgs.Empty));
                }
            }
            catch (OperationCanceledException) when (wake.IsCancellationRequested)
            {
                // Woken (filter or visibility changed) or stopped.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                CurrentBackoff = CurrentBackoff == TimeSpan.Zero ? MinBackoff : TimeSpan.FromTicks(Math.Min(CurrentBackoff.Ticks * 2, MaxBackoff.Ticks));
                var failedAt = _time.GetUtcNow();
                Raise(() => Failed?.Invoke(this, ex));
                await DelayUntilAsync(failedAt + CurrentBackoff, wake.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _wake = null;
                }
            }
        }
    }

    /// <summary>One round: status when due, then a long-poll (visible) or a wait until the next status (hidden).</summary>
    private async Task<DateTimeOffset?> RunRoundAsync(DateTimeOffset? lastStatus, CancellationToken token)
    {
        var visible = IsVisible;
        var interval = visible ? VisibleStatusInterval : HiddenStatusInterval;
        if (lastStatus is null || _time.GetUtcNow() - lastStatus >= interval)
        {
            var status = await _client.GetStatusAsync(token).ConfigureAwait(false);
            lastStatus = _time.GetUtcNow();
            Raise(() => StatusReceived?.Invoke(this, status));
        }

        if (visible && Cursor is { } cursor && Filter is { } filter)
        {
            var query = filter() with { After = cursor, Before = null, WaitSeconds = WaitSeconds, Limit = PageLimit };
            var page = await _client.GetEventsAsync(query, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_cursor != cursor)
                {
                    // The feed was reloaded meanwhile: this answer belongs to the old filter.
                    return lastStatus;
                }

                var newest = page.Items.Count == 0 ? cursor : page.Items.Max(e => e.Id);
                _cursor = page.HasMore ? Math.Max(cursor, newest) : Math.Max(Math.Max(cursor, newest), page.LatestId);
            }

            if (page.Items.Count > 0)
            {
                Raise(() => EventsReceived?.Invoke(this, page));
            }

            return lastStatus;
        }

        await DelayUntilAsync(lastStatus.Value + interval, token).ConfigureAwait(false);
        return lastStatus;
    }

    private async Task DelayUntilAsync(DateTimeOffset due, CancellationToken token)
    {
        var remaining = due - _time.GetUtcNow();
        if (remaining > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(remaining, _time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Woken or stopped: the loop decides.
            }
        }
    }

    /// <summary>Raises an event on the UI thread unless the poller was disposed meanwhile.</summary>
    private void Raise(Action raise)
    {
        if (!_disposed)
        {
            _dispatcher.RunAsync(() =>
            {
                if (!_disposed)
                {
                    raise();
                }
            }).Forget();
        }
    }
}
