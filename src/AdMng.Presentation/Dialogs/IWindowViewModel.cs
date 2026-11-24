using System.ComponentModel;
using AdMng.Core.Directory;
using AdMng.Core.Schema;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Dialogs;

/// <summary>
/// Contract of a view model hosted in its own window (property sheet, batch editor, pickers, Find, session log,
/// settings...). The WPF host treats all of them the same way:
/// <list type="number">
/// <item>create the view model through <see cref="IDialogViewModelFactory"/>, set it as the window's DataContext and
/// bind the title to <see cref="WindowTitle"/>;</item>
/// <item>subscribe to <see cref="CloseRequested"/>: close the window (modal windows set DialogResult to the argument) -
/// the view model has decided this close, the host asks nothing;</item>
/// <item>when the operator closes the window with the title bar button / Alt+F4, cancel the close, await
/// <see cref="CanCloseAsync"/> and close only when it returns true;</item>
/// <item>after the window is shown call <see cref="InitializeAsync"/> (it never throws; failures are shown by the view model);</item>
/// <item>dispose the view model when the window has closed (unsubscribes from application-wide events).</item>
/// </list>
/// </summary>
public interface IWindowViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Localized window title (raises PropertyChanged when the language changes).</summary>
    string WindowTitle { get; }

    /// <summary>The view model asks the host to close the window; the argument is the dialog result.</summary>
    event EventHandler<bool?>? CloseRequested;

    /// <summary>Loads what the window shows. Never throws; safe to call fire-and-forget.</summary>
    Task InitializeAsync();

    /// <summary>
    /// Called before the operator closes the window (title bar / Alt+F4). Returns false to keep it open, e.g. when
    /// the operator declined to discard pending changes.
    /// </summary>
    Task<bool> CanCloseAsync();
}

/// <summary>
/// Base class of window view models: title refresh on language change, close request, disposal, and the directory
/// session the window belongs to.
/// </summary>
/// <remarks>
/// A window is pinned to the session that was current when it was opened (<see cref="Session"/>). When that session
/// ends - disconnect, or a new connection, possibly with another identity - the window does not follow the new
/// session: <see cref="IsSessionEnded"/> becomes true, <see cref="OnSessionEnded"/> lets the window disable its
/// commands, and reads and writes are refused. Otherwise a property sheet opened by a helpdesk operator would apply
/// its changes as the administrator who connected next. Reopening the window uses the current session.
/// </remarks>
public abstract class WindowViewModelBase : ObservableObject, IWindowViewModel
{
    private readonly IDirectorySession? _openedIn;

    /// <summary>The DC of the session that replaced the window's session by "Change domain controller", else null.</summary>
    private string? _movedTo;

    /// <summary>The close question that is open or waiting for a stop: a second close joins it (one question, one wait).</summary>
    private Task<bool>? _closing;

    /// <summary>
    /// The executions a close looked at (D-A): told once the window closed, so that the connection says how those that ended
    /// in the window ended - its line goes with it (<see cref="Feedback.IWindowRun.WindowClosed"/>).
    /// </summary>
    private readonly List<Feedback.IWindowRun> _reportOnClose = [];

    protected WindowViewModelBase(DialogServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        _openedIn = services.Sessions.Current;
        Services.Localizer.CultureChanged += OnLocalizerCultureChanged;
        Services.Sessions.SessionChanged += OnSessionsChanged;
    }

    public event EventHandler<bool?>? CloseRequested;

    public abstract string WindowTitle { get; }

    /// <summary>True after <see cref="Dispose()"/> (the window closed).</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// The session this window was opened in has ended: the window no longer reads or writes (Apply, Execute, Restore...
    /// are disabled) and must be reopened. False for a window opened while disconnected.
    /// </summary>
    public bool IsSessionEnded { get; private set; }

    /// <summary>
    /// Localized "session ended - reopen the window" while <see cref="IsSessionEnded"/> (after "Change domain controller":
    /// the window belongs to the previous DC), else null.
    /// </summary>
    public string? SessionEndedMessage => IsSessionEnded ? EndedMessage : null;

