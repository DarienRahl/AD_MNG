using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Shell;
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
        var document = Assert.IsType<ObjectListViewModel>(Assert.Single(shell.Documents));
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

        // After SelectAll the first row already is the SelectedItem: clear the selection so that selecting it is a change.
        list.ListControl.UnselectAll();
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
    public Task MainWindow_DetailsPane_ShowsLabelsAsCaptionsAboveFullWidthValues() => WpfTestHost.RunAsync(async () =>
    {
        // Polish labels such as "Nazwa logowania (starsza niż Windows 2000)" are long and the pane is narrow: a label
        // column would leave the values ~110 px. The label is a caption above its value, the value gets the full width.
        await using var context = await UiTestContext.CreateAsync("pl");
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await window.Shell.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        await UiTestSupport.WaitForIdleAsync();
        var document = window.Shell.SelectedDocument!;
        window.SelectedListView!.ListControl.SelectedItem = document.Items[0];
        await UiWait.UntilAsync(() => window.DetailsContent.Visibility == Visibility.Visible, "details pane shown");
        await UiTestSupport.WaitForIdleAsync();

        var labels = UiWait.Descendants<TextBlock>(window.DetailsContent)
            .Where(t => t.DataContext is SummaryField field && t.Text == field.Label)
            .ToList();
        Assert.NotEmpty(labels);
        Assert.Contains(labels, t => t.Text.Length > 20);
        Assert.All(labels, t => Assert.Null(LayoutInformation.GetLayoutClip(t)));

        var content = window.DetailsContent;
        var values = UiWait.Descendants<TextBox>(content).Where(t => t.DataContext is SummaryField).ToList();
        Assert.Equal(labels.Count, values.Count);
        foreach (var label in labels)
        {
            var value = values.Single(t => ReferenceEquals(t.DataContext, label.DataContext));
            var labelBounds = BoundsIn(label, content);
            var valueBounds = BoundsIn(value, content);
            Assert.True(labelBounds.Bottom <= valueBounds.Top + 0.5, $"'{label.Text}' is above its value");
            Assert.Equal(labelBounds.Left, valueBounds.Left, 0.5);

            // The value and its copy button fill the entry's width (no label column next to the value).
            var row = (FrameworkElement)VisualTreeHelper.GetParent(value);
            var entry = (FrameworkElement)VisualTreeHelper.GetParent(label);
            Assert.Same(entry, VisualTreeHelper.GetParent(row));
            Assert.Equal(entry.ActualWidth, row.ActualWidth, 0.5);
            Assert.Equal(BoundsIn(row, content).Left, valueBounds.Left, 0.5);
        }

        // Location and DN: the same caption-above-value entries, values at the same left edge.
        Assert.Equal(BoundsIn(labels[0], content).Left, BoundsIn(window.DetailsDnText, content).Left, 0.5);
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
        var newMenu = Assert.Single(containerMenu.OfType<MenuItem>(), item => item.Items.Count == 5);

        // "New" creates in the right-clicked container, not in the current tab's.
        Assert.All(newMenu.Items.OfType<MenuItem>(), item =>
        {
            Assert.Equal(domain.Dn, item.CommandParameter);
            Assert.True(item.Command.CanExecute(item.CommandParameter));
        });
        Assert.NotEmpty(TreeMenu.Create(window.Shell.Tree.ViewsNode, window.Shell, views, dialogs));

        // The built-in quick filter folder offers nothing (views are opened by clicking them).
        var quickFilters = window.Shell.Tree.ViewsNode.Children.OfType<FolderNodeViewModel>().First();
        Assert.Empty(TreeMenu.Create(quickFilters, window.Shell, views, dialogs));
        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_NewCommands_SayWhyTheyAreDisabled_WhereCreatingIsNotPermitted() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        context.Directory.Session.SetPermissions(context.Company.ItOu, new EffectivePermissions
        {
            IsKnown = true,
            CreatableChildClasses = new HashSet<string>(["user"], StringComparer.OrdinalIgnoreCase),
        });
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await window.Shell.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        await UiWait.UntilAsync(() => !window.Shell.NewGroupCommand.CanExecute(null), "New Group disabled in OU=IT");
        await UiTestSupport.WaitForIdleAsync();

        var localizer = context.Services.GetRequiredService<ILocalizer>();
        var buttons = LogicalDescendants(window).OfType<Button>()
            .Where(b => CommandMenu.GetId(b) is "new.user" or "new.group")
            .ToDictionary(b => CommandMenu.GetId(b)!);
        Assert.True(buttons["new.user"].IsEnabled);
        Assert.Equal(ShortcutText.WithShortcut(localizer[L.Commands.NewUser], "Ctrl+Shift+U"), buttons["new.user"].ToolTip);
        Assert.False(buttons["new.group"].IsEnabled);
        Assert.Equal(localizer.Format(L.Permissions.CannotCreate, localizer[DisplayNames.ObjectKindKey(ObjectKind.Group)]), buttons["new.group"].ToolTip);

        // The tree's context menu reads the permissions of the clicked container.
        var it = await window.Shell.Tree.ExpandToAsync(context.Company.ItOu, select: false);
        var views = context.Services.GetRequiredService<Presentation.Views.ViewLibrary>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        var newMenu = Assert.Single(TreeMenu.Create(it!, window.Shell, views, dialogs).OfType<MenuItem>(), item => item.Items.Count == 5);
        var items = newMenu.Items.OfType<MenuItem>().ToList();
        await UiWait.UntilAsync(() => !items[1].Command.CanExecute(items[1].CommandParameter), "New Group disabled in the tree menu");
        Assert.True(items[0].Command.CanExecute(items[0].CommandParameter));
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
        var (activity, execution) = window.Shell.Activities.Start("Zmiana nazw", 1, async (_, token) =>
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await activity.Completion;
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
        var (activity, execution) = window.Shell.Activities.Start("Zmiana nazw", 1, async (_, token) =>
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await activity.Completion;
        await UiTestSupport.WaitForIdleAsync();
        context.AssertHealthy();
    });

    private static Rect BoundsIn(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

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
