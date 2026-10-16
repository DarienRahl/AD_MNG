using AdMng.App.Infrastructure;
using AdMng.App.Localization;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;
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
        Assert.Same(services.GetRequiredService<ILocalizer>(), TranslationSource.Localizer);
        context.AssertHealthy();
    });

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
