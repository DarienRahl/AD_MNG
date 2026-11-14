using System.ComponentModel;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Presentation.Feedback;

/// <summary>A page view model that says what it is doing through a status line of its own (design note 6.1).</summary>
public interface IHasPageStatus
{
    /// <summary>The status of the page; the view binds its status line to it.</summary>
    PageStatusHolder Status { get; }
}

/// <summary>
/// The status of one page (track V, stage V2; design note 4.5, 6.1 and 6.7): the one place where the page says what it
/// is doing and what happened. A page view model owns one holder and the view binds its status line to
/// <see cref="Current"/> - there is no hub: a status never leaves its page (what finished elsewhere is a toast, what
/// runs in the connection is the activity list).
/// </summary>
/// <remarks>
/// <para>The page shows one thing at a time: its summary (<see cref="SetIdle"/>), a running operation
/// (<see cref="Begin"/>) or a result (<see cref="Show"/>). Whatever speaks last wins. A second operation
/// <b>replaces</b> the running one - its token is cancelled and what it reports later is ignored - because a page that
/// loads (the object list) must follow the operator at once: a new location, F5 or a sort starts a new load and the
/// old one is of no interest. A page whose operation must not be interrupted keeps its command disabled while it runs;
/// that is the command's business, not the holder's.</para>
/// <para><b>A state that lasts</b> (<see cref="SetLasting"/>; stage V2b): "the object was deleted", "could not be
/// read", "the session of this window ended" are true for as long as the page shows what it shows. Such a state takes
/// the place of the summary: it is what the page says whenever nothing else is said, it cannot be closed, an operation
/// and its result cover it for their time, and it is back when the result is closed. It goes when the page says so
/// (<c>SetLasting(null)</c>), not when it was read.</para>
/// <para><b>Several sources in one window</b> (<see cref="Front"/>; stage V2b): a window whose pages (tabs) say
/// something of their own gives every page view model a holder and tells its own holder which page is in front. The
/// one status line of the window is bound to the window's holder, whose <see cref="Current"/> is then what the line
/// shows - the window's own status or the one of the page in front, see <see cref="Front"/> for the rule. Everything
/// else of a holder (<see cref="IsWorking"/>, <see cref="Operation"/>, <see cref="CancelCommand"/>…) is about its own
/// operation, also when it has a page in front.</para>
/// <para>Members are UI-thread members, like every public member of a view model. The operation handed out by
/// <see cref="Begin"/> is the exception: an <see cref="OperationScope"/> may be used from any thread.</para>
/// <para>Every new message that is not the summary is written to the diagnostic log as its value-free copy
/// (<see cref="PageStatus.Log"/>: message key, counts, result code - never the text on screen; CLAUDE.md rule 1).</para>
/// </remarks>
public sealed class PageStatusHolder : ObservableObject
{
    /// <summary>Event ids of the log copies: this number plus the kind.</summary>
    internal const int LogEventBase = 5200;

    private readonly ILocalizer _localizer;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private PageStatus _idle = PageStatus.Empty;
    private PageStatus? _lasting;

    // What this page itself says: its summary or lasting state, its operation or a result.
    private PageStatus _own = PageStatus.Empty;

    // What the status line shows: _own, or what the page in front says.
    private PageStatus _current = PageStatus.Empty;
    private OperationScope? _operation;
    private PageStatusHolder? _front;

    // What the page in front said last, and whether it said it after this page spoke.
    private PageStatus _frontSaid = PageStatus.Empty;
    private bool _frontSpokeLast;

