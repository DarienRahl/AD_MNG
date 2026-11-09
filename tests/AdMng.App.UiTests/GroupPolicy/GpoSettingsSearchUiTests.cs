using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// "Wyszukiwarka ustawień GPO" in the main window (K7, design note docs/dev/k-computers-and-gpo.md 21.4): opened from the GPO
/// list's toolbar, the tree's menu and the palette command (one tab), the summary of an empty query, the results grouped by
/// setting in a list that virtualizes also when grouped, the details and actions of the selected rows, Ctrl+F into the query
/// and "Znajdź we wszystkich GPO" of a GPO's "Ustawienia" tab; binding-error free.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GpoSettingsSearchUiTests
{
    [Fact]
    public Task Search_OpensOnce_GroupsTheResults_AndShowsTheSelectedOccurrence() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        context.Directory.AddGroupPolicyCleanupSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        // The GPO list's toolbar opens the document; the tree's menu and the palette command select the same tab.
        var objects = Assert.IsType<GroupPolicyObjectsDocumentViewModel>(shell.OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects)));
        await UiWait.UntilAsync(() => !objects.IsLoading && objects.Rows.Count > 7, "the GPO list");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var objectsView = Assert.Single(UiWait.Descendants<GroupPolicyObjectsView>(main));
        Assert.True(objectsView.SearchSettingsButton.IsEnabled);
        objectsView.SearchSettingsButton.Command.Execute(null);
        await UiWait.UntilAsync(() => shell.SelectedTab is GpoSettingsSearchDocumentViewModel { IsLoading: false, HasIndex: true }, "the settings index");
        var document = (GpoSettingsSearchDocumentViewModel)shell.SelectedTab!;
        var domain = shell.Tree.DomainNode!;
        await domain.EnsureChildrenLoadedAsync();
        var gpoRoot = domain.Children.OfType<GroupPolicyObjectsNodeViewModel>().Single();
        var views = context.Services.GetRequiredService<ViewLibrary>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        var item = TreeMenu.Create(gpoRoot, shell, views, dialogs).OfType<MenuItem>().Single(i => Equals(i.Header, "Wyszukaj ustawienie we wszystkich GPO…"));
        item.Command.Execute(item.CommandParameter);
        shell.OpenGroupPolicySearchCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Same(document, shell.SelectedTab);
        Assert.Single(shell.Documents.OfType<GpoSettingsSearchDocumentViewModel>());

        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<GpoSettingsSearchView>(main));
        Assert.Equal("Wyszukiwarka ustawień GPO", view.TitleText.Text);
        Assert.Equal(TextTrimming.CharacterEllipsis, view.TitleText.TextTrimming);
        Assert.True(view.SummaryPanel.IsVisible);
        Assert.NotEmpty(view.SummaryList.Items);
        Assert.Empty(view.ResultsList.Items);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(view.ResultsList));
        Assert.True(VirtualizingPanel.GetIsVirtualizingWhenGrouping(view.ResultsList));
        Assert.Equal(SelectionMode.Extended, view.ResultsList.SelectionMode);
        Assert.Single(UiWait.Descendants<PaneSplitter>(view));
        Assert.True(view.DetailsEmptyText.IsVisible);

        // Headers wider than their texts (nothing trimmed).
        var gridView = (GridView)view.ResultsList.View;
        foreach (var column in gridView.Columns)
        {
            var text = Assert.IsType<string>(column.Header);
            var header = new TextBlock { Text = text };
            header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(column.Width > header.DesiredSize.Width + 16, text);
        }

        // A query: the results grouped by setting, the summary hidden.
        document.SearchText = "K:";
        await document.SearchCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.False(view.SummaryPanel.IsVisible);
        Assert.True(view.ResultText.IsVisible);
        Assert.Equal(document.Rows.Count, view.ResultsList.Items.Count);
        var groups = view.ResultsList.Items.Groups!.Cast<CollectionViewGroup>().ToList();
        Assert.Equal(document.Groups.Count, groups.Count);
        var drive = groups.Single(g => g.Name is GpoSearchGroupViewModel { Area: GpoSearchArea.Preferences });
        Assert.Equal(3, drive.ItemCount);
        Assert.NotEmpty(UiWait.Descendants<GroupItem>(view.ResultsList));

        // Rows selected: the details of the current one, the actions follow the selection.
        var rows = ((GpoSearchGroupViewModel)drive.Name).Rows;
        Assert.False(view.OpenGpoButton.IsEnabled);
        view.ResultsList.SelectedItems.Add(rows[0]);
        view.ResultsList.SelectedItems.Add(rows[1]);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Equal(2, document.SelectedRows.Count);
        Assert.NotNull(document.SelectedRow);
        Assert.True(view.DetailsScroll.IsVisible);
        Assert.Equal(document.SelectedRow!.Name, view.DetailsName.Text);
        Assert.Equal(document.SelectedRow.ValueText, view.DetailsValue.Text);
        Assert.True(view.OpenGpoButton.IsEnabled);
        Assert.True(view.CompareButton.IsEnabled);
        Assert.True(view.CopyToButton.IsEnabled);
        Assert.True(view.CopyButton.IsEnabled);
        Assert.True(view.ExportCsvButton.IsEnabled);

        // Ctrl+F reaches the query.
        await shell.FindCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(view.SearchBox, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(view.SearchBox)));

        // "Znajdź we wszystkich GPO" of a GPO's "Ustawienia" tab hands its search text to this tab.
        var gpo = Assert.IsType<GroupPolicyObjectDocumentViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Object) { GpoId = sample.WorkstationPasswords, Tab = GpoDocumentTab.Settings }));
        await UiWait.UntilAsync(() => !gpo.IsLoading && gpo.SettingCount > 0, "the GPO's settings");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var gpoView = Assert.Single(UiWait.Descendants<GroupPolicyObjectView>(main));
        var findInAll = Assert.Single(UiWait.Descendants<Button>(gpoView), b => b.Name == "SearchAllGposButton");
        gpo.SearchText = "długość";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        findInAll.Command.Execute(null);
        await UiWait.UntilAsync(() => ReferenceEquals(shell.SelectedTab, document) && document.SearchText == "długość", "the query handed over");
        await document.SearchCommand.ExecuteAsync(null);
        Assert.Contains(document.Groups, g => g.Name == "Minimalna długość hasła");
        context.AssertHealthy();
    });
}
