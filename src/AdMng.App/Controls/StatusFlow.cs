using AdMng.Presentation.Feedback;

namespace AdMng.App.Controls;

/// <summary>
/// The one clock of the status line (track V, stage V2b-5b): a one-shot timer on the UI thread. It is an interface so
/// that a test can replace it and make the delay and the throttle of <see cref="StatusFlow"/> deterministic.
/// </summary>
internal interface IStatusScheduler
{
    /// <summary>Runs <paramref name="callback"/> once, on the UI thread, after <paramref name="delay"/>.</summary>
    /// <returns>A handle; disposing it before the time has come cancels the call (disposing it afterwards does nothing).</returns>
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>
/// When what a page says reaches the screen and the screen reader (design note docs/dev/v-interface-renewal.md, "V2b-5b
/// as built"): the logic of <c>StatusLine</c> that has to do with time, without a single WPF type, so a test
/// drives it with a manual clock. The page's status is the model and is never delayed - the holder says "Working" at
/// once - only what the line <em>shows</em> is.
/// </summary>
/// <remarks>
/// <para><b>The delay before "Working"</b> (owner's decision 5): a status that is a running operation, arriving while
/// the line shows anything else, is held back for <c>Motion.WorkingDelay</c> (about 150 ms); the line keeps showing
/// what it showed (the summary, or nothing - its band keeps its height either way). If something else arrives before
/// the delay has run out - the operation ended at once - the line goes straight to that and the working look never
/// flashes. What it ended as is a new message unless it is the summary: a result that is the same message as the one
/// the line shows (the same error after Retry, "Saved 1 object." twice) enters and is announced again, because it is
/// all the operator learns of an operation they did not see run. A running operation that is already shown is not held
/// back again by a new message or a new count (the stages of one operation), and a second running status that arrives
/// during the delay only replaces the first.</para>
/// <para><b>The announcement of a summary</b> (owner's decision 2): the summary of a page whose text changes while the
/// page stays as it is (a count of a search, a number of rows, a selection) is announced to a screen reader once, after
/// <c>Motion.SummaryThrottle</c> (about 800 ms) without a further change, so typing in a search box does not read out
/// every letter. Nothing is announced for the very first summary of a page, for a text the operator has heard already,
/// for an empty summary, or when <c>mayAnnounceSummary</c> says no (the page is not in front, the window is not
/// active). What the line says as a new message (a result, "Working" once it is shown, the summary after an operation)
/// is announced at once by the caller, as before; here such a message only cancels a summary that waits.</para>
/// </remarks>
internal sealed class StatusFlow
{
    private readonly Func<TimeSpan?> _workingDelay;
    private readonly Func<TimeSpan?> _summaryThrottle;
    private readonly Func<bool> _mayAnnounceSummary;
    private readonly Action<PageStatus, bool, bool> _show;
    private readonly Action _settleName;
    private readonly Action _announceSummary;

    private PageStatus _shown = PageStatus.Empty;
    private PageStatus? _waiting;
    private IDisposable? _delay;
    private IDisposable? _throttle;
    private string? _heard;

    /// <param name="scheduler">The clock.</param>
    /// <param name="workingDelay">The delay before "Working" is shown; null (the token is missing) or not positive: none.</param>
    /// <param name="summaryThrottle">The quiet time before a changed summary is announced; null or not positive: at once.</param>
    /// <param name="mayAnnounceSummary">Asked at the moment of the announcement: the page is in front and its window is active.</param>
    /// <param name="show">
    /// Puts a status on the screen: the status, whether it is a new message (another kind or another message key), and
    /// whether the name a screen reader reads follows it now - false while a changed summary waits for its quiet time, so
    /// that the name changes together with the announcement and not on every letter typed.
    /// </param>
    /// <param name="settleName">The name a screen reader reads follows what the line shows now.</param>
    /// <param name="announceSummary">Tells a screen reader that the summary changed.</param>
    public StatusFlow(
        IStatusScheduler scheduler,
        Func<TimeSpan?> workingDelay,
        Func<TimeSpan?> summaryThrottle,
        Func<bool> mayAnnounceSummary,
        Action<PageStatus, bool, bool> show,
        Action settleName,
        Action announceSummary)
    {
        Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _workingDelay = workingDelay ?? throw new ArgumentNullException(nameof(workingDelay));
        _summaryThrottle = summaryThrottle ?? throw new ArgumentNullException(nameof(summaryThrottle));
        _mayAnnounceSummary = mayAnnounceSummary ?? throw new ArgumentNullException(nameof(mayAnnounceSummary));
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _settleName = settleName ?? throw new ArgumentNullException(nameof(settleName));
        _announceSummary = announceSummary ?? throw new ArgumentNullException(nameof(announceSummary));
    }

    /// <summary>The clock; replaced by tests, before the first status.</summary>
    public IStatusScheduler Scheduler { get; set; }

