using System.Collections.Concurrent;
using System.Net;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Testing;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Testing.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.Connections;

/// <summary>
/// The Presentation part of the application's composition with one DI scope per connection window (what
/// <c>ConnectionWindowManager</c> does in the WPF app): the UI services are scoped doubles, the directory is a set of
/// fake domains. Each <see cref="CreateConnection"/> is one window.
/// </summary>
internal sealed class ConnectionScopeHost : IAsyncDisposable
{
    private readonly TempAppPaths _paths = new();
    private readonly List<TestConnection> _connections = [];

    public ConnectionScopeHost(Action<IServiceCollection>? configure = null, IEnumerable<FakeDirectorySession>? domains = null)
    {
        Connector = new MultiDomainConnector(domains ?? [new FakeDirectoryBuilder().Build()]);
        Services = new ServiceCollection();
        Register(Services, _paths, Connector, Windows);
        configure?.Invoke(Services);
        Provider = Services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public IServiceCollection Services { get; }

    public ServiceProvider Provider { get; }

    public MultiDomainConnector Connector { get; }

    public FakeConnectionWindows Windows { get; } = new();

    public ConnectionRegistry Registry => Provider.GetRequiredService<ConnectionRegistry>();

    /// <summary>
    /// The host side of the composition (what AppHost registers before <c>AddAdMngPresentation</c>): app-wide singletons and,
    /// per connection window, the UI service doubles - scoped, so two windows never share a dialog service.
    /// </summary>
    public static void Register(IServiceCollection services, IAppPaths paths, IDirectoryConnector connector, IConnectionWindows? windows = null)
    {
        services.AddSingleton<IUiDispatcher, SynchronousUiDispatcher>();
        services.AddSingleton(paths);
        services.AddSingleton<IThemeService, Dialogs.FakeThemeService>();
        services.AddSingleton(connector);
        services.AddSingleton<ICsvCodec, SimpleCsvCodec>();
        services.AddAttributeFormatter(_ => new PassThroughAttributeFormatter());
        services.AddSingleton<IWatchdogClient>(new FakeWatchdogClient());
        if (windows is not null)
        {
            services.AddSingleton(windows);
        }

        services.AddScoped<ScriptedDialogService>();
        services.AddScoped<IDialogService>(sp => sp.GetRequiredService<ScriptedDialogService>());
        services.AddScoped<IFileDialogService, FakeFileDialogService>();
        services.AddScoped<IClipboardService, FakeClipboard>();
        services.AddScoped<RecordingNotificationService>();
        services.AddScoped<INotificationService>(sp => sp.GetRequiredService<RecordingNotificationService>());
        services.AddAdMngPresentation();
    }

    /// <summary>A new connection window (scope), not connected yet.</summary>
    public TestConnection CreateConnection()
    {
        var connection = new TestConnection(Provider.CreateAsyncScope());
        _connections.Add(connection);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await Provider.DisposeAsync();
        _paths.Dispose();
    }
}

/// <summary>One connection window of <see cref="ConnectionScopeHost"/>: its scope and the services tests look at.</summary>
internal sealed class TestConnection(AsyncServiceScope scope) : IAsyncDisposable
{
    private bool _disposed;

    public IServiceProvider Services => scope.ServiceProvider;

    public ConnectionContext Context => Get<ConnectionContext>();

    public ISessionManager Sessions => Get<ISessionManager>();

    public ScriptedDialogService Dialogs => Get<ScriptedDialogService>();

    public RecordingNotificationService Notifications => Get<RecordingNotificationService>();

    public bool IsDisposed => _disposed;

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Connects the window to <paramref name="domain"/> as <paramref name="user"/> (the bound identity of the fake).</summary>
    public async Task<IDirectorySession> ConnectAsync(string domain = FakeDirectoryBuilder.DefaultDomain, string? user = null)
    {
        _ = Context;
        return await Sessions.ConnectAsync(
            new ConnectionOptions { Domain = domain, UserName = user, Authentication = user is null ? AuthenticationMode.CurrentUser : AuthenticationMode.Credentials },
            user is null ? null : new NetworkCredential(user, "Hasło-tylko-w-pamięci"),
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await scope.DisposeAsync();
    }
}

/// <summary>
/// Connector over several fake domains. Every connect returns a NON-owning session on the domain's fake (closing a
/// window never disposes the directory another window, or the next connect, uses), with the requested account as bound
/// identity. "Change domain controller" returns another non-owning session on the same fake.
/// </summary>
internal sealed class MultiDomainConnector : IDirectoryConnector
{
    private readonly Dictionary<string, FakeDirectorySession> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<FakeDomainControllerSession> _opened = new();

    public MultiDomainConnector(IEnumerable<FakeDirectorySession> domains)
    {
        foreach (var domain in domains)
        {
            _domains[domain.Info.DomainDnsName] = domain;
        }

        Default = _domains.Values.First();
    }

    public FakeDirectorySession Default { get; }

    /// <summary>Every session handed out, in order.</summary>
    public IReadOnlyList<FakeDomainControllerSession> Opened => [.. _opened];

    /// <summary>When set, binds wait for it (a slow DC) and ignore cancellation, like a native bind.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public FakeDirectorySession Domain(string dnsName) => _domains[dnsName];

    public string? GetComputerDomain() => Default.Info.DomainDnsName;

    public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DomainControllerInfo>>(
        [
            new($"dc01.{domain}", "Warszawa", true, false, true),
            new($"dc02.{domain}", "Kraków", true, false, false),
        ]);

    public async Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        var directory = options.Domain is { } name && _domains.TryGetValue(name, out var found) ? found : Default;
        var user = credential?.UserName ?? options.UserName;
        var session = new FakeDomainControllerSession(
            directory,
            options.Server ?? directory.Info.Server,
            info => user is null ? info : info with { BoundIdentity = $@"{info.DomainNetBiosName}\{user}" });
        _opened.Enqueue(session);
        return session;
    }

    public async Task<IDirectorySession> ConnectToServerAsync(IDirectorySession current, string server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        var directory = current is FakeDomainControllerSession wrapper ? wrapper.Directory : Default;
        var identity = current.Info.BoundIdentity;
        var session = new FakeDomainControllerSession(directory, server, info => info with { BoundIdentity = identity });
        _opened.Enqueue(session);
        return session;
    }
}

/// <summary><see cref="IConnectionWindows"/> double: records what the shell asked for.</summary>
internal sealed class FakeConnectionWindows : IConnectionWindows
{
    private readonly List<ConnectionWindowInfo> _windows = [];

    public int NewConnectionRequests { get; private set; }

    public List<Guid> Activated { get; } = [];

    public IReadOnlyList<ConnectionWindowInfo> OpenWindows => [.. _windows];

    public event EventHandler? WindowsChanged;

    public void OpenNewConnection() => NewConnectionRequests++;

    public void Activate(Guid connectionId) => Activated.Add(connectionId);

    /// <summary>Sets the open windows (as the window manager would after opening or closing one).</summary>
    public void SetWindows(params IConnectionHandle[] windows)
    {
        _windows.Clear();
        _windows.AddRange(windows.Select(w => new ConnectionWindowInfo(w.Id, w.Identity)));
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }
}
