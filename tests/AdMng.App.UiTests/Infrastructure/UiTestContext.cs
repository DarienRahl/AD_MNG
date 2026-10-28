using System.IO;
using System.Windows;
using AdMng.App.Connections;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Directory;
using AdMng.Core.Messaging;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// The real application host for one UI test: every service as in production except the directory connector (the
/// in-memory sample company of <see cref="FakeDirectoryBuilder"/>), SYSVOL (an empty <see cref="FakeSysvolFileReader"/>,
/// so that no test reaches \\dc01.contoso.test over the network; a test replaces it with its own; the Group Policy views
/// write to <see cref="Sysvol"/>) and the settings folder (a temporary directory); the computer remote client, tool
/// launcher, ping and DNS resolver are fakes (K1), so are Outlook, its signatures and the U1 Outlook transport (U5).
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
        string settingsDirectory,
        FakeSysvol sysvol)
    {
        Host = host;
        Sysvol = sysvol;
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

    /// <summary>
    /// The writable SYSVOL of the Group Policy views (K2): the GPO folders of the fake domain controller (empty until a
    /// test adds them, e.g. with <see cref="FakeDirectoryBuilder.AddGroupPolicyManagementSample"/>).
    /// </summary>
    public FakeSysvol Sysvol { get; }

    public SampleCompany Company { get; }

    public FakeDirectoryConnector Connector { get; }

    /// <summary>Shutdown requests (closing the main window, or the connect window before connecting).</summary>
    public RecordingShutdown Shutdown { get; }

    public IReadOnlyList<Exception> Exceptions => _exceptions;

    /// <summary>The in-memory Outlook of Korespondencja (U5), unless the test registered another client.</summary>
    public FakeOutlookClient? Outlook => Host.Services.GetService<IOutlookClient>() as FakeOutlookClient;

    /// <param name="language">UI language ("en" / "pl").</param>
    /// <param name="configure">Further replacements of services (e.g. a scripted dialog service).</param>
    public static async Task<UiTestContext> CreateAsync(string language = "en", Action<IServiceCollection>? configure = null)
    {
        var directory = new FakeDirectoryBuilder();
        var company = directory.AddSampleCompany();
        var connector = new FakeDirectoryConnector(directory.Build());
        var shutdown = new RecordingShutdown();
        var sysvol = new FakeSysvol();
        var settingsDirectory = Path.Combine(Path.GetTempPath(), "AdMng-uitests-" + Guid.NewGuid().ToString("N"));
        var options = new StartupOptions(Language: language, SettingsDirectory: settingsDirectory);
        var host = AppHost.Build(options, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDirectoryConnector>(connector));
            services.Replace(ServiceDescriptor.Singleton<IAppShutdown>(shutdown));
            services.Replace(ServiceDescriptor.Singleton<Core.GroupPolicy.ISysvolFileReader>(new FakeSysvolFileReader()));
            services.Replace(ServiceDescriptor.Singleton<Core.GroupPolicy.ISysvolFileWriter>(sysvol));
            configure?.Invoke(services);

            // Computers (K1): no test reaches a computer over WinRM, DNS or ICMP or starts mstsc/mmc - fakes, unless the
            // test registered its own instance above.
            UseFake<Core.Computers.Remote.IRemoteComputerClient>(services, new FakeRemoteComputerClient());
            UseFake<Core.Computers.Remote.ILocalToolLauncher>(services, new FakeLocalToolLauncher());
            UseFake<Core.Computers.Remote.IPingService>(services, new FakePingService());
            UseFake<Core.Computers.Remote.IHostAddressResolver>(services, new FakeHostAddressResolver());

            // Korespondencja (U5): no test starts or attaches to Outlook or reads the operator's signatures - an in-memory
            // Outlook (sample profile, every item recorded), one Polish signature and the U1 Outlook transport fake, unless
            // the test registered its own instance above.
            UseFake<IOutlookClient>(services, new FakeOutlookClient());
            UseFake<IOutlookSignatureSource>(services, new FakeOutlookSignatureSource());
            UseFake<IOutlookUserMessageTransportFactory>(services, new FakeOutlookUserMessageTransportFactory());
        });
        var context = new UiTestContext(host, directory, company, connector, shutdown, settingsDirectory, sysvol);
        await AppHost.InitializeAsync(host.Services, options);
        WpfTestHost.Bindings.Clear();
        return context;
    }

    /// <summary>Replaces the application's registration of <typeparamref name="T"/> with <paramref name="fake"/> unless a test registered an instance.</summary>
    private static void UseFake<T>(IServiceCollection services, T fake)
        where T : class
    {
        if (services.LastOrDefault(d => d.ServiceType == typeof(T)) is not { ImplementationInstance: not null })
        {
            services.Replace(ServiceDescriptor.Singleton(fake));
        }
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
    /// <remarks>
    /// The scope is not one of <see cref="ConnectionWindowManager"/>'s windows: it is in the <c>ConnectionRegistry</c> (the
    /// comparison window lists it as a target) but not in <c>IConnectionWindows.OpenWindows</c>, so the window switcher
    /// and the shell's "another connection window is open" checks (e.g. "Porównaj z obiektem w innym połączeniu…") do not
    /// see it. A test of those opens the window as the operator does: <see cref="ConnectionWindowManager.OpenNewConnection"/>
    /// and its connect window.
    /// </remarks>
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
