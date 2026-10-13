using System.Windows;
using AdMng.App.Services;
using AdMng.Presentation.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdMng.App;

/// <summary>
/// Application entry: parses the command line, builds the host (<see cref="AppHost"/>), applies settings, language
/// and theme, installs the global exception handler and opens the connect window. <c>--smoke-test</c> runs
/// <see cref="SmokeTest"/> instead and exits with its result.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private GlobalExceptionHandler? _exceptions;

    /// <summary>The running host (null before start-up completed and in the smoke test).</summary>
    public IServiceProvider? Services => _host?.Services;

    /// <summary>
    /// Whether start-up builds the host and opens the connect window (default). Hosts that only need the application
    /// object and its resources - the UI tests - set it to false before the dispatcher runs: WPF raises Startup from a
    /// dispatcher callback queued by the <see cref="Application"/> constructor even when <see cref="Application.Run()"/>
    /// is never called.
    /// </summary>
    public bool StartShell { get; init; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        if (!StartShell)
        {
            return;
        }

        var options = StartupOptions.Parse(e.Args);
        if (options.SmokeTest)
        {
            SmokeTest.Start(this, options);
            return;
        }

        Start(options);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exceptions?.Detach();
        if (_host is { } host)
        {
            _host = null;

            // Closes the LDAP sessions. Bounded, so a hanging DC can never keep the process alive.
            var stop = Task.Run(async () =>
            {
                await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (host is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    host.Dispose();
                }
            });
            try
            {
                _ = stop.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                // Best effort: a service that fails to dispose must not turn a normal exit into a crash.
            }
        }

        base.OnExit(e);
    }

    private async void Start(StartupOptions options)
    {
        try
        {
            _host = AppHost.Build(options);
            _exceptions = _host.Services.GetRequiredService<GlobalExceptionHandler>();
            _exceptions.Attach(this);
            await _host.StartAsync().ConfigureAwait(true);
            await AppHost.InitializeAsync(_host.Services, options).ConfigureAwait(true);
            _host.Services.GetRequiredService<AppNavigator>().ShowConnect();
        }
        catch (Exception ex)
        {
            // The host itself failed: no dialog service yet, fall back to a plain message box (still localized).
            var localizer = new ResxLocalizer(ResxLocalizer.ResolveCulture(options.Language));
            MessageBox.Show(
                localizer.Format(L.Ui.StartupFailed, ex.Message),
                localizer[L.App.Title],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
