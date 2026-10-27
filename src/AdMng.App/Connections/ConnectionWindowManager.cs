using System.Windows;
using AdMng.App.Services;
using AdMng.App.Views;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdMng.App.Connections;

/// <summary>Shows the error of an unhandled exception, whose connection is unknown (<see cref="GlobalExceptionHandler"/>).</summary>
public interface IAppErrorPresenter
{
    /// <summary>Shows an application error dialog owned by the active connection window (no connection header).</summary>
    Task ShowErrorAsync(string title, string message);
}

/// <summary>Draws the operator's attention to a connection window whose dialog waits while another window is active.</summary>
public interface IConnectionAttention
{
    /// <summary>A dialog of <paramref name="waitingConnection"/> opened without focus while another connection's window is active.</summary>
    void DialogWaiting(Guid waitingConnection);
}

/// <summary>
/// The connection windows of the application (singleton; design note docs/dev/multi-window.md 2.1, 3): one DI scope per
/// connection window, created for "Nowe połączenie w nowym oknie…" and at start. A window's scope is closed when its
/// connect window is cancelled or its main window closes: its lifetime ends, its dialogs and toasts are closed, its
/// running activities are cancelled (bounded), its session is closed off the UI thread (bounded) and then the scope is
/// disposed. The application shuts down after the last connection window ended. Holds no credential and stores nothing.
/// </summary>
public sealed class ConnectionWindowManager : IConnectionWindows, IAppErrorPresenter, IConnectionAttention
{
    /// <summary>How long a closing window waits for its cancelled activities before it closes the session anyway.</summary>
    public static readonly TimeSpan ActivityTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a closing window waits for its session to close (a hanging unbind is abandoned).</summary>
    public static readonly TimeSpan SessionCloseTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly IAppShutdown _shutdown;
    private readonly ConnectionRegistry _registry;
    private readonly ILocalizer _localizer;
    private readonly ILogger<ConnectionWindowManager> _logger;
    private readonly List<ConnectionWindowEntry> _entries = [];
    private readonly HashSet<Guid> _showingError = [];
    private Guid? _lastActivated;
    private bool _shuttingDown;

