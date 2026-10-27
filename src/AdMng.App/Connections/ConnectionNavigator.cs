using System.Windows;
using AdMng.App.Views;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdMng.App.Connections;

/// <summary>
/// The window flow of ONE connection window (scoped; design note docs/dev/multi-window.md 2.1, 3.1–3.3): its connect
/// window → (successful bind) → its main window; "Rozłącz" shows the connect window again over the main window. It is
/// the only code that resolves this scope's <see cref="Views.MainWindow"/> and <see cref="Views.ConnectWindow"/>. The
/// <see cref="ConnectionWindowManager"/> listens to <see cref="ConnectCancelled"/> and <see cref="MainWindowClosed"/>
/// and closes the scope; nothing here shuts the application down.
/// </summary>
public sealed class ConnectionNavigator
{
    private readonly IServiceProvider _services;
    private readonly ConnectionContext _connection;
    private readonly ConnectionRegistry _registry;
    private readonly ILocalizer _localizer;
    private readonly ILogger<ConnectionNavigator> _logger;
    private ConnectWindow? _connect;

    public ConnectionNavigator(
        IServiceProvider services,
        ConnectionContext connection,
        ConnectionRegistry registry,
        ILocalizer localizer,
        ILogger<ConnectionNavigator> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The connect window was closed before the first connection (the window never opened).</summary>
    public event EventHandler? ConnectCancelled;

    /// <summary>The main window was shown (first successful connection).</summary>
    public event EventHandler? MainWindowShown;

    /// <summary>The main window closed: the connection window ends.</summary>
    public event EventHandler? MainWindowClosed;

    /// <summary>The main window once the first connection succeeded.</summary>
    public MainWindow? MainWindow { get; private set; }

    /// <summary>The open connect window, if any.</summary>
    public ConnectWindow? ConnectWindow => _connect;

    /// <summary>
    /// The connect that was still running when the connect window closed (completed when none was): its bind holds the
    /// credential typed into that window, so the scope is disposed only after it ended.
    /// </summary>
    public Task PendingConnect { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Shows (or activates) the connect window: owned by the main window after "Rozłącz", unowned and centred on the
    /// screen for a new connection window (owned by the window it was opened from, closing that window would kill it).
    /// </summary>
    public void ShowConnect()
    {
        if (_connect is { } open)
        {
            open.Activate();
            return;
        }

        var window = _services.GetRequiredService<ConnectWindow>();
        window.Connected += OnConnected;
        window.Closed += OnConnectClosed;
        if (MainWindow is { IsVisible: true } main)
        {
            window.Owner = main;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (_registry.All.Any(c => c.Id != _connection.Id))
            {
                // Other connection windows are open: say that this one is a new connection.
                window.Title = _localizer.Format(L.Connections.NewConnectionTitle, _localizer[L.App.Title]);
            }
        }

        _connect = window;
        window.Show();
        window.Activate();
    }

    /// <summary>Closes the pending connect window (the application exits, or "Zakończ program").</summary>
    public void CloseConnectWindow() => _connect?.Close();

    private async void OnConnected(object? sender, EventArgs e)
    {
        var connect = sender as ConnectWindow;
        try
        {
            if (MainWindow is null)
            {
                var main = _services.GetRequiredService<MainWindow>();
                MainWindow = main;
                main.Closed += OnMainClosed;
                main.Show();
                CloseConnect(connect);
                MainWindowShown?.Invoke(this, EventArgs.Empty);
                await main.Shell.InitializeAsync().ConfigureAwait(true);
            }
            else
            {
                // The shell re-initializes itself on SessionChanged (tree, first tab).
                CloseConnect(connect);
                MainWindow.Activate();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Opening the main window failed ({ErrorType}).", ex.GetType().Name);
            var errors = _services.GetRequiredService<ErrorTranslator>();
            await _services.GetRequiredService<IDialogService>()
                .ShowErrorAsync(_localizer[L.Errors.Title], errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    private void CloseConnect(ConnectWindow? window)
    {
        if (window is null)
        {
            return;
        }

        window.Closed -= OnConnectClosed;
        window.Connected -= OnConnected;
        if (ReferenceEquals(_connect, window))
        {
            _connect = null;
        }

        window.Close();
    }

    private void OnConnectClosed(object? sender, EventArgs e)
    {
        if (sender is ConnectWindow window)
        {
            window.Connected -= OnConnected;
            window.Closed -= OnConnectClosed;
            PendingConnect = window.ViewModel.ConnectCommand.ExecutionTask ?? Task.CompletedTask;
        }

        _connect = null;
        if (MainWindow is null)
        {
            // Closed before the first connection: this connection window never opened.
            ConnectCancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnMainClosed(object? sender, EventArgs e)
    {
        if (sender is Window main)
        {
            main.Closed -= OnMainClosed;
        }

        _connect?.Close();
        MainWindowClosed?.Invoke(this, EventArgs.Empty);
    }
}
