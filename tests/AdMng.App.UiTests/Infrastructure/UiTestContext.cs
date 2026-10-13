using System.IO;
using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Core.Directory;
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
internal sealed class UiTestContext : IAsyncDisposable
{
    private readonly string _settingsDirectory;
    private readonly List<Exception> _exceptions = [];
    private readonly GlobalExceptionHandler _exceptionHandler;

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
        _exceptionHandler = host.Services.GetRequiredService<GlobalExceptionHandler>();
        _exceptionHandler.Recorder = _exceptions.Add;
        _exceptionHandler.Attach(Application.Current
            ?? throw new InvalidOperationException("The shared WPF application object is missing (was it shut down by a test?)."));
    }

    public IHost Host { get; }

    public IServiceProvider Services => Host.Services;

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
    public Task<IDirectorySession> ConnectAsync() =>
        Services.GetRequiredService<ISessionManager>()
            .ConnectAsync(new ConnectionOptions { Domain = FakeDirectoryBuilder.DefaultDomain }, null, CancellationToken.None);

    /// <summary>No WPF binding errors and no unhandled exceptions so far.</summary>
    public void AssertHealthy()
    {
        Assert.Equal([], WpfTestHost.Bindings.Errors);
        Assert.Equal([], _exceptions.Select(e => e.ToString()));
    }

    public async ValueTask DisposeAsync()
    {
        UiTestSupport.CloseAllWindows();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        _exceptionHandler.Detach();
        Services.GetRequiredService<AppearanceService>().Stop();
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
