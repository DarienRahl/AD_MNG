using System.Windows;
using AdMng.App.Connections;
using AdMng.App.Correspondence;
using AdMng.App.Infrastructure;
using AdMng.App.Localization;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Batch;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.Messaging;
using AdMng.Core.PrivilegedAccess;
using AdMng.Outlook;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Comparison;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;
using AdMng.Presentation.Tests.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

[Collection(UiTestCollection.Name)]
public sealed class HostTests
{
    [Fact]
    public Task AppHost_RegistersTheUiServices_AndTheDialogWindows() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var services = context.Services;

        Assert.IsType<WpfUiDispatcher>(services.GetRequiredService<IUiDispatcher>());
        Assert.IsType<DialogService>(services.GetRequiredService<IDialogService>());
        Assert.IsType<FileDialogService>(services.GetRequiredService<IFileDialogService>());
        Assert.IsType<ClipboardService>(services.GetRequiredService<IClipboardService>());
        Assert.Same(services.GetRequiredService<NotificationService>(), services.GetRequiredService<INotificationService>());
        Assert.Same(services.GetRequiredService<ThemeService>(), services.GetRequiredService<IThemeService>());
        Assert.IsType<AppPaths>(services.GetRequiredService<IAppPaths>());
        Assert.NotNull(services.GetRequiredService<IChangePlanner>());
        Assert.NotNull(services.GetRequiredService<IBatchExecutor>());
        Assert.NotNull(services.GetRequiredService<IRollbackPlanner>());
        Assert.NotNull(services.GetRequiredService<IPowerShellExporter>());
        Assert.NotNull(services.GetRequiredService<ICsvImportPlanner>());
        Assert.NotNull(services.GetRequiredService<ICsvCodec>());
        Assert.IsType<FakeDirectoryConnector>(services.GetRequiredService<IDirectoryConnector>());

        var windows = services.GetRequiredService<IWindowService>();
        Assert.True(windows.IsRegistered(typeof(MessageDialogViewModel)));
        Assert.True(windows.IsRegistered(typeof(SavedViewEditorViewModel)));
        Assert.True(windows.IsRegistered(typeof(ColumnChooserViewModel)));
        Assert.True(windows.IsRegistered(typeof(ObjectComparisonViewModel)));
        Assert.True(windows.IsRegistered(typeof(AccountReviewViewModel)));
        Assert.True(windows.IsRegistered(typeof(Presentation.Filters.SavedFiltersViewModel)));
        Assert.True(windows.IsRegistered(typeof(Presentation.Filters.FilterEditorViewModel)));
        Assert.Same(services.GetRequiredService<DialogService>(), services.GetRequiredService<IObjectComparisonLauncher>());
        Assert.Same(services.GetRequiredService<ILocalizer>(), TranslationSource.Localizer);

