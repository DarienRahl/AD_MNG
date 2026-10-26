using System.IO;
using System.Windows;
using AdMng.App.Connections;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Core.Directory;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// The real application host for one UI test: every service as in production except the directory connector (the
/// in-memory sample company of <see cref="FakeDirectoryBuilder"/>) and the settings folder (a temporary directory).
/// Unhandled UI exceptions are recorded instead of shown. Create and dispose it on the UI thread.
/// </summary>
/// <remarks>
/// Per-connection services live in a connection scope (design note docs/dev/multi-window.md 10.3): <see cref="Services"/>
/// is the scope of a default connection window (it also resolves the singletons), <see cref="Host"/>.Services the root.
/// A test that needs a second connection window creates it with <see cref="CreateConnection"/>. Every scope is disposed
/// before the host stops - a scope left open would leak its session into the next test.
/// </remarks>
internal sealed class UiTestContext : IAsyncDisposable
{
    private readonly string _settingsDirectory;
    private readonly List<Exception> _exceptions = [];
    private readonly GlobalExceptionHandler _exceptionHandler;
    private readonly AsyncServiceScope _defaultScope;
    private readonly List<AsyncServiceScope> _scopes = [];

    private UiTestContext(
        IHost host,
        FakeDirectoryBuilder directory,
        SampleCompany company,
        FakeDirectoryConnector connector,
        RecordingShutdown shutdown,
        string settingsDirectory)
    {
        Host = host;
        Shutdown = shutdown;
        Directory = directory;
        Company = company;
        Connector = connector;
        _settingsDirectory = settingsDirectory;
        _defaultScope = host.Services.CreateAsyncScope();
        _exceptionHandler = host.Services.GetRequiredService<GlobalExceptionHandler>();
        _exceptionHandler.Recorder = _exceptions.Add;
        _exceptionHandler.Attach(Application.Current
            ?? throw new InvalidOperationException("The shared WPF application object is missing (was it shut down by a test?)."));
    }

    public IHost Host { get; }

    /// <summary>The services of the default connection window (scope); singletons resolve here too.</summary>
    public IServiceProvider Services => _defaultScope.ServiceProvider;

    /// <summary>The connection context of the default connection window.</summary>
    public ConnectionContext Connection => Services.GetRequiredService<ConnectionContext>();

    public FakeDirectoryBuilder Directory { get; }

    public SampleCompany Company { get; }

    public FakeDirectoryConnector Connector { get; }

    /// <summary>Shutdown requests (closing the main window, or the connect window before connecting).</summary>
    public RecordingShutdown Shutdown { get; }

    public IReadOnlyList<Exception> Exceptions => _exceptions;

    /// <param name="language">UI language ("en" / "pl").</param>
    /// <param name="configure">Further replacements of services (e.g. a scripted dialog service).</param>
    public static async Task<UiTestContext> CreateAsync(string language = "en", Action<IServiceCollection>? configure = null)
    {
        var directory = new FakeDirectoryBuilder();
        var company = directory.AddSampleCompany();
        var connector = new FakeDirectoryConnector(directory.Build());
        var shutdown = new RecordingShutdown();
        var settingsDirectory = Path.Combine(Path.GetTempPath(), "AdMng-uitests-" + Guid.NewGuid().ToString("N"));
        var options = new StartupOptions(Language: language, SettingsDirectory: settingsDirectory);
        var host = AppHost.Build(options, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDirectoryConnector>(connector));
            services.Replace(ServiceDescriptor.Singleton<IAppShutdown>(shutdown));
            configure?.Invoke(services);
        });
        var context = new UiTestContext(host, directory, company, connector, shutdown, settingsDirectory);
        await AppHost.InitializeAsync(host.Services, options);
        WpfTestHost.Bindings.Clear();
        return context;
    }

    /// <summary>Connects the session manager to the sample company (as the connect window would).</summary>
    public Task<IDirectorySession> ConnectAsync()
    {
        _ = Connection;
        return Services.GetRequiredService<ISessionManager>()
            .ConnectAsync(new ConnectionOptions { Domain = FakeDirectoryBuilder.DefaultDomain }, null, CancellationToken.None);
    }

    /// <summary>
    /// Another connection window's scope (its own session, dialogs, toasts…), not connected yet; disposed with the context.
    /// Resolve its <c>MainWindow</c> at most once.
    /// </summary>
    public IServiceProvider CreateConnection()
    {
        var scope = Host.Services.CreateAsyncScope();
        _scopes.Add(scope);
        _ = scope.ServiceProvider.GetRequiredService<ConnectionContext>();
        return scope.ServiceProvider;
    }

    /// <summary>Connects a connection scope to <paramref name="domain"/> as <paramref name="user"/> (the fake's bound identity).</summary>
    public static Task<IDirectorySession> ConnectAsync(IServiceProvider connection, string domain, string? user = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _ = connection.GetRequiredService<ConnectionContext>();
        return connection.GetRequiredService<ISessionManager>().ConnectAsync(
            new ConnectionOptions { Domain = domain, UserName = user, Authentication = user is null ? AuthenticationMode.CurrentUser : AuthenticationMode.Credentials },
            user is null ? null : new System.Net.NetworkCredential(user, "Hasło-tylko-w-pamięci"),
            CancellationToken.None);
    }

    /// <summary>No WPF binding errors and no unhandled exceptions so far.</summary>
    public void AssertHealthy()
    {
        // Full texts in the failure message: CI logs are the only place where these can be read.
        var bindingErrors = WpfTestHost.Bindings.Errors;
        Assert.True(bindingErrors.Count == 0, "WPF binding errors:" + Environment.NewLine + string.Join(Environment.NewLine, bindingErrors));
        Assert.True(_exceptions.Count == 0, "Unhandled exceptions:" + Environment.NewLine + string.Join(Environment.NewLine, _exceptions));
    }

    public async ValueTask DisposeAsync()
    {
        UiTestSupport.CloseAllWindows();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        _exceptionHandler.Detach();
        Host.Services.GetRequiredService<AppearanceService>().Stop();

        // Connection windows the manager opened (and did not close yet), then the scopes of the test, default first.
        Host.Services.GetRequiredService<ConnectionWindowManager>().CloseLeftoverSessions(TimeSpan.FromSeconds(5));
        await _defaultScope.DisposeAsync();
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await Host.StopAsync(TimeSpan.FromSeconds(5));
        if (Host is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
        else
        {
            Host.Dispose();
        }

        try
        {
            System.IO.Directory.Delete(_settingsDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temporary folder; nothing to clean up if it was never created or is still locked.
        }
    }
}

/// <summary>All UI tests share one STA thread and run one after another.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiTestCollection
{
    public const string Name = "WPF UI";
}
