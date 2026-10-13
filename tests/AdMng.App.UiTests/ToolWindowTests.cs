using System.Windows;
using System.Windows.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

[Collection(UiTestCollection.Name)]
public sealed class ToolWindowTests
{
    [Fact]
    public Task AppHost_RegistersTheBatchAndToolWindows() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var windows = context.Services.GetRequiredService<IWindowService>();

        Assert.True(windows.IsRegistered(typeof(BatchEditorViewModel)));
        Assert.True(windows.IsRegistered(typeof(SessionLogViewModel)));
        Assert.True(windows.IsRegistered(typeof(PowerShellHistoryViewModel)));
        Assert.True(windows.IsRegistered(typeof(SettingsViewModel)));
        Assert.NotNull(context.Services.GetRequiredService<ToolWindows>());
        context.AssertHealthy();
    });

    [Fact]
    public Task SessionLog_ShowsAChangeLive_AndFilters() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
        await using var context = await UiTestContext.CreateAsync(
            configure: services => services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs)));
        await context.ConnectAsync();
        var log = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateSessionLog();
        var window = (SessionLogWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(log);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Empty(window.EntriesList.Items);

        // A change made while the window is open appears without a refresh.
        await ApplyAsync(context, context.Company.MalgorzataWojcik, Attr.Title, "Dyrektor finansowa");
        await UiTestSupport.WaitForIdleAsync();
        var entry = Assert.Single(log.Entries);
        Assert.Equal("Główna księgowa", entry.OldValue);
        Assert.Equal("Dyrektor finansowa", entry.NewValue);
        Assert.True(entry.Succeeded);
        Assert.Single(window.EntriesList.Items);
        Assert.Equal(log.CountText, window.CountText.Text);

        log.FilterText = "nie ma takiego wpisu";
        await UiTestSupport.WaitForIdleAsync();
        Assert.Empty(window.EntriesList.Items);
        log.FilterText = "dyrektor";
        await UiTestSupport.WaitForIdleAsync();
        Assert.Single(window.EntriesList.Items);
        log.ShowFailuresOnly = true;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Empty(window.EntriesList.Items);
        log.ShowFailuresOnly = false;

        // Enter / double-click open the object's properties.
        log.SelectedEntry = log.Entries[0];
        await log.OpenCommand.ExecuteAsync(null);
        log.CloseCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.IsVisible);
        Assert.True(log.IsDisposed);
        context.AssertHealthy();
    });

    [Fact]
    public Task PowerShellHistory_ShowsTheScriptOfEachChange_AndCopiesIt() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new RecordingClipboard();
        await using var context = await UiTestContext.CreateAsync(
            language: "pl",
            configure: services => services.Replace(ServiceDescriptor.Singleton<IClipboardService>(clipboard)));
        await context.ConnectAsync();
        await ApplyAsync(context, context.Company.GrazynaDabrowska, Attr.Department, "Finanse");

        var history = context.Services.GetRequiredService<IDialogViewModelFactory>().CreatePowerShellHistory();
        var window = (PowerShellHistoryWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(history);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        await UiTestSupport.WaitForIdleAsync();

        var item = Assert.Single(history.Items);
        Assert.Same(item, history.SelectedItem);
        Assert.False(string.IsNullOrWhiteSpace(item.Script));
        Assert.Equal(item.Script, window.ScriptBox.Text);
        Assert.Equal(Visibility.Collapsed, window.EmptyHint.Visibility);
        history.CopyCommand.Execute(null);
        Assert.Equal(item.Script, clipboard.Text);

        // The next change is added on top while the window is open.
        await ApplyAsync(context, context.Company.GrazynaDabrowska, Attr.Title, "Starsza księgowa");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(2, history.Items.Count);
        Assert.Equal(2, window.ItemsList.Items.Count);
        history.SelectedItem = history.Items[0];
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(history.Items[0].Script, window.ScriptBox.Text);
        context.AssertHealthy();
    });

    [Fact]
    public Task PowerShellHistory_IsOpenedFromTheToolsMenu_AsOneWindow() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();

        main.PowerShellHistoryMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        main.PowerShellHistoryMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UiTestSupport.WaitForIdleAsync();

        var window = Assert.Single(Application.Current.Windows.OfType<PowerShellHistoryWindow>());
        Assert.Empty(window.ViewModel.Items);
        Assert.Equal(Visibility.Visible, window.EmptyHint.Visibility);
        context.AssertHealthy();
    });

    [Fact]
    public Task DialogService_ShowsOneSessionLog() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var dialogs = context.Services.GetRequiredService<IDialogService>();

        await dialogs.ShowSessionLogAsync();
        await dialogs.ShowSessionLogAsync();
        await UiTestSupport.WaitForIdleAsync();

        var window = Assert.Single(Application.Current.Windows.OfType<SessionLogWindow>());
        Assert.Same(window, context.Services.GetRequiredService<IWindowService>().FindOpen(ToolWindows.SessionLogKey));
        context.AssertHealthy();
    });

    [Fact]
    public Task Settings_ValidatesSavesAndCloses() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var settings = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateSettings();
        var window = (SettingsWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(settings);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Same(settings.Language, window.LanguageBox.SelectedItem);
        Assert.Same(settings.Theme, window.ThemeBox.SelectedItem);

        // Typing in the box updates the setting; out of range values disable Save.
        window.PageSizeBox.Text = "10";
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(10, settings.PageSize);
        Assert.NotNull(settings.Error);
        Assert.Equal(settings.Error, window.ErrorMessage.Text);
        Assert.False(settings.SaveCommand.CanExecute(null));

        window.PageSizeBox.Text = "1000";
        settings.ConfirmationThreshold = 40;
        settings.ShowAdvancedFeatures = true;
        settings.Language = settings.Languages.Single(l => l.Value == "pl");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(settings.Error);
        await settings.SaveCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.True(window.Result);
        Assert.False(window.IsVisible);
        var current = context.Services.GetRequiredService<ISettingsStore>().Current;
        Assert.Equal(1000, current.PageSize);
        Assert.Equal(40, current.ConfirmationThreshold);
        Assert.True(current.ShowAdvancedFeatures);
        Assert.Equal("pl", current.Language);
        Assert.Equal("pl-PL", context.Localizer().Culture.Name);
        context.AssertHealthy();
    });

    [Fact]
    public Task Settings_CancelLeavesTheSettingsUnchanged() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var settings = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateSettings();
        var window = (SettingsWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(settings);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        await UiTestSupport.WaitForIdleAsync();

        settings.InactiveDays = 30;
        settings.CancelCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.False(window.Result);
        Assert.False(window.IsVisible);
        Assert.Equal(90, context.Services.GetRequiredService<ISettingsStore>().Current.InactiveDays);
        context.AssertHealthy();
    });

    /// <summary>Applies one attribute change through the change pipeline (as a simple action would).</summary>
    private static async Task ApplyAsync(UiTestContext context, string dn, string attribute, string value)
    {
        var outcome = await context.Services.GetRequiredService<IChangeCoordinator>()
            .ApplyAsync(new ChangeRequest([dn], [new SetAttributeOperation(attribute, value)], "Zmiana atrybutu"));
        Assert.Equal(ChangeOutcome.Executed, outcome);
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public void SetText(string text) => Text = text;
    }
}
