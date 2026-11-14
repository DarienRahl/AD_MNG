using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Testing;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of "Group Policy" of a domain, an organizational unit or a site (track V, stage V2b-3;
/// docs/dev/v-interface-renewal.md "V2b-3 as built"), through the real tab of the main window over the fake directory:
/// one line under the header - the summary of the links and of the inheritance, a container that is gone as the state
/// of the tab - and an operation of the tab that says in that line why nothing was changed, with Retry that leads to
/// the change plan preview: the write itself is the pipeline's, as before. A failing domain controller is an exception
/// of the fake session; every state that is asserted lasts until the test changes it. What the tab shows and changes is
/// covered by <see cref="GroupPolicyWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GroupPolicyScopeStatusUiTests
{
    [Fact]
    public Task TheTab_HasOneStatusLine_UnderItsHeader_WithTheSummaryOfTheLinks() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenScopeAsync(main, context.Company.ComputersOu, "Komputery");
        var line = view.PageStatus;

        // One status line, named PageStatus, over the status of the document: under the header, above the content.
        AssertTheLineOf(line, view, document);
        Assert.True(TopOf(view.RefreshButton, view) < TopOf(line, view), "the status line is not under the header");
        Assert.True(TopOf(line, view) < TopOf(view.BlockInheritanceBox, view), "the status line is not above the inheritance box");
        Assert.True(TopOf(view.BlockInheritanceBox, view) < TopOf(view.LinksList, view), "the inheritance box is not above the links");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: how many GPOs are linked here and how many apply - in the line, not in the status bar.
        Assert.Equal(2, document.Links.Count);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith("2 linked GPOs, ", line.Message, StringComparison.Ordinal);
        Assert.Equal(document.StatusText, line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(string.Empty, main.TabStatusText.Text);
        AssertNoNotes(view.DocumentNotes);
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AnOperationWhosePlanCannotBeMade_SaysSoInTheLine_AndRetryLeadsToTheChangePlanPreview() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context);
        var (document, view) = await OpenScopeAsync(main, context.Company.ComputersOu, "Komputery");
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var summary = line.Message;
        document.SelectedLink = document.Links.Single(l => l.Link.GpoId == sample.WorkstationPasswords);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var enforced = document.SelectedLink.Enforced;
        var name = document.SelectedLink.GpoName;
        Assert.True(view.ToggleEnforcedButton.IsEnabled);
        var linksTop = TopOf(view.LinksList, view);

        // The domain controller does not answer the read of the container's links any more: the plan cannot be made.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation.Kind == FakeOperationKind.Get && operation.Attributes.Contains(Attr.GpLink, StringComparer.OrdinalIgnoreCase))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(view.ToggleEnforcedButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Error && !document.Status.IsWorking, "the plan could not be made");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Said where the operation was started - no dialog -: what was not done, why, what to do, the code. The tab is
        // as it was, so the message can be closed; Retry makes the plan of the same change again.
        Assert.Equal("Error", line.Title);
        Assert.StartsWith($"Link of “{name}” – nothing was changed: ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again. (ServerUnavailable)", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Empty(Application.Current.Windows.OfType<MessageDialogWindow>());
        Assert.Equal(2, view.LinksList.Items.Count);
        Assert.Equal(enforced, document.Links.Single(l => l.Link.GpoId == sample.WorkstationPasswords).Enforced);
        Assert.False(document.IsLoading);
        view.UpdateLayout();
        Assert.Equal(linksTop + GrowthOf(line), TopOf(view.LinksList, view), 0.5);
        AssertNoOldFeedback(view);

        // The domain controller answers again. Retry, pressed in the line: the plan is made and shown in the change
        // plan preview - while that window is open the line of the tab says its summary again - and executed there.
        Volatile.Write(ref fail, 0);
        var drive = UiWait.DriveModalAsync<ChangePlanPreviewWindow>(async preview =>
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.Equal(summary, line.Message);
            await preview.ViewModel.ExecuteCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => preview.ViewModel.Step == PlanPreviewStep.Results, "the results of the change");
            Assert.False(preview.ViewModel.ResultHasFailures);
            preview.ViewModel.CloseCommand.Execute(null);
        });
        ControlHost.Press(action);
        await drive;
        await UiWait.UntilAsync(
            () => !document.IsLoading && line.Kind == PageStatusKind.Idle
                && document.Links.Any(l => l.Link.GpoId == sample.WorkstationPasswords && l.Enforced == !enforced),
            "the link was changed and the tab read again");
        session.BeforeOperation = null;
        Assert.Equal(document.StatusText, line.Message);
        view.UpdateLayout();
        Assert.Equal(linksTop, TopOf(view.LinksList, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AContainerThatIsGone_IsSaidByTheLine_ForAsLongAsTheTabShowsIt() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenScopeAsync(main, "OU=Nie ma takiej,OU=Firma," + context.Directory.DomainDn, "Nie ma takiej");
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // The state of the tab: an error that cannot be closed and has nothing to offer - there is nothing to retry.
        AssertTheLineOf(line, view, document);
        Assert.True(document.IsMissing);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.Equal("The container no longer exists.", line.Message);
        Assert.Equal(document.Error, line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Empty(view.LinksList.Items);
        Assert.False(view.LinkExistingButton.IsEnabled);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == document.Error);
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    private static Task<(GroupPolicyScopeDocumentViewModel Document, GroupPolicyScopeView View)> OpenScopeAsync(MainWindow main, string containerDn, string name) =>
        OpenAsync<GroupPolicyScopeDocumentViewModel, GroupPolicyScopeView>(
            main, new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Scope) { ContainerDn = containerDn, Name = name });
}