        // U1c: the App's reader of Outlook's clipboard images (8.3 names expanded) replaces the Presentation default; one
        // WebView2 environment for the whole application.
        Assert.IsType<Presentation.UserMessages.PastedImageFileSource>(services.GetRequiredService<Core.Messaging.Html.IPastedImageSource>());
        Assert.Same(services.GetRequiredService<WebView2Host>(), context.CreateConnection().GetRequiredService<WebView2Host>());
        context.AssertHealthy();
    });

    /// <summary>The App's services of one connection window (with <see cref="ConnectionLifetimes.PerConnection"/>).</summary>
    private static readonly Type[] PerConnectionApp =
    [
        typeof(IWindowService),
        typeof(DialogService),
        typeof(IDialogService),
        typeof(ToolWindows),
        typeof(NotificationService),
        typeof(INotificationService),
        typeof(IClipboardService),
        typeof(IFileDialogService),
        typeof(ConnectionNavigator),
        typeof(IUserMessageLauncher),
        typeof(IObjectComparisonLauncher),
        typeof(Presentation.GroupPolicy.IGroupPolicyDialogs),
        typeof(Presentation.Correspondence.ICorrespondenceDialogs),
        typeof(Presentation.Correspondence.ICorrespondenceLauncher),
    ];

    [Fact]
    public Task EveryRegistration_ResolvesInItsLifetime() => WpfTestHost.RunAsync(async () =>
    {
        // The host does not expose its service collection: the configure callback gets the same list object.
        IServiceCollection? registrations = null;
        await using var context = await UiTestContext.CreateAsync(configure: services => registrations = services);
        var root = context.Host.Services;
        var connection = context.CreateConnection();

        foreach (var descriptor in registrations!.Where(d => !d.ServiceType.IsGenericTypeDefinition))
        {
            // Windows need their view model (the smoke test and the window tests create them).
            if (typeof(Window).IsAssignableFrom(descriptor.ImplementationType ?? descriptor.ServiceType))
            {
                continue;
            }

            var provider = descriptor.Lifetime == ServiceLifetime.Singleton ? root : connection;
            Assert.True(provider.GetService(descriptor.ServiceType) is not null, $"{descriptor.ServiceType.Name} ({descriptor.Lifetime}) did not resolve.");
        }

        context.AssertHealthy();
    });

    [Fact]
    public Task PerConnectionServices_AreScoped_AndTwoWindowsHaveTheirOwn() => WpfTestHost.RunAsync(async () =>
    {
        IServiceCollection? registrations = null;
        await using var context = await UiTestContext.CreateAsync(configure: services => registrations = services);
        var first = context.Services;
        var second = context.CreateConnection();

        foreach (var type in ConnectionLifetimes.PerConnection.Concat(PerConnectionApp))
        {
            // The descriptor itself: a singleton registered first would pre-empt AddAdMngPresentation's TryAddScoped.
            var descriptor = Assert.Single(registrations!, d => d.ServiceType == type);
            Assert.True(descriptor.Lifetime == ServiceLifetime.Scoped, $"{type.Name} is {descriptor.Lifetime}, not Scoped.");
            Assert.NotSame(first.GetRequiredService(type), second.GetRequiredService(type));
            Assert.Throws<InvalidOperationException>(() => context.Host.Services.GetRequiredService(type));
        }

        foreach (var type in ConnectionLifetimes.Shared.Concat(SharedApp))
        {
            Assert.Same(first.GetRequiredService(type), second.GetRequiredService(type));
        }

        // README U4: the grant sources (ACL delegations, local rights from GPOs) are stateless engines of the application;
        // a run reads through the session of the window that runs the review, never through a session of their own.
        Assert.All(registrations!.Where(d => d.ServiceType == typeof(IPrivilegeGrantSource)), d => Assert.Equal(ServiceLifetime.Singleton, d.Lifetime));
        Assert.Equal(2, registrations!.Count(d => d.ServiceType == typeof(IPrivilegeGrantSource)));
        Assert.Equal(first.GetServices<IPrivilegeGrantSource>(), second.GetServices<IPrivilegeGrantSource>());
        context.AssertHealthy();
    });

    /// <summary>Application-wide services of the App: the same instance in every connection window.</summary>
    private static readonly Type[] SharedApp =
    [
        typeof(ConnectionWindowManager),
        typeof(Core.Computers.Remote.IRemoteComputerClient),
        typeof(Core.Computers.Remote.ILocalToolLauncher),
        typeof(Core.Computers.Remote.IPingService),
        typeof(Core.Computers.Remote.IHostAddressResolver),
        typeof(AppearanceService),
        typeof(ISysvolFileReader),
        typeof(WebView2Host),
        typeof(ISysvolFileWriter),
        typeof(Core.Correspondence.Outlook.IOutlookClient),
        typeof(Core.Correspondence.Outlook.IOutlookSignatureSource),
        typeof(Core.Messaging.IOutlookUserMessageTransportFactory),
        typeof(Presentation.Correspondence.IBrowserPreview),
        typeof(Presentation.Correspondence.ICorrespondenceKeyProtector),
        typeof(Presentation.Correspondence.ICorrespondencePlatform),
    ];

    /// <summary>
    /// Korespondencja (U5): the App registers AdMng.Outlook – the signatures, the client that lists them and the U1 Outlook
    /// transport over that same client – and no constructor touches Outlook (the UI tests replace them with fakes).
    /// </summary>
    [Fact]
    public void CorrespondenceRegistration_UsesAdMngOutlook_OverOneClient()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddAdMngCorrespondenceApp();
        using var provider = services.BuildServiceProvider();

        var client = Assert.IsType<OutlookClient>(provider.GetRequiredService<IOutlookClient>());
        Assert.IsType<OutlookSignatureFiles>(provider.GetRequiredService<IOutlookSignatureSource>());
        var factory = Assert.IsType<OutlookUserMessageTransportFactory>(provider.GetRequiredService<IOutlookUserMessageTransportFactory>());
        Assert.Same(client, factory.Client);
        Assert.Single(services, d => d.ServiceType == typeof(IOutlookUserMessageTransportFactory));
        Assert.Same(factory, provider.GetRequiredService<IOutlookUserMessageTransportFactory>());
    }

    /// <summary>The Outlook of the smoke test (<see cref="OfflineOutlook"/>): not installed, never connects, no signatures.</summary>
    [Fact]
    public async Task OfflineOutlook_ReportsNotInstalled_AndHasNoSignatures()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddAdMngCorrespondenceApp();
        OfflineOutlook.Use(services);
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IOutlookClient>();
        Assert.Equal(OutlookAvailability.NotInstalled, client.Availability);
        var connect = await client.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Null(connect.Session);
        Assert.Equal(OutlookFailureKind.NotInstalled, connect.Failure?.Kind);
        Assert.Equal(OfflineOutlook.NotInstalledCode, connect.Failure?.Code);
        Assert.True(connect.Failure!.StopsRun);
        var signatures = provider.GetRequiredService<IOutlookSignatureSource>();
        Assert.Empty(signatures.List());
        Assert.Null(signatures.Folder);
        var factory = Assert.IsType<OutlookUserMessageTransportFactory>(provider.GetRequiredService<IOutlookUserMessageTransportFactory>());
        Assert.Same(client, factory.Client);
    }

    [Fact]
    public Task NotificationService_ShowsAtMostFourToasts_FromAnyThread() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var notifications = context.Services.GetRequiredService<NotificationService>();

        await Task.Run(() =>
        {
            for (var i = 0; i < 6; i++)
            {
                notifications.Show(NotificationSeverity.Info, $"Komunikat {i}");
            }
        });
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal(NotificationService.MaxVisible, notifications.Toasts.Count);
        Assert.Equal("Komunikat 5", notifications.Toasts[^1].Message);
        notifications.Toasts[0].DismissCommand.Execute(null);
        Assert.Equal(NotificationService.MaxVisible - 1, notifications.Toasts.Count);
        context.AssertHealthy();
    });
}
