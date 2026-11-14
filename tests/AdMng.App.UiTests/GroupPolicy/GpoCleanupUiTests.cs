using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Cleanup;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// "Porządki w GPO" in the main window (K7, design note docs/dev/k-computers-and-gpo.md 21.6): opened from the GPO list's
/// toolbar, the menu entry and the tree (one tab), the proposals of the cleanup sample in a virtualized list, the chips, the
/// details behind a splitter with only the actions the proposal offers, Ctrl+F into the search; binding-error free.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GpoCleanupUiTests
{
    [Fact]
    public Task Cleanup_OpensOnce_ShowsTheProposals_TheDetails_AndOnlyTheOfferedActions() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        var sample = context.Directory.AddGroupPolicyCleanupSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        // The GPO list's toolbar opens the document; the menu entry and the tree select the same tab.
        var objects = Assert.IsType<GroupPolicyObjectsDocumentViewModel>(shell.OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects)));
        await UiWait.UntilAsync(() => !objects.IsLoading && objects.Rows.Count == 16, "the GPO list");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var objectsView = Assert.Single(UiWait.Descendants<GroupPolicyObjectsView>(main));
        Assert.True(objectsView.CleanupButton.IsEnabled);
        objectsView.CleanupButton.Command.Execute(null);
        await UiWait.UntilAsync(() => shell.SelectedTab is GpoCleanupDocumentViewModel { IsLoading: false, HasResult: true }, "the analysis");
        var document = (GpoCleanupDocumentViewModel)shell.SelectedTab!;
        shell.OpenGroupPolicyCleanupCommand.Execute(null);
        var domain = shell.Tree.DomainNode!;
        await domain.EnsureChildrenLoadedAsync();
        var gpoRoot = domain.Children.OfType<GroupPolicyObjectsNodeViewModel>().Single();
        var views = context.Services.GetRequiredService<ViewLibrary>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        var item = TreeMenu.Create(gpoRoot, shell, views, dialogs).OfType<MenuItem>().Single(i => Equals(i.Header, "Porządki w GPO…"));
        item.Command.Execute(item.CommandParameter);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Same(document, shell.SelectedTab);
        Assert.Single(shell.Documents.OfType<GpoCleanupDocumentViewModel>());

        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<GpoCleanupView>(main));
        Assert.Equal("Porządki w GPO", view.TitleText.Text);
        Assert.Equal(TextTrimming.CharacterEllipsis, view.TitleText.TextTrimming);
        Assert.Equal(document.Rows.Count, view.ProposalsList.Items.Count);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(view.ProposalsList));
        Assert.Equal(Enum.GetValues<GpoCleanupKind>().Length, view.ChipsList.Items.Count);
        Assert.Single(UiWait.Descendants<PaneSplitter>(view));
        Assert.True(view.DetailsEmptyText.IsVisible);
        Assert.Null(document.Status.Lasting); // nothing changed after the analysis: the status line has no state to say

        // Headers wider than their texts (nothing trimmed).
        var gridView = (GridView)view.ProposalsList.View;
        foreach (var column in gridView.Columns)
        {
            var text = Assert.IsType<string>(column.Header);
            var header = new TextBlock { Text = text };
            header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(column.Width > header.DesiredSize.Width + 16, text);
        }

        // The duplicate: its details and only "Usuń duplikat z tego GPO".
        var duplicate = document.Rows.Single(r => r.Kind == GpoCleanupKind.DuplicateSetting && r.GpoId == sample.TemplatesBranch);
        view.ProposalsList.SelectedItem = duplicate;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(duplicate, document.SelectedRow);
        Assert.True(view.DetailsScroll.IsVisible);
        Assert.Equal("Zduplikowane ustawienie", view.DetailsTitle.Text);
        Assert.Equal(duplicate.Message, view.DetailsMessage.Text);
        Assert.True(view.RemoveSettingButton.IsVisible);
        Assert.True(view.RemoveSettingButton.IsEnabled);
        Assert.False(view.DeleteButton.IsVisible);
        Assert.False(view.MergeButton.IsVisible);
        Assert.False(view.NoActionsText.IsVisible);
        Assert.True(view.OpenGpoButton.IsEnabled);
        Assert.True(view.ShowInSearchButton.IsEnabled);

        // The empty GPO: delete and unlink.
        view.ProposalsList.SelectedItem = document.Rows.Single(r => r.Kind == GpoCleanupKind.EmptyGpo && r.GpoId == sample.EmptyGpo);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.True(view.DeleteButton.IsVisible);
        Assert.True(view.UnlinkButton.IsVisible);
        Assert.False(view.RemoveSettingButton.IsVisible);

        // A chip filters; Ctrl+F reaches the document's search box.
        var chip = document.Chips.Single(c => c.Kind == GpoCleanupKind.MergeCandidate);
        chip.IsSelected = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(chip.Count, view.ProposalsList.Items.Count);
        Assert.True(view.ClearFiltersButton.IsVisible);
        await shell.FindCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(view.SearchBox, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(view.SearchBox)));
        document.ClearFiltersCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(document.Rows.Count, view.ProposalsList.Items.Count);
        Assert.False(document.Status.IsWorking);
        context.AssertHealthy();
    });
}