    /// <summary>
    /// <see cref="SessionEndedMessage"/> as the state of the window that lasts (track V, stage V2b): what a window with a
    /// status line hands to <see cref="Feedback.PageStatusHolder.SetLasting"/> - first among its conditions - once its
    /// session ended; null while the session lasts. An error that cannot be closed: nothing in the window can be read
    /// or written any more.
    /// </summary>
    protected Feedback.PageStatus? SessionEndedStatus => SessionEndedMessage is { } message
        ? Feedback.PageStatus.Error(message, new Feedback.PageStatusLog(L.WindowSession.Ended), canClose: false)
        : null;

    protected DialogServices Services { get; }

    protected ILocalizer Localizer => Services.Localizer;

    protected ErrorTranslator Errors => Services.Errors;

    /// <summary>
    /// The session this window was opened in, while it is still the current session; null once it ended (or when the
    /// window was opened while disconnected). Never another session.
    /// </summary>
    protected IDirectorySession? Session => _openedIn is { } session && ReferenceEquals(session, Services.Sessions.Current) ? session : null;

    /// <summary>
    /// Schema of the session this window was opened in, also after that session ended: in-memory metadata used to
    /// recognize secret attributes when exporting what the window already shows. Never used to talk to a DC.
    /// </summary>
    protected ISchemaCatalog? OpenedSessionSchema => _openedIn?.Schema;

    /// <summary>
    /// The domain of the session this window was opened in - the only session it ever writes to - for the sentence of a
    /// destructive confirmation (empty for a window opened while disconnected).
    /// </summary>
    protected string ConfirmationDomain => _openedIn is { } session ? Connections.ConfirmationText.Domain(session) : string.Empty;

    /// <summary>
    /// False for windows that never act in the directory session (e.g. the PowerShell history): they do not end with
    /// it and are reused after a reconnect.
    /// </summary>
    protected virtual bool UsesSession => true;

    /// <summary>Localized reason why <see cref="Session"/> is null: the session ended, or not connected.</summary>
    protected string NoSessionMessage => _openedIn is null ? Localizer[L.Errors.NotConnected] : EndedMessage;

    private string EndedMessage => _movedTo is { } server && _openedIn is { } openedIn
        ? Localizer.Format(L.WindowSession.EndedOnPreviousDc, openedIn.Info.Server, server)
        : Localizer[L.WindowSession.Ended];

    /// <summary><see cref="Session"/>, or an exception that <see cref="ErrorTranslator"/> explains.</summary>
    /// <exception cref="SessionEndedException">The session of the window ended.</exception>
    /// <exception cref="InvalidOperationException">The window was opened while disconnected.</exception>
    protected IDirectorySession RequireSession() =>
        Session ?? (_openedIn is null ? throw new InvalidOperationException("Not connected to a directory.") : throw new SessionEndedException());

    public virtual Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// The window is about to be closed by the operator. First the one question of every window that works
    /// (<see cref="Feedback.RunningWork"/>, track V stage V2b-5b and the owner's decision D-A): an operation that writes and
    /// can be stopped is asked about - Stop and close, Close and continue in the background (the execution of a plan that
    /// may go on without the window), Don't close - and the window closes after a stopped operation ended, or at once when the
    /// execution left it; one that cannot be stopped keeps the window open and says so; a read is cancelled without a
    /// question. The window's own questions (<see cref="CanCloseCoreAsync"/>: unsaved changes, generated passwords) come
    /// with it. A second close while the first one asks or waits - the title bar, then the Close button - joins it. A window that
    /// closes after an execution ended in it - stopped, or its result came back under the question - hands the connection's
    /// report of it on once it has closed (<see cref="Dispose()"/>).
    /// </summary>
    public Task<bool> CanCloseAsync()
    {
        if (_closing is { IsCompleted: false } pending)
        {
            return pending;
        }

        var closing = AskCloseAsync();
        _closing = closing.IsCompleted ? null : closing;
        return closing;
    }

    private async Task<bool> AskCloseAsync()
    {
        try
        {
            var closes = await Feedback.RunningWork.ConfirmCloseAsync(
                Services.Localizer, Services.Dialogs, WindowTitle, Feedback.RunningWork.HoldersOf(this), ownQuestions: CanCloseCoreAsync,
                reportOnClose: _reportOnClose).ConfigureAwait(true);
            if (closes && IsDisposed)
            {
                // Closed by force while the question waited (the owner window closed): the runs are told now.
                TellRunsTheWindowClosed();
            }

            return closes;
        }
        finally
        {
            // Kept, or allowed: the next close asks again.
            _closing = null;
        }
    }

