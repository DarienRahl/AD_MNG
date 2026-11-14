using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Testing;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of "WMI Filters" (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"), through
/// the real tab of the main window over the fake directory: one line under the header - how many filters the domain
/// has - and a read that failed as the state of the tab, with Retry. A failing domain controller is an exception of the
/// fake session; every state that is asserted lasts until the test changes it. What the tab shows is covered by
/// <see cref="GroupPolicyWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class WmiFiltersStatusUiTests
{
    private static readonly GroupPolicyDocumentRequest Filters = new(GroupPolicyDocumentKind.WmiFilters);

    [Fact]
    public Task TheTab_HasOneStatusLine_UnderItsHeader_WithTheNumberOfFilters() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<WmiFiltersDocumentViewModel, WmiFiltersView>(main, Filters);
        var line = view.PageStatus;

        // One status line, named PageStatus, over the status of the document: under the header, above the list.
        AssertTheLineOf(line, view, document);
        Assert.True(TopOf(view.RefreshButton, view) < TopOf(line, view), "the status line is not under the header");
        Assert.True(TopOf(line, view) < TopOf(view.FiltersList, view), "the status line is not above the list");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: how many filters there are - in the line, not in the status bar of the window.
        Assert.Single(view.FiltersList.Items);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("1 WMI filters", line.Message);
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
    public Task AReadThatFails_IsAnErrorWithRetry_UntilTheFiltersAreReadAgain() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<WmiFiltersDocumentViewModel, WmiFiltersView>(main, Filters);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var listTop = TopOf(view.FiltersList, view);

        // The domain controller does not answer the read of the WMI filters (CN=SOM,CN=WMIPolicy,CN=System) any more.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation.Kind == FakeOperationKind.Search
                && operation.DistinguishedName.StartsWith("CN=SOM,CN=WMIPolicy,CN=System,", StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(view.RefreshButton);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Error, "the read failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The line says why the tab does not show the filters as they are now - with Retry, and it cannot be closed.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(document.Error);
        Assert.Equal($"Could not read “WMI Filters”: {document.Error} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(document.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        view.UpdateLayout();
        Assert.Equal(listTop + GrowthOf(line), TopOf(view.FiltersList, view), 0.5);
        AssertNoOldFeedback(view);

        // Retry, pressed in the line, reads the filters: the summary again, and the list is where it was.
        Volatile.Write(ref fail, 0);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Idle, "the filters were read");
        session.BeforeOperation = null;
        Assert.Equal("1 WMI filters", line.Message);
        view.UpdateLayout();
        Assert.Equal(listTop, TopOf(view.FiltersList, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });
}
