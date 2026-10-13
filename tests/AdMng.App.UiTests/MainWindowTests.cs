using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

[Collection(UiTestCollection.Name)]
public sealed class MainWindowTests
{
    [Fact]
    public Task MainWindow_Disconnected_RendersTheEmptyShell() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();

        Assert.False(window.Shell.IsConnected);
        Assert.Empty(window.Shell.Documents);
        Assert.Contains(window.Shell.Tree.ViewsNode, window.Shell.Tree.Roots);

        // Nothing selected: only the "no selection" hint, not an empty summary (null DataContext → fallback value).
        Assert.Null(window.Shell.SelectedObject);
        Assert.Equal(Visibility.Collapsed, window.DetailsContent.Visibility);
        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_Connected_ShowsTheSampleCompany_WithoutBindingErrors() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();

        var shell = window.Shell;
        Assert.True(shell.IsConnected);
        Assert.NotNull(shell.Tree.DomainNode);
        Assert.True(shell.Tree.DomainNode!.ChildrenLoaded);
        var document = Assert.Single(shell.Documents);
        Assert.NotEmpty(document.Items);
        Assert.NotNull(window.SelectedListView);
        Assert.Equal(Visibility.Collapsed, window.DetailsContent.Visibility);

        // Open the IT OU (users with Polish names), select everything, then a single user (details pane).
        await shell.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        await UiTestSupport.WaitForIdleAsync();
        var list = window.SelectedListView!;
        Assert.Equal(2, document.Items.Count);
        list.ListControl.SelectAll();
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(2, document.Selection.Count);
        Assert.Equal(2, shell.SelectedObject?.Count);

        list.ListControl.SelectedItem = document.Items[0];
        await Task.Delay(400);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Single(document.Selection);
        Assert.Equal(document.Items[0].Name, shell.SelectedObject?.Name);
        Assert.Equal(Visibility.Visible, window.DetailsContent.Visibility);

