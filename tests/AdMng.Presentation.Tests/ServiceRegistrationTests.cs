using AdMng.Core.Batch;
using AdMng.Presentation.Services;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Connections;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Views;
using AdMng.Presentation.Watchdog;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests;

/// <summary>
/// The Presentation registrations with one DI scope per connection window (design note docs/dev/multi-window.md 2.2/2.3):
/// what is per window is Scoped (and cannot be pre-empted by a host's earlier singleton), what is shared is one instance,
/// and everything resolves in its lifetime.
/// </summary>
public sealed class ServiceRegistrationTests
{
    private const string Jan = "CN=Jan Nowak,OU=IT,OU=Firma,DC=contoso,DC=test";

    [Fact]
    public async Task AddAdMngPresentation_ResolvesAllViewModels_WithinAConnectionScope()
    {
        await using var host = CreateHost();
        await using var window = host.CreateConnection();
        var services = window.Services;

        Assert.NotNull(services.GetRequiredService<ConnectViewModel>());
        var shell = services.GetRequiredService<ShellViewModel>();
        Assert.Same(shell, services.GetRequiredService<ShellViewModel>());
        Assert.Same(window.Context, shell.Connection);
        Assert.NotNull(services.GetRequiredService<NavigationTreeViewModel>());
        Assert.NotNull(services.GetRequiredService<ObjectListViewModel>());
        Assert.NotNull(services.GetRequiredService<SavedViewEditorViewModel>());
        Assert.Same(services.GetRequiredService<ObjectListServices>(), services.GetRequiredService<ObjectListServices>());

        // B2: window view models are created by the factory; parameterless ones also resolve directly.
        var factory = services.GetRequiredService<IDialogViewModelFactory>();
        Assert.IsType<DialogViewModelFactory>(factory);
        Assert.NotNull(services.GetRequiredService<SessionLogViewModel>());
        Assert.NotNull(services.GetRequiredService<PowerShellHistoryViewModel>());
        Assert.NotNull(services.GetRequiredService<SettingsViewModel>());
        Assert.NotNull(services.GetRequiredService<CsvImportViewModel>());
        Assert.NotNull(services.GetRequiredService<IRollbackPlanner>());
        Assert.NotNull(services.GetRequiredService<ICsvImportPlanner>());
        Assert.NotNull(services.GetRequiredService<PowerShellHistory>());
        Assert.IsType<RecordingChangePlanner>(services.GetRequiredService<IChangePlanner>());
        Assert.IsType<BatchExecutor>(services.GetRequiredService<IBatchExecutor>());
        Assert.Same(window.Context, services.GetRequiredService<DialogServices>().Connection);
        using var sheet = factory.CreatePropertySheet(Jan);
        using var editor = factory.CreateBatchEditor(new BatchEditorRequest([Jan]));
        using var reset = factory.CreateResetPassword([Jan]);
        using var newUser = factory.CreateNewObject(Core.Model.ObjectKind.User, "OU=IT,OU=Firma,DC=contoso,DC=test");
        using var find = factory.CreateFind(null);
        using var settings = factory.CreateSettings();
        using var recycleBin = factory.CreateRecycleBin();
        Assert.NotNull(services.GetRequiredService<RecycleBinViewModel>());
        Assert.All<IWindowViewModel>([sheet, editor, reset, newUser, find, settings, recycleBin], vm => Assert.False(string.IsNullOrEmpty(vm.WindowTitle)));

        // Watchdog tab: one service connection for the application; history launcher per window; a new document per call.
        Assert.Same(host.Provider.GetRequiredService<WatchdogConnectionService>(), services.GetRequiredService<WatchdogConnectionService>());
        Assert.Same(services.GetRequiredService<WatchdogObjectHistoryLauncher>(), sheet.HistoryLauncher);
        var watchdogFactory = services.GetRequiredService<Func<WatchdogDocumentViewModel>>();
        using var first = watchdogFactory();
        using var second = watchdogFactory();
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task PerConnectionServices_AreDeclaredScoped()
    {
        await using var host = CreateHost();

        // The descriptor itself: a singleton registered before AddAdMngPresentation would win over its TryAddScoped and
        // silently share one instance between windows, which ValidateScopes does not notice.
        Assert.All(ConnectionLifetimes.PerConnection, type =>
        {
            var descriptor = Assert.Single(host.Services, d => d.ServiceType == type);
            Assert.True(descriptor.Lifetime == ServiceLifetime.Scoped, $"{type.Name} is {descriptor.Lifetime}, not Scoped.");
        });
        Assert.All(ConnectionLifetimes.Shared, type =>
            Assert.Equal(ServiceLifetime.Singleton, Assert.Single(host.Services, d => d.ServiceType == type).Lifetime));
    }

    [Fact]
    public async Task TwoConnectionScopes_HaveTheirOwnPerConnectionServices_AndShareTheApplicationOnes()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();

        Assert.All(ConnectionLifetimes.PerConnection, type =>
        {
            var inA = a.Services.GetRequiredService(type);
            Assert.Same(inA, a.Services.GetRequiredService(type));
            Assert.NotSame(inA, b.Services.GetRequiredService(type));
        });
        Assert.All(ConnectionLifetimes.Shared, type =>
            Assert.Same(a.Services.GetRequiredService(type), b.Services.GetRequiredService(type)));
        Assert.NotEqual(a.Context.Id, b.Context.Id);
        Assert.Equal([a.Context.Id, b.Context.Id], host.Registry.All.Select(c => c.Id));
    }