    /// <summary>What the line shows now (<see cref="PageStatus.Empty"/> until the first status).</summary>
    public PageStatus Shown => _shown;

    /// <summary>A running operation is waiting for its delay to run out; the line still shows something else.</summary>
    public bool IsWaiting => _waiting is not null;

    /// <summary>A changed summary waits for its quiet time to run out.</summary>
    public bool IsAnnouncing => _throttle is not null;

    /// <summary>The page says something else (or the same again): the line takes it now or after the delay.</summary>
    public void Set(PageStatus? status)
    {
        status ??= PageStatus.Empty;
        if (_waiting is not null)
        {
            if (status.IsWorking)
            {
                // Still the same operation, a new text or count: the delay runs on from its first moment.
                _waiting = status;
                return;
            }

            // The operation ended before it was shown: straight to what it ended as - a new message even when it is the
            // one on the line, unless it is the summary (which stays quiet, see the remarks).
            CancelDelay();
            Present(status, endsUnseenOperation: true);
            return;
        }

        if (status.IsWorking && !_shown.IsWorking && _workingDelay() is { } delay && delay > TimeSpan.Zero)
        {
            _waiting = status;
            _delay = Scheduler.Schedule(delay, OnDelayElapsed);
            return;
        }

        Present(status);
    }

    /// <summary>Cancels what waits (the delay, the quiet time); the line keeps what it shows.</summary>
    public void Cancel()
    {
        CancelDelay();
        CancelThrottle();
    }

    private void OnDelayElapsed()
    {
        var status = _waiting;
        _waiting = null;
        _delay = null;
        if (status is not null)
        {
            Present(status);
        }
    }

    private void CancelDelay()
    {
        _delay?.Dispose();
        _delay = null;
        _waiting = null;
    }

    private void CancelThrottle()
    {
        _throttle?.Dispose();
        _throttle = null;
    }

    /// <summary>Shows a status (the delay is over or there was none) and decides what the screen reader hears.</summary>
    /// <param name="status">What the line shows now.</param>
    /// <param name="endsUnseenOperation">
    /// The status ends an operation whose working look was never shown: unless it is the summary it is a new message, also
    /// when it is the same message as the one shown - nothing else would tell the operator that the operation ran.
    /// </param>
    private void Present(PageStatus status, bool endsUnseenOperation = false)
    {
        var previous = _shown;
        _shown = status;
        if (!status.IsSameMessageAs(previous) || (endsUnseenOperation && !status.IsIdle))
        {
            // A new message is announced by the caller, at once. A summary that waited for its quiet time would now say
            // what the line says since a moment, so it is dropped; what the operator hears now is the baseline.
            _show(status, true, true);
            CancelThrottle();
            if (status.IsIdle && status.Message.Length > 0)
            {
                _heard = status.Message;
            }

            return;
        }

        if (!status.IsIdle || string.Equals(status.Message, previous.Message, StringComparison.Ordinal))
        {
            // The same message with a new count (a running operation), or nothing changed.
            _show(status, false, true);
            return;
        }

        PresentSummary(status);
    }

    /// <summary>The summary of the page changed its text, the page is the same: shown at once, told after a quiet time.</summary>
    private void PresentSummary(PageStatus status)
    {
        var text = status.Message;
        if (_heard is null)
        {
            // The very first summary of the page: it is what the page says when it opens, not a change.
            if (text.Length > 0)
            {
                _heard = text;
            }

            _show(status, false, true);
            return;
        }

        if (text.Length == 0 || string.Equals(text, _heard, StringComparison.Ordinal))
        {
            // Nothing to say, or back to what was heard: whatever waits has nothing new to tell.
            CancelThrottle();
            _show(status, false, true);
            return;
        }

        CancelThrottle();
        if (_summaryThrottle() is { } quiet && quiet > TimeSpan.Zero)
        {
            _show(status, false, false);
            _throttle = Scheduler.Schedule(quiet, OnThrottleElapsed);
        }
        else
        {
            _show(status, false, true);
            Announce();
        }
    }

    private void OnThrottleElapsed()
    {
        _throttle = null;
        if (!_shown.IsIdle || _shown.Message.Length == 0)
        {
            _settleName();
            return;
        }

        Announce();
    }

    /// <summary>One announcement for the last text of the summary.</summary>
    private void Announce()
    {
        if (!_shown.IsIdle || _shown.Message.Length == 0 || string.Equals(_shown.Message, _heard, StringComparison.Ordinal))
        {
            return;
        }

        // The text is consumed whether or not it is told: a page that is not in front or a window that is not active
        // announces nothing, and the same text does not come back as news later.
        _heard = _shown.Message;
        _settleName();
        if (_mayAnnounceSummary())
        {
            _announceSummary();
        }
    }
}