    /// <summary>
    /// The window's own questions before it closes (pending changes, generated passwords, "not while it is busy"), asked
    /// after the question about a running operation. True when the window may close.
    /// </summary>
    protected virtual Task<bool> CanCloseCoreAsync() => Task.FromResult(true);

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;

        // A close that was not asked (the owner window closed, the program ends): an execution that still runs leaves the
        // window - whatever would have kept it - so that the connection says how it ended; nothing of the window is touched
        // by it any more. One whose result came back already, while the window's code finishes (it reads its list again),
        // cannot leave: the connection says how it ended all the same, as the line goes with the window.
        foreach (var operation in Feedback.RunningWork.RunningWrites(Feedback.RunningWork.HoldersOf(this)))
        {
            if (operation.Run is { } run && !run.LeaveWindow())
            {
                run.ReportByConnection();
                if (!_reportOnClose.Contains(run))
                {
                    _reportOnClose.Add(run);
                }
            }
        }

        // The window is gone now: what the connection says of the executions that ended in it comes after it, never owned by it.
        TellRunsTheWindowClosed();

        Services.Localizer.CultureChanged -= OnLocalizerCultureChanged;
        Services.Sessions.SessionChanged -= OnSessionsChanged;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases subscriptions of the derived view model.</summary>
    protected virtual void Dispose(bool disposing)
    {
    }

    private void TellRunsTheWindowClosed()
    {
        var runs = _reportOnClose.ToList();
        _reportOnClose.Clear();
        foreach (var run in runs)
        {
            run.WindowClosed();
        }
    }

    /// <summary>
    /// Raises <see cref="CloseRequested"/>: the window closes at once. For a close that follows from what the window
    /// did - a choice was made, Apply succeeded. The host does not ask <see cref="CanCloseAsync"/> for it, so a Close or
    /// Cancel command of the operator must not come here directly: see <see cref="CloseByOperatorAsync"/>.
    /// </summary>
    protected void RequestClose(bool? dialogResult) => CloseRequested?.Invoke(this, dialogResult);

    /// <summary>
    /// The operator closes the window with a control of its own - the Close or Cancel button, Esc: the same close as
    /// the title bar's. <see cref="CanCloseAsync"/> is asked first (the question about a running operation that writes,
    /// then the window's own questions), and the window closes only when it allows it. A command that raised
    /// <see cref="CloseRequested"/> directly closed over a running restore, send or remote command without a word,
    /// while the title bar of the same window asked.
    /// </summary>
    /// <param name="dialogResult">The dialog result of the close.</param>
    protected async Task CloseByOperatorAsync(bool? dialogResult)
    {
        if (!IsDisposed && await CanCloseAsync().ConfigureAwait(true) && !IsDisposed)
        {
            RequestClose(dialogResult);
        }
    }

    /// <summary>Called after the UI language changed (on the thread that changed it). Refreshes the title by default.</summary>
    protected virtual void OnCultureChanged()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(SessionEndedMessage));
    }

    /// <summary>
    /// Called once on the UI thread after the session of the window ended (<see cref="IsSessionEnded"/> is already
    /// true): stop running reads, show <see cref="SessionEndedMessage"/> and refresh the commands that write.
    /// </summary>
    protected virtual void OnSessionEnded()
    {
    }

    private void OnLocalizerCultureChanged(object? sender, EventArgs e) => OnCultureChanged();

    private void OnSessionsChanged(object? sender, EventArgs e) => _ = Services.Dispatcher.RunAsync(() =>
    {
        if (IsDisposed || IsSessionEnded || !UsesSession || _openedIn is null || ReferenceEquals(_openedIn, Services.Sessions.Current))
        {
            return;
        }

        var current = Services.Sessions.Current;
        _movedTo = DomainControllerSwitch.IsSwitch(_openedIn, current) ? current!.Info.Server : null;
        IsSessionEnded = true;
        OnPropertyChanged(nameof(IsSessionEnded));
        OnPropertyChanged(nameof(SessionEndedMessage));
        OnSessionEnded();
    });
}