    /// <param name="localizer">Texts of the holder's own words ("Stop", "Stopping…").</param>
    /// <param name="dispatcher">The UI thread (operations may report from worker threads).</param>
    /// <param name="logger">The diagnostic log; null writes nothing.</param>
    public PageStatusHolder(ILocalizer localizer, IUiDispatcher dispatcher, ILogger? logger = null)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? NullLogger.Instance;
        CloseCommand = new RelayCommand(Close, () => _current.CanClose);
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => _operation is { CanCancel: true, IsCancelling: false });
    }

    /// <summary>
    /// What the status line of the page shows right now: what the page itself says or, for a window with a page in
    /// front (<see cref="Front"/>), what that page says.
    /// </summary>
    public PageStatus Current => _current;

    /// <summary>The summary of the page: shown whenever nothing else is.</summary>
    public PageStatus Idle => _idle;

    /// <summary>The state of the page that lasts (<see cref="SetLasting"/>), or null: shown in place of the summary.</summary>
    public PageStatus? Lasting => _lasting;

    /// <summary>The running operation of this page, or null.</summary>
    public OperationScope? Operation => _operation;

    /// <summary>True while an operation of this page runs (also while it is stopping).</summary>
    public bool IsWorking => _own.IsWorking;

    /// <summary>What this page itself says, whatever the page in front says (equal to <see cref="Current"/> without one).</summary>
    internal PageStatus Own => _own;

    /// <summary>
    /// The page in front: the holder of the selected tab of a window (or of the part of a page the operator looks at),
    /// or null. The window sets it whenever the selection changes; its status line stays bound to its own holder.
    /// </summary>
    /// <remarks>
    /// What <see cref="Current"/> then is, in this order:
    /// <list type="number">
    /// <item>the window's own running operation, or its own error that cannot be closed (the object is gone, the session
    /// ended, nothing could be read) - then nothing a page says matters;</item>
    /// <item>otherwise whatever was said last by the window or by the page in front that is not a summary. A page says
    /// its status again when it comes to the front, so the operator always sees why the page in front of them is empty
    /// or stale; a result of the window that was said later covers it and is back under it when closed; a new count of
    /// the same message is not "saying it again";</item>
    /// <item>otherwise the summary of the page in front or, when it has none, the summary of the window.</item>
    /// </list>
    /// Nothing is lost by that: a status that is not shown stays in its holder and is shown when its turn comes - a tab
    /// that failed while it was not selected says so when it is selected. <see cref="CloseCommand"/> closes what is
    /// shown, in the holder that said it. A page in front may have a part in front of its own (tabs inside a tab).
    /// </remarks>
    /// <exception cref="ArgumentException">The holder would be in front of itself.</exception>
    public PageStatusHolder? Front
    {
        get => _front;
        set
        {
            if (ReferenceEquals(_front, value))
            {
                return;
            }

            for (var part = value; part is not null; part = part._front)
            {
                if (ReferenceEquals(part, this))
                {
                    throw new ArgumentException("A page cannot be in front of itself.", nameof(value));
                }
            }

            if (_front is not null)
            {
                _front.PropertyChanged -= OnFrontChanged;
            }

            _front = value;
            _frontSaid = value?.Current ?? PageStatus.Empty;

            // Coming to the front counts as saying it again.
            _frontSpokeLast = value is not null;
            if (value is not null)
            {
                value.PropertyChanged += OnFrontChanged;
            }

            OnPropertyChanged(nameof(Front));
            UpdateCurrent();
        }
    }

    /// <summary>True from the moment the running operation was asked to stop until it ended.</summary>
    public bool IsCancelling => _operation is { IsCancelling: true };

    /// <summary>Closes a result: the page shows its summary again. Not available while an operation runs.</summary>
    public IRelayCommand CloseCommand { get; }

    /// <summary>Stops the running operation (see <see cref="OperationScope.Cancel"/>). Available once per operation.</summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>Sets the summary of the page ("2 314 objects, 12 selected"). It is shown at once unless something else is.</summary>
    public void SetIdle(string? summary)
    {
        var idle = PageStatus.Idle(summary);
        if (string.Equals(idle.Message, _idle.Message, StringComparison.Ordinal))
        {
            return;
        }

        _idle = idle;
        OnPropertyChanged(nameof(Idle));
        if (_own.IsIdle)
        {
            SetOwn(idle);
        }
    }

    /// <summary>
    /// Sets the state of the page that lasts, or removes it (null): an "attention" or an "error" that is true for as
    /// long as the page shows what it shows ("the object was deleted", "could not be read: …", "the values are out of
    /// date"). It takes the place of the summary - the page says it whenever nothing else is said - and it cannot be
    /// closed, whatever <see cref="PageStatus.CanClose"/> of <paramref name="state"/> says.
    /// </summary>
    /// <remarks>
    /// A new state is shown at once, also in place of a result; a running operation keeps the line and the state is
    /// shown when the operation ended without a message, or when its result is closed. Setting the same state again
    /// (the same kind, text and action) changes nothing, so a page may compute its state anew whenever one of its
    /// conditions changes. A page with several conditions sets the most important one.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="state"/> is not an "attention" or an "error".</exception>
    public void SetLasting(PageStatus? state)
    {
        if (state is not null and not { Kind: PageStatusKind.Attention or PageStatusKind.Error })
        {
            throw new ArgumentException("A state that lasts is an attention or an error.", nameof(state));
        }

        var lasting = state?.AsLasting();
        var previous = _lasting;
        if (lasting is null ? previous is null : lasting.HasSameContentAs(previous))
        {
            return;
        }

        _lasting = lasting;
        OnPropertyChanged(nameof(Lasting));
        if (_operation is not null)
        {
            // The running operation keeps the line; how it ends decides what is shown then.
            return;
        }

        if (lasting is not null)
        {
            SetOwn(lasting);
        }
        else if (ReferenceEquals(_own, previous))
        {
            SetOwn(_idle);
        }
    }

    /// <summary>
    /// Shows a result (done, attention or error). A running operation is over with it: its token is cancelled and what
    /// it reports later is ignored.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="status"/> is a running operation: "working" is only shown through <see cref="Begin"/>, whose
    /// scope guarantees that it ends.
    /// </exception>
    public void Show(PageStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.IsWorking)
        {
            throw new ArgumentException("A running operation is shown with Begin, which guarantees that it ends.", nameof(status));
        }

        if (status.IsIdle)
        {
            // A summary is not a result: it becomes the summary of the page, and the page shows it.
            SetIdle(status.Message);
            Clear();
            return;
        }

        Replace(status);
    }

    /// <summary>
    /// Back to the summary (or to the state that lasts): a result is closed, a running operation is over without a
    /// message.
    /// </summary>
    public void Clear() => Replace(Rest);

    /// <summary>What the page says when nothing else is said: its state that lasts, else its summary.</summary>
    private PageStatus Rest => _lasting ?? _idle;

    /// <summary>
    /// The close button: only what the operator may close goes (a command runs also when it says it cannot), and it
    /// goes in the holder that said it - this one or the page in front.
    /// </summary>
    private void Close()
    {
        if (!_current.CanClose)
        {
            return;
        }

        if (ReferenceEquals(_current, _own))
        {
            Clear();
        }
        else
        {
            _front?.CloseCommand.Execute(null);
        }
    }

    /// <summary>
    /// Starts an operation of the page: the page says <paramref name="message"/> as "working" at once, with "Stop" as
    /// its action when the operation can be cancelled. A running operation is replaced (see the remarks of the class).
    /// The scope must be completed or disposed - put it in a <c>using</c>.
    /// </summary>
    /// <param name="message">The action and its scope ("Reading 2 314 accounts from Kraków…").</param>
    /// <param name="log">The value-free copy of the message.</param>
    /// <param name="progress">How far it is (indeterminate by default).</param>
    /// <param name="canCancel">False for an operation that cannot be stopped once it started.</param>
    public OperationScope Begin(string message, PageStatusLog log, PageProgress progress = default, bool canCancel = true)
    {
        var stop = canCancel ? new PageStatusAction(_localizer[L.Common.Stop], CancelCommand) : null;
        var working = PageStatus.Working(message, log, progress, stop);
        var previous = _operation;
        var scope = new OperationScope(this, working, canCancel);

        // The new operation is the page's before the old one hears that it was cancelled: whatever the old one says in
        // answer (it may answer inside Abandon, on this thread) is no longer the page's.
        _operation = scope;
        SetOwn(working);
        OperationChanged();
        previous?.Abandon();
        return scope;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread: at once when the caller is on it.</summary>
    internal void Post(Action action) => _ = _dispatcher.RunAsync(action);

    /// <summary>A running operation has something new to say. UI thread.</summary>
    internal void Report(OperationScope scope, string? message, PageProgress? progress, PageStatusLog? log)
    {
        // Not the page's operation any more, or already asked to stop: the line says "Stopping…" until it ended.
        if (!ReferenceEquals(scope, _operation) || scope.IsCancelling)
        {
            return;
        }

        scope.Working = scope.Working.Update(message, progress, log);
        SetOwn(scope.Working);
    }

    /// <summary>The running operation was asked to stop: the page says so at once. UI thread.</summary>
    internal void MarkCancelling(OperationScope scope)
    {
        if (!ReferenceEquals(scope, _operation) || !scope.MarkCancelling())
        {
            return;
        }

        SetOwn(PageStatus.Working(_localizer[L.Feedback.Stopping], new PageStatusLog(L.Feedback.Stopping)));
        OperationChanged();
    }

    /// <summary>The operation ended with <paramref name="result"/>, or without a message (null). UI thread.</summary>
    internal void Complete(OperationScope scope, PageStatus? result)
    {
        if (!ReferenceEquals(scope, _operation))
        {
            // Replaced, or completed before: the first result stands.
            return;
        }

        _operation = null;
        scope.MarkEnded();
        SetOwn(result is null or { IsIdle: true } or { IsWorking: true } ? Rest : result);
        OperationChanged();
    }

    /// <summary>
    /// Shows <paramref name="status"/> in place of whatever the page says. A running operation is taken off the page
    /// first and told afterwards: what it says in answer (it may answer inside Abandon, on this thread) is ignored.
    /// </summary>
    private void Replace(PageStatus status)
    {
        var previous = _operation;
        _operation = null;
        SetOwn(status);
        if (previous is not null)
        {
            OperationChanged();
            previous.Abandon();
        }
    }

    private void OperationChanged()
    {
        OnPropertyChanged(nameof(Operation));
        OnPropertyChanged(nameof(IsCancelling));
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The page itself says <paramref name="status"/> now.</summary>
    private void SetOwn(PageStatus status)
    {
        var previous = _own;
        if (ReferenceEquals(previous, status))
        {
            return;
        }

        _own = status;
        if (!status.IsSameMessageAs(previous))
        {
            // Something new - not a new count of the same message: the page spoke after the page in front.
            _frontSpokeLast = false;
        }

        UpdateCurrent();
        if (previous.IsWorking != status.IsWorking)
        {
            OnPropertyChanged(nameof(IsWorking));
        }

        Write(previous, status);
    }

    /// <summary>The page in front says something else.</summary>
    private void OnFrontChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Current) || sender is not PageStatusHolder front || !ReferenceEquals(front, _front))
        {
            return;
        }

        var said = front.Current;
        if (!said.IsIdle && !said.IsSameMessageAs(_frontSaid))
        {
            _frontSpokeLast = true;
        }

        _frontSaid = said;
        UpdateCurrent();
    }

    /// <summary>Shows what the line has to show now (see <see cref="Front"/> for the rule).</summary>
    private void UpdateCurrent()
    {
        var shown = Shown();
        var previous = _current;
        if (ReferenceEquals(previous, shown))
        {
            return;
        }

        _current = shown;
        OnPropertyChanged(nameof(Current));
        if (previous.CanClose != shown.CanClose)
        {
            CloseCommand.NotifyCanExecuteChanged();
        }
    }

    private PageStatus Shown()
    {
        if (_front is not { } part)
        {
            return _own;
        }

        // The window's own operation takes the line while it runs, and its own error that cannot be closed means that
        // nothing a page says matters.
        if (_own.IsWorking || _own is { Kind: PageStatusKind.Error, CanClose: false })
        {
            return _own;
        }

        var front = part.Current;
        var (first, second) = _frontSpokeLast ? (front, _own) : (_own, front);
        if (!first.IsIdle)
        {
            return first;
        }

        if (!second.IsIdle)
        {
            return second;
        }

        return front.Message.Length > 0 ? front : _own;
    }

    /// <summary>
    /// The value-free copy of a new message for the diagnostic log. The summary is not logged, nor is a running
    /// operation that only reports a new count (the same message). The message key is also the name of the event, so the
    /// problem report - which keeps templates and event names, never argument values - shows which message it was.
    /// </summary>
    private void Write(PageStatus previous, PageStatus status)
    {
        if (status.IsIdle || status.IsSameMessageAs(previous))
        {
            return;
        }

        var level = status.Kind switch
        {
            PageStatusKind.Error => LogLevel.Error,
            PageStatusKind.Attention => LogLevel.Warning,
            PageStatusKind.Done => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        var log = status.Log;
        _logger.Log(
            level,
            new EventId(LogEventBase + (int)status.Kind, log.Key),
            "Page status {Kind}: {MessageKey} ({Counts}) [{Code}]",
            status.Kind,
            log.Key,
            log.CountsText,
            log.Code.ToString());
    }
}
