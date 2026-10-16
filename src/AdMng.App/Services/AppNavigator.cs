using System.Windows;
using AdMng.App.Views;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdMng.App.Services;

/// <summary>
/// Window flow of the application: connect window → (successful bind) → main window; Disconnect shows the connect
/// window again over the (now empty) main window. Closing the connect window before the first connection, or closing
/// the main window, shuts the application down (<see cref="ShutdownMode.OnExplicitShutdown"/>).
/// </summary>
public sealed class AppNavigator
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AppNavigator> _logger;
    private readonly IAppShutdown _shutdown;
    private ConnectWindow? _connect;

    public AppNavigator(IServiceProvider services, ILogger<AppNavigator> logger, IAppShutdown shutdown)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
    }

    /// <summary>The main window once the first connection succeeded.</summary>
    public MainWindow? MainWindow { get; private set; }

    /// <summary>The open connect window, if any.</summary>
    public ConnectWindow? ConnectWindow => _connect;

    /// <summary>Shows (or activates) the connect window.</summary>
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
        }

        _connect = window;
        window.Show();
        window.Activate();
    }

    private async void OnConnected(object? sender, EventArgs e)
    {
        var connect = sender as ConnectWindow;
        try
        {
            if (MainWindow is null)
            {
                var main = _services.GetRequiredService<MainWindow>();
                MainWindow = main;
                if (Application.Current is { } application)
                {
                    application.MainWindow = main;
                }

                main.Closed += OnMainClosed;
                main.Show();
                CloseConnectWindow(connect);
                await main.Shell.InitializeAsync().ConfigureAwait(true);
            }
            else
            {
                // The shell re-initializes itself on SessionChanged (tree, first tab).
                CloseConnectWindow(connect);
                MainWindow.Activate();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Opening the main window failed ({ErrorType}).", ex.GetType().Name);
            var localizer = _services.GetRequiredService<ILocalizer>();
            var errors = _services.GetRequiredService<ErrorTranslator>();
            await _services.GetRequiredService<Presentation.Services.IDialogService>()
                .ShowErrorAsync(localizer[L.Errors.Title], errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    private void CloseConnectWindow(ConnectWindow? window)
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
        }

        _connect = null;
        if (MainWindow is null)
        {
            // Closed before the first connection: nothing else to show.
            _shutdown.Shutdown();
        }
    }

    private void OnMainClosed(object? sender, EventArgs e)
    {
        _connect?.Close();
        _shutdown.Shutdown();
    }
}
