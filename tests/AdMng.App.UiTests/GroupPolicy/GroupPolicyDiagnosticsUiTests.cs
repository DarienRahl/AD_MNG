using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Diagnostics;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// "Diagnostyka zasad grupy" in the main window (K6, design note docs/dev/k-computers-and-gpo.md 20.2): opened from the GPO
/// list's toolbar, the menu entry and the tree (one tab), the quick check of the opening shown in a virtualized list with
/// severity dots, the chips, the details behind a splitter, Ctrl+F into the search, the full check; binding-error free.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GroupPolicyDiagnosticsUiTests
{
    [Fact]
    public Task Diagnostics_OpensOnce_ShowsTheQuickCheck_TheDetails_AndRunsAFullCheck() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        // The GPO list's toolbar opens the document; the menu entry and the tree select the same tab.
        var objects = Assert.IsType<GroupPolicyObjectsDocumentViewModel>(shell.OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects)));
        await UiWait.UntilAsync(() => !objects.IsLoading && objects.Rows.Count == 7, "the GPO list");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var objectsView = Assert.Single(UiWait.Descendants<GroupPolicyObjectsView>(main));
        Assert.True(objectsView.DiagnoseButton.IsEnabled);
        objectsView.DiagnoseButton.Command.Execute(null);
        await UiWait.UntilAsync(() => shell.SelectedTab is GroupPolicyDiagnosticsDocumentViewModel { IsLoading: false, LastDepth: not null }, "the quick check");
        var document = (GroupPolicyDiagnosticsDocumentViewModel)shell.SelectedTab!;
        shell.OpenGroupPolicyDiagnosticsCommand.Execute(null);
        var domain = shell.Tree.DomainNode!;
        await domain.EnsureChildrenLoadedAsync();
        var gpoRoot = domain.Children.OfType<GroupPolicyObjectsNodeViewModel>().Single();
        var views = context.Services.GetRequiredService<ViewLibrary>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        var item = TreeMenu.Create(gpoRoot, shell, views, dialogs).OfType<MenuItem>().Single(i => Equals(i.Header, "Diagnostyka zasad grupy…"));
        item.Command.Execute(item.CommandParameter);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Same(document, shell.SelectedTab);
        Assert.Single(shell.Documents.OfType<GroupPolicyDiagnosticsDocumentViewModel>());

        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<GroupPolicyDiagnosticsView>(main));
        Assert.Equal("Diagnostyka zasad grupy", view.TitleText.Text);
        Assert.Equal(TextTrimming.CharacterEllipsis, view.TitleText.TextTrimming);
        Assert.Equal(document.Rows.Count, view.FindingsList.Items.Count);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(view.FindingsList));
        Assert.Equal(5, view.ChipsList.Items.Count);
        Assert.True(view.NotesBar.IsVisible);
        Assert.Single(UiWait.Descendants<PaneSplitter>(view));
        Assert.True(view.DetailsEmptyText.IsVisible);

        // Headers wider than their texts (nothing trimmed).
        var gridView = (GridView)view.FindingsList.View;
        foreach (var column in gridView.Columns)
        {
            var text = Assert.IsType<string>(column.Header);
            var header = new TextBlock { Text = text };
            header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(column.Width > header.DesiredSize.Width + 16, text);
        }

        // A finding selected: its details and actions.
        var mismatch = document.Rows.Single(r => r.Code == GpoFindingCodes.VersionMismatch);
        Assert.Equal(sample.DriveMappings, mismatch.Finding.GpoId);
        view.FindingsList.SelectedItem = mismatch;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(mismatch, document.SelectedRow);
        Assert.True(view.DetailsScroll.IsVisible);
        Assert.Equal("Wersje w AD i SYSVOL różnią się", view.DetailsTitle.Text);
        Assert.Equal(mismatch.Message, view.DetailsMessage.Text);
        Assert.True(view.FixButton.IsEnabled);
        Assert.True(view.OpenGpoButton.IsEnabled);
        Assert.False(view.EditSettingButton.IsEnabled);

        // A chip filters; Ctrl+F reaches the document's search box.
        var medium = document.Chips.Single(c => c.Severity == GpoFindingSeverity.Medium);
        medium.IsSelected = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(medium.Count, view.FindingsList.Items.Count);
        Assert.True(view.ClearFiltersButton.IsVisible);
        await shell.FindCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(view.SearchBox, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(view.SearchBox)));
        document.ClearFiltersCommand.Execute(null);

        // The full check: no notes, more findings.
        await document.FullCheckCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Equal(GpoDiagnosticsDepth.Full, document.LastDepth);
        Assert.False(view.NotesBar.IsVisible);
        Assert.Contains(document.Rows, r => r.Code == GpoFindingCodes.AccountPolicyBelowDomain && r.GpoText == "Stacje robocze – Zasady haseł");
        Assert.Equal(document.Rows.Count, view.FindingsList.Items.Count);
        Assert.False(document.Status.IsWorking);
        context.AssertHealthy();
    });
}
