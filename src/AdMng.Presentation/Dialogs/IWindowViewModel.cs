using System.ComponentModel;
using AdMng.Core.Directory;
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
/// <item>subscribe to <see cref="CloseRequested"/>: close the window (modal windows set DialogResult to the argument);</item>
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

    /// <summary>Localized "session ended - reopen the window" while <see cref="IsSessionEnded"/>, else null.</summary>
    public string? SessionEndedMessage => IsSessionEnded ? Localizer[L.WindowSession.Ended] : null;

    protected DialogServices Services { get; }

    protected ILocalizer Localizer => Services.Localizer;

    protected ErrorTranslator Errors => Services.Errors;

    /// <summary>
    /// The session this window was opened in, while it is still the current session; null once it ended (or when the
    /// window was opened while disconnected). Never another session.
    /// </summary>
    protected IDirectorySession? Session => _openedIn is { } session && ReferenceEquals(session, Services.Sessions.Current) ? session : null;

    /// <summary>
    /// False for windows that never act in the directory session (e.g. the PowerShell history): they do not end with
    /// it and are reused after a reconnect.
    /// </summary>
    protected virtual bool UsesSession => true;

    /// <summary>Localized reason why <see cref="Session"/> is null: the session ended, or not connected.</summary>
    protected string NoSessionMessage => Localizer[_openedIn is null ? L.Errors.NotConnected : L.WindowSession.Ended];

    /// <summary><see cref="Session"/>, or an exception that <see cref="ErrorTranslator"/> explains.</summary>
    /// <exception cref="SessionEndedException">The session of the window ended.</exception>
    /// <exception cref="InvalidOperationException">The window was opened while disconnected.</exception>
    protected IDirectorySession RequireSession() =>
        Session ?? (_openedIn is null ? throw new InvalidOperationException("Not connected to a directory.") : throw new SessionEndedException());

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual Task<bool> CanCloseAsync() => Task.FromResult(true);

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Services.Localizer.CultureChanged -= OnLocalizerCultureChanged;
        Services.Sessions.SessionChanged -= OnSessionsChanged;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases subscriptions of the derived view model.</summary>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>Raises <see cref="CloseRequested"/>.</summary>
    protected void RequestClose(bool? dialogResult) => CloseRequested?.Invoke(this, dialogResult);

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

        IsSessionEnded = true;
        OnPropertyChanged(nameof(IsSessionEnded));
        OnPropertyChanged(nameof(SessionEndedMessage));
        OnSessionEnded();
    });
}
