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
    private PageStatus _current = PageStatus.Empty;
    private OperationScope? _operation;

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

    /// <summary>What the page says right now.</summary>
    public PageStatus Current => _current;

    /// <summary>The summary of the page: shown whenever nothing else is.</summary>
    public PageStatus Idle => _idle;

    /// <summary>The running operation, or null.</summary>
    public OperationScope? Operation => _operation;

    /// <summary>True while an operation runs (also while it is stopping).</summary>
    public bool IsWorking => _current.IsWorking;

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
        if (_current.IsIdle)
        {
            SetCurrent(idle);
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

    /// <summary>Back to the summary: a result is closed, a running operation is over without a message.</summary>
    public void Clear() => Replace(_idle);

    /// <summary>The close button: only what the operator may close goes (a command runs also when it says it cannot).</summary>
    private void Close()
    {
        if (_current.CanClose)
        {
            Clear();
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
        SetCurrent(working);
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
        SetCurrent(scope.Working);
    }

    /// <summary>The running operation was asked to stop: the page says so at once. UI thread.</summary>
    internal void MarkCancelling(OperationScope scope)
    {
        if (!ReferenceEquals(scope, _operation) || !scope.MarkCancelling())
        {
            return;
        }

        SetCurrent(PageStatus.Working(_localizer[L.Feedback.Stopping], new PageStatusLog(L.Feedback.Stopping)));
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
        SetCurrent(result is null or { IsIdle: true } or { IsWorking: true } ? _idle : result);
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
        SetCurrent(status);
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

    private void SetCurrent(PageStatus status)
    {
        var previous = _current;
        if (ReferenceEquals(previous, status))
        {
            return;
        }

        _current = status;
        OnPropertyChanged(nameof(Current));
        if (previous.IsWorking != status.IsWorking)
        {
            OnPropertyChanged(nameof(IsWorking));
        }

        if (previous.CanClose != status.CanClose)
        {
            CloseCommand.NotifyCanExecuteChanged();
        }

        Write(previous, status);
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