    public ConnectionWindowManager(
        IServiceScopeFactory scopes,
        IAppShutdown shutdown,
        ConnectionRegistry registry,
        ILocalizer localizer,
        ILogger<ConnectionWindowManager> logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registry.ConnectionsChanged += (_, _) => WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? WindowsChanged;

    /// <summary>The open main windows (connected or not), in opening order.</summary>
    public IReadOnlyList<ConnectionWindowInfo> OpenWindows =>
        [.. _entries.Where(e => e.Navigator.MainWindow is not null && !e.IsClosing).Select(e => new ConnectionWindowInfo(e.Id, e.Context.Identity))];

    /// <summary>Connection windows (pending connect windows included) that have not ended yet.</summary>
    public int Count => _entries.Count;

    /// <summary>The application is shutting down (logoff, <see cref="Application.Shutdown()"/>): windows no longer start the close pipeline.</summary>
    public bool IsShuttingDown => _shuttingDown;

    /// <summary>Follows the application's end (Exit, Windows logoff): from then on <see cref="App.OnExit"/> closes what is left.</summary>
    public void Attach(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Exit += (_, _) => _shuttingDown = true;
        application.SessionEnding += (_, _) => _shuttingDown = true;
    }

    /// <summary>"Nowe połączenie w nowym oknie…": an unowned connect window in a new connection scope.</summary>
    public void OpenNewConnection()
    {
        if (_shuttingDown)
        {
            return;
        }

        var entry = CreateEntry();
        try
        {
            entry.Navigator.ShowConnect();
        }
        catch (Exception ex)
        {
            _logger.LogError("Opening a connect window failed ({ErrorType}).", ex.GetType().Name);
            _ = CloseEntryAsync(entry);
            throw;
        }
    }

    /// <summary>Brings the main window of the connection to the front (restored when minimized).</summary>
    public void Activate(Guid connectionId)
    {
        var window = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => ConnectionChrome.BelongsTo(w, connectionId))
            ?? (Window?)_entries.FirstOrDefault(e => e.Id == connectionId)?.Navigator.ConnectWindow;
        if (window is null)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>
    /// "Zakończ program": closes every connection window in turn, the others first and <paramref name="requester"/> last.
    /// Each asks its own questions (with its own domain); the first refusal stops the sequence.
    /// </summary>
    public async Task ExitAllAsync(MainWindow? requester)
    {
        var windows = (Application.Current?.Windows.OfType<MainWindow>() ?? [])
            .Where(w => w.IsVisible && !ReferenceEquals(w, requester))
            .Append(requester)
            .OfType<MainWindow>()
            .ToList();
        foreach (var window in windows)
        {
            if (!await window.TryCloseAsync().ConfigureAwait(true))
            {
                return;
            }
        }

        // Pending connect windows of connections that never opened.
        foreach (var entry in _entries.ToList())
        {
            entry.Navigator.CloseConnectWindow();
        }
    }

    /// <summary>
    /// <see cref="App.OnExit"/>: closes the sessions of every connection window that is still open, concurrently and
    /// bounded, so no LDAP connection (with its in-memory credential) outlives the process' UI. Deliberately does not
    /// dispose the scopes: that would dispose UI-bound view models off the UI thread, and the process is ending anyway.
    /// </summary>
    public void CloseLeftoverSessions(TimeSpan timeout)
    {
        _shuttingDown = true;
        var sessions = _entries.Select(e => e.Scope.ServiceProvider.GetService<ISessionManager>()).OfType<IAsyncDisposable>().ToList();
        _entries.Clear();
        if (sessions.Count == 0)
        {
            return;
        }

        var close = Task.WhenAll(sessions.Select(s => Task.Run(async () => await s.DisposeAsync().ConfigureAwait(false))));
        try
        {
            if (!close.Wait(timeout))
            {
                _logger.LogWarning("{Count} directory sessions did not close within the time limit at exit.", sessions.Count);
            }
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning("Closing directory sessions at exit failed ({ErrorType}).", ex.InnerException?.GetType().Name);
        }
    }

    /// <summary>
    /// The error dialog of an unhandled exception: owned by the active connection window (else the one activated last),
    /// without a connection header - the exception may come from another window's background work. One dialog per
    /// connection at a time; with no connection window at all, an unowned dialog.
    /// </summary>
    public async Task ShowErrorAsync(string title, string message)
    {
        var owner = ActiveConnection() ?? _lastActivated;
        var entry = owner is { } id ? _entries.FirstOrDefault(e => e.Id == id && !e.IsClosing) : null;
        var key = entry?.Id ?? Guid.Empty;
        if (!_showingError.Add(key))
        {
            _logger.LogWarning("An error dialog of this window is already open; the error is logged only.");
            return;
        }

        try
        {
            if (entry is not null)
            {
                await entry.Scope.ServiceProvider.GetRequiredService<DialogService>().ShowApplicationErrorAsync(title, message).ConfigureAwait(true);
            }
            else
            {
                ShowUnownedError(title, message);
            }
        }
        finally
        {
            _showingError.Remove(key);
        }
    }

    /// <summary>
    /// A dialog of <paramref name="waitingConnection"/> opened without focus: the active connection window gets a toast
    /// "Okno fabrikam.test czeka na odpowiedź" (the waiting window flashes in the taskbar).
    /// </summary>
    public void DialogWaiting(Guid waitingConnection)
    {
        if (ActiveConnection() is not { } active || active == waitingConnection
            || _entries.FirstOrDefault(e => e.Id == active && !e.IsClosing) is not { } entry)
        {
            return;
        }

        var label = _registry.Find(waitingConnection)?.Identity?.DomainDnsName ?? _localizer[L.Connections.NotConnected];
        entry.Scope.ServiceProvider.GetRequiredService<INotificationService>()
            .Show(NotificationSeverity.Warning, _localizer.Format(L.Connections.DialogWaiting, label));
    }

    /// <summary>The services (scope) of an open connection window, or null (tests look into a window's services).</summary>
    internal IServiceProvider? ServicesOf(Guid connectionId) =>
        _entries.FirstOrDefault(e => e.Id == connectionId && !e.IsClosing)?.Scope.ServiceProvider;

    /// <summary>A new connection scope with its context and navigator (used by <see cref="OpenNewConnection"/> and tests).</summary>
    internal ConnectionWindowEntry CreateEntry()
    {
        var scope = _scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ConnectionContext>();
        var navigator = scope.ServiceProvider.GetRequiredService<ConnectionNavigator>();
        var entry = new ConnectionWindowEntry(context.Id, scope, context, navigator);
        navigator.ConnectCancelled += (_, _) => _ = OnConnectCancelledAsync(entry);
        navigator.MainWindowShown += (_, _) =>
        {
            if (navigator.MainWindow is { } main)
            {
                main.Activated += (_, _) => _lastActivated = entry.Id;
            }

            _lastActivated = entry.Id;
            WindowsChanged?.Invoke(this, EventArgs.Empty);
        };
        navigator.MainWindowClosed += (_, _) => _ = CloseEntryAsync(entry);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>The connection of the active window, if it belongs to one.</summary>
    private static Guid? ActiveConnection() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) is { } active ? ConnectionChrome.GetConnectionId(active) : null;

    /// <summary>
    /// The connect window of a connection that never opened was closed: the bind still running (it holds the credential
    /// typed into that window) is cancelled and awaited, bounded, before the scope is disposed.
    /// </summary>
    private async Task OnConnectCancelledAsync(ConnectionWindowEntry entry)
    {
        entry.Context.EndLifetime();
        if (entry.Navigator.PendingConnect is { IsCompleted: false } connecting)
        {
            try
            {
                await connecting.WaitAsync(SessionCloseTimeout).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogWarning("A cancelled connect did not end within the time limit ({ErrorType}).", ex.GetType().Name);
            }
        }

        await CloseEntryAsync(entry).ConfigureAwait(true);
    }

    /// <summary>The close pipeline of one connection window (design note 3.4).</summary>
    private async Task CloseEntryAsync(ConnectionWindowEntry entry)
    {
        if (entry.IsClosing)
        {
            return;
        }

        entry.IsClosing = true;
        WindowsChanged?.Invoke(this, EventArgs.Empty);
        var services = entry.Scope.ServiceProvider;

        // From now on, leftover work of this connection finds a closed window: no planning, no dialogs, no toasts.
        entry.Context.EndLifetime();
        services.GetRequiredService<DialogService>().MarkClosed();
        services.GetRequiredService<NotificationService>().MarkClosed();
        if (_shuttingDown)
        {
            // The dispatcher is stopping: App.OnExit closes the sessions.
            return;
        }

        try
        {
            await services.GetRequiredService<ActivityCenterViewModel>().CancelRunningAsync().WaitAsync(ActivityTimeout).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Background operations of a closed window did not end within the time limit; they are abandoned.");
        }

        if (services.GetService<ISessionManager>() is IAsyncDisposable sessions)
        {
            try
            {
                await Task.Run(async () => await sessions.DisposeAsync().ConfigureAwait(false)).WaitAsync(SessionCloseTimeout).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("The directory session of a closed window did not close within the time limit; it is abandoned.");
            }
        }

        try
        {
            await entry.Scope.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Disposing a connection window failed ({ErrorType}).", ex.GetType().Name);
        }

        _entries.Remove(entry);
        _showingError.Remove(entry.Id);
        WindowsChanged?.Invoke(this, EventArgs.Empty);
        if (_entries.Count == 0 && !_shuttingDown)
        {
            _shutdown.Shutdown();
        }
    }

    private void ShowUnownedError(string title, string message)
    {
        var dialog = MessageDialogViewModel.Error(title, message, _localizer);
        var window = new MessageDialogWindow(dialog, new ScopelessClipboard()) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.ShowDialog();
    }

    /// <summary>Ctrl+C of an error dialog shown without any connection window (no scoped clipboard service exists then).</summary>
    private sealed class ScopelessClipboard : IClipboardService
    {
        public bool SetText(string text)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        }

        public bool SetSecret(string secret) => false;
    }
}

/// <summary>One connection window of <see cref="ConnectionWindowManager"/>: its scope, context and navigator.</summary>
internal sealed class ConnectionWindowEntry(Guid id, AsyncServiceScope scope, ConnectionContext context, ConnectionNavigator navigator)
{
    public Guid Id { get; } = id;

    public AsyncServiceScope Scope { get; } = scope;

    public ConnectionContext Context { get; } = context;

    public ConnectionNavigator Navigator { get; } = navigator;

    /// <summary>The close pipeline started.</summary>
    public bool IsClosing { get; set; }

    /// <summary>The id only.</summary>
    public override string ToString() => $"ConnectionWindowEntry({Id:N})";
}