        // A second tab, the command palette, the filter chips toggle and the details pane toggle.
        await shell.NewTabCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(2, shell.Documents.Count);
        shell.CommandPalette.OpenCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, window.PaletteLayer.Visibility);
        shell.CommandPalette.FilterText = "odśw";
        await UiTestSupport.WaitForIdleAsync();
        shell.CommandPalette.CloseCommand.Execute(null);
        window.FilterToggle.IsChecked = false;
        await shell.ToggleDetailsPaneCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Collapsed, window.DetailsPane.Visibility);

        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_GlobalSearch_ShowsSuggestions_AndOpensAllResults() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();

        var shell = window.Shell;
        shell.SearchText = "Kowal";
        await Task.Delay(700);
        await UiTestSupport.WaitForIdleAsync();
        Assert.NotEmpty(shell.SearchSuggestions);

        await shell.ShowAllSearchResultsCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(ListLocationKind.Search, shell.SelectedDocument?.Location?.Kind);
        Assert.NotEmpty(shell.SelectedDocument!.Items);
        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_InlineRename_OpensTheEditorOverTheNameCell() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await window.Shell.NavigateAsync(ListLocation.ForContainer(context.Company.AccountingOu));
        await UiTestSupport.WaitForIdleAsync();

        var document = window.Shell.SelectedDocument!;
        var list = window.SelectedListView!;
        list.ListControl.SelectedItem = document.Items[0];
        await UiTestSupport.WaitForIdleAsync();
        window.Shell.BeginRenameCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.True(list.IsRenaming);
        Assert.Equal(Visibility.Visible, list.RenameEditor.Visibility);
        Assert.Equal(document.Items[0].Name, list.RenameEditor.Text);
        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_MenusAndToolbar_ResolveEveryCommandId_AndShortcuts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();

        var elements = LogicalDescendants(window).OfType<FrameworkElement>()
            .Concat(LogicalDescendants(window.SelectedListView!.ListControl.ContextMenu).OfType<FrameworkElement>())
            .Where(e => CommandMenu.GetId(e) is not null)
            .ToList();
        Assert.True(elements.Count > 40);
        Assert.All(elements, e => Assert.True(CommandMenu.Apply(e, window.Shell), $"Unknown command id {CommandMenu.GetId(e)}"));

        // Every descriptor shortcut became a key binding (object commands on the document area).
        var gestures = window.InputBindings.OfType<KeyBinding>()
            .Concat(window.DocumentHost.InputBindings.OfType<KeyBinding>())
            .Select(b => (b.Key, b.Modifiers))
            .ToHashSet();
        foreach (var descriptor in window.Shell.CommandDescriptors.Where(d => d.Shortcut.Length > 0))
        {
            var gesture = ShortcutText.TryParse(descriptor.Shortcut);
            Assert.NotNull(gesture);
            Assert.Contains((gesture!.Key, gesture.Modifiers), gestures);
        }

        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_TreeContextMenu_HasActionsForContainersAndViews() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();

        var views = context.Services.GetRequiredService<Presentation.Views.ViewLibrary>();
        var dialogs = context.Services.GetRequiredService<Presentation.Services.IDialogService>();
        var domain = window.Shell.Tree.DomainNode!;
        var containerMenu = TreeMenu.Create(domain, window.Shell, views, dialogs);
        Assert.Contains(containerMenu.OfType<MenuItem>(), item => item.Items.Count == 5);
        Assert.NotEmpty(TreeMenu.Create(window.Shell.Tree.ViewsNode, window.Shell, views, dialogs));

        // The built-in quick filter folder offers nothing (views are opened by clicking them).
        var quickFilters = window.Shell.Tree.ViewsNode.Children.OfType<FolderNodeViewModel>().First();
        Assert.Empty(TreeMenu.Create(quickFilters, window.Shell, views, dialogs));
        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_FollowsThemeAndLanguageSwitchesAtRuntime() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();

        var appearance = context.Services.GetRequiredService<AppearanceService>();
        var theme = context.Services.GetRequiredService<ThemeService>();
        var localizer = context.Services.GetRequiredService<ILocalizer>();
        try
        {
            await appearance.SetThemeCommand.ExecuteAsync(ThemePreference.Dark);
            await UiTestSupport.WaitForIdleAsync();
            Assert.Equal(ThemePreference.Dark, theme.Current);

            await appearance.SetLanguageCommand.ExecuteAsync("pl");
            await UiTestSupport.WaitForIdleAsync();
            Assert.Equal("pl-PL", localizer.Culture.Name);
            Assert.StartsWith("Użytkownicy i komputery usługi Active Directory", window.Title, StringComparison.Ordinal);
        }
        finally
        {
            // The theme is process wide: leave it as the other tests expect it.
            await appearance.SetThemeCommand.ExecuteAsync(ThemePreference.System);
            await UiTestSupport.WaitForIdleAsync();
        }

        Assert.Equal(ThemePreference.System, theme.Current);
        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_ClosingWhileAnOperationRuns_AsksAfterTheClosingEvent_ThenCloses() => WpfTestHost.RunAsync(async () =>
    {
        // Close() inside the Closing event throws in WPF: the question and the second Close() must come after it.
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
        await using var context = await UiTestContext.CreateAsync(
            configure: services => services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs)));
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        var activity = window.Shell.Activities.Start("Zmiana nazw", 1, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.Shell.Activities.HasRunning);

        window.Close();
        Assert.True(window.IsVisible);
        await UiTestSupport.WaitForIdleAsync();

        Assert.Single(dialogs.Confirmations);
        Assert.False(window.IsVisible);
        activity.CancelCommand.Execute(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activity.Completion);
        await UiTestSupport.WaitForIdleAsync();
        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_ClosingWhileAnOperationRuns_StaysOpenWhenDeclined() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = false };
        await using var context = await UiTestContext.CreateAsync(
            configure: services => services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs)));
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        var activity = window.Shell.Activities.Start("Zmiana nazw", 1, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        await UiTestSupport.WaitForIdleAsync();

        window.Close();
        await UiTestSupport.WaitForIdleAsync();
        window.Close();
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal(2, dialogs.Confirmations.Count);
        Assert.True(window.IsVisible);
        activity.CancelCommand.Execute(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activity.Completion);
        await UiTestSupport.WaitForIdleAsync();
        context.AssertHealthy();
    });

    private static IEnumerable<object> LogicalDescendants(object? root)
    {
        if (root is not DependencyObject element)
        {
            yield break;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(element))
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child))
            {
                yield return descendant;
            }
        }
    }
}