    [Fact]
    public async Task PerConnectionServices_AreNeverResolvedFromTheRoot()
    {
        await using var host = CreateHost();

        Assert.Throws<InvalidOperationException>(() => host.Provider.GetRequiredService<ISessionManager>());
        Assert.Throws<InvalidOperationException>(() => host.Provider.GetRequiredService<ShellViewModel>());
        Assert.Throws<InvalidOperationException>(() => host.Provider.GetRequiredService<IChangeCoordinator>());
    }

    [Fact]
    public async Task EveryRegistration_ResolvesInItsLifetime()
    {
        await using var host = CreateHost();
        await using var window = host.CreateConnection();

        foreach (var descriptor in host.Services.Where(d => !d.ServiceType.IsGenericTypeDefinition))
        {
            var provider = descriptor.Lifetime == ServiceLifetime.Singleton ? host.Provider : window.Services;
            var resolved = provider.GetService(descriptor.ServiceType);
            Assert.True(resolved is not null, $"{descriptor.ServiceType.Name} ({descriptor.Lifetime}) did not resolve.");
            if (resolved is IDisposable disposable and not (ShellViewModel or ActivityCenterViewModel or ConnectionContext or WatchdogObjectHistoryLauncher))
            {
                // Transient view models resolved here only for the test (scoped ones are disposed with the scope).
                if (descriptor.Lifetime == ServiceLifetime.Transient)
                {
                    disposable.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task ChangeDomainController_FromTheContainer_UsesTheDcLocator()
    {
        await using var host = CreateHost();
        await using var window = host.CreateConnection();
        await window.ConnectAsync();

        // The factory's optional connector comes from the container: the client site is known (DC locator).
        using var dialog = window.Get<IDialogViewModelFactory>().CreateChangeDomainController();
        await dialog.InitializeAsync();

        Assert.False(string.IsNullOrEmpty(dialog.WindowTitle));
        Assert.Null(dialog.ErrorText);
        Assert.True(dialog.CanUseAnyWritableInSite);
        Assert.Contains(dialog.DomainControllers, d => d.HostName == "dc02.contoso.test");
    }

    private static ConnectionScopeHost CreateHost() =>
        new(services => services.AddSingleton<IChangePlanner, RecordingChangePlanner>(), [new Testing.FakeDirectoryBuilder().Build()]);
}
