using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the property sheet (track V, stage V2b; docs/dev/v-interface-renewal.md 6.8 and "V2b-1 as
/// built"), through the real window over the fake directory: one line between the header and the pages - the summary, a
/// slow read with Stop, a failed read with Retry, the result of Apply - that also says what the page in front has to say
/// (a tab that could not be read, the nested list and its action button). A slow domain controller is the fake
/// session's latency (cancellable), a failing one an injected failure; every state that is asserted lasts until the test
/// changes it.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PropertySheetStatusUiTests
{
    /// <summary>Longer than any wait of a test: a read that is not cancelled fails the test instead of ending by itself.</summary>
    private static readonly TimeSpan SlowDomainController = TimeSpan.FromSeconds(30);

    [Fact]
    public Task TheSheet_HasOneStatusLine_BetweenTheHeaderAndThePages_AndNoBannerOrProgressBarOnAnyPage() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = await OpenSheetAsync(context, context.Company.JanKowalski);
        var sheet = window.ViewModel;
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, above the pages, of the fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(sheet);
        Assert.Same(sheet.Status.Current, line.Status);
        Assert.Same(sheet.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.PageTabs, window), "the status line is not above the pages");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: where and when the object was read, with the info picture, nothing to press.
        var summary = line.Message;
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith($"Read from {context.Directory.Session.Info.Server} · as of ", summary, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));

        // Every page in front: the line follows the selected tab, a page with a count says it there, and no page has a
        // banner of the old styles or a progress bar of its own.
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        foreach (var page in sheet.Pages.ToList())
        {
            sheet.SelectedPage = page;
            await UiWait.UntilAsync(() => !page.IsLoading && !page.Status.IsWorking, $"{page.Key} read");
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Same(page, window.PageTabs.SelectedItem);
            Assert.Same(page.Status, sheet.Status.Front);
            Assert.Same(sheet.Status.Current, line.Status);
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.Equal(page.Status.Current.Message.Length > 0 ? page.Status.Current.Message : summary, line.Message);
            Assert.Empty(UiWait.Descendants<ProgressBar>(window));
            Assert.DoesNotContain(
                UiWait.Descendants<Border>(window),
                border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
            Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        }

        // "Member Of" says its count in the line - and not a second time in the page.
        var memberOf = Assert.Single(sheet.Pages.OfType<MemberOfPageViewModel>());
        sheet.SelectedPage = memberOf;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.NotEqual(string.Empty, memberOf.CountText);
        Assert.Equal(memberOf.CountText, line.Message);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == memberOf.CountText);
        context.AssertHealthy();
    });

    [Fact]
    public Task ASlowReadOfTheObject_IsWorkingWithStop_AndStopReturnsControl() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = await OpenSheetAsync(context, context.Company.JanKowalski);
        var sheet = window.ViewModel;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var pagesTop = TopOf(window.PageTabs, window);
        var session = context.Directory.Session;

        // A domain controller that takes its time.
        session.Latency = SlowDomainController;
        Task load;
        try
        {
            load = sheet.RefreshCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => sheet.IsLoading && line.IsWorking, "the object is being read");

            // Working: the action and its scope, the turning ring, Stop - and nothing to close.
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Reading “{sheet.Name}”…", line.Message);
            Assert.True(ring.IsTurning, "the ring of the running read does not turn");
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.False(window.RefreshButton.IsEnabled);

            // The line was there before: the pages did not move.
            window.UpdateLayout();
            Assert.Equal(pagesTop, TopOf(window.PageTabs, window));

            // Stop, pressed in the line: the read ends well before the domain controller would have answered.
            ControlHost.Press(action);
            await load.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            session.Latency = TimeSpan.Zero;
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: an attention that is the state of the window - what it may lack, Refresh as the way on, nothing to
        // close. Control is back.
        Assert.False(sheet.IsLoading);
        Assert.Null(sheet.LoadError);
        Assert.Equal(PageStatusKind.Attention, line.Kind);
        Assert.Equal("Attention", line.Title);
        Assert.Equal(
            $"Reading “{sheet.Name}” was stopped: the window may not show everything. Press Refresh to read the object again.",
            line.Message);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsTurning);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(sheet.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.True(window.RefreshButton.IsEnabled);
        window.UpdateLayout();
        Assert.Equal(pagesTop, TopOf(window.PageTabs, window));

        // Refresh, pressed in the line, reads the object: the summary again.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !sheet.IsLoading && line.Kind == PageStatusKind.Idle, "the object was read after Refresh");
        Assert.StartsWith("Read from ", line.Message, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, action.Visibility);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AFailedReadOfTheObject_IsAnErrorInTheLine_WithRetry() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var jan = context.Company.JanKowalski;
        var window = await OpenSheetAsync(context, jan);
        var sheet = window.ViewModel;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // The domain controller refuses the next read of the object.
        context.Directory.Session.FailNext(FakeOperationKind.Get, jan, DirectoryErrorKind.ServerUnavailable);
        await sheet.RefreshCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // What we tried: why it failed. What to do next. (code) - with Retry and nothing to close: for as long as it
        // lasts it explains why the pages show the previous read.
        var reason = Assert.IsType<string>(sheet.LoadError);
        Assert.False(sheet.IsLoading);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.Equal("Error", line.Title);
        Assert.Equal(
            PageStatusText.Failure($"Could not read “{sheet.Name}”", reason, "Press Retry to try again.", ResultCode.Ldap(52)),
            line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(sheet.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);

        // One place: the reason is not shown a second time in the window.
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text.Contains(reason, StringComparison.Ordinal));

        // No page in front covers it.
        sheet.SelectedPage = Assert.Single(sheet.Pages.OfType<MemberOfPageViewModel>());
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Error, line.Kind);

        // Retry, pressed in the line, reads the object: the error is gone, the page in front says its summary.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !sheet.IsLoading && line.Kind == PageStatusKind.Idle, "the object was read after Retry");
        Assert.Null(sheet.LoadError);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task Apply_SaysInTheLineWhatWasSaved_AndClosedTheLineSaysTheSummaryAgain() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var jan = context.Company.JanKowalski;
        var window = await OpenSheetAsync(context, jan);
        var sheet = window.ViewModel;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var title = Assert.Single(
            Assert.IsType<FieldsPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Pages.Organization)).Fields.OfType<FieldViewModel>(),
            f => f.Attribute == Attr.Title);

        title.Value = "Kierownik działu IT";
        Assert.True(window.ApplyButton.IsEnabled);
        ControlHost.Press(window.ApplyButton);
        await UiWait.UntilAsync(() => !sheet.IsApplying && !sheet.HasChanges && line.Kind == PageStatusKind.Done, "the changes were saved");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Done: what happened, where, and where the report is - with the picture of "done" and a close button.
        Assert.Null(sheet.ApplyError);
        Assert.Equal("Done", line.Title);
        Assert.StartsWith($"The changes to “{sheet.Name}” were saved in ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(" The report is in the list of background operations.", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusOk, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal("Kierownik działu IT", Assert.Single(context.Directory.Session.GetValues(jan, Attr.Title)));

        // The result stays when another page comes to the front.
        sheet.SelectedPage = Assert.Single(sheet.Pages.OfType<MemberOfPageViewModel>());
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Done, line.Kind);

        // Closed: the page in front says its summary.
        ControlHost.Press(close);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith("Groups: ", line.Message, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheNestedList_IsReadWithAnActionButton_AndCancelledWhereItWasStarted() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = await OpenSheetAsync(context, context.Company.JanKowalski);
        var sheet = window.ViewModel;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var page = Assert.Single(sheet.Pages.OfType<MemberOfPageViewModel>());
        sheet.SelectedPage = page;
        await UiWait.UntilAsync(() => !page.IsLoading, "the groups were read");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var tabs = Assert.Single(UiWait.Descendants<TabControl>(window), t => t.Name == "MembershipTabs");
        var direct = line.Message;
        Assert.Equal(page.CountText, direct);
        Assert.Equal(0, page.SelectedListIndex);

        // The inner tab "All (nested)" on a slow domain controller: the list starts to read when it is shown.
        session.Latency = SlowDomainController;
        ActionButton button;
        try
        {
            tabs.SelectedIndex = MembershipPageViewModel.NestedListIndex;
            await UiWait.UntilAsync(() => page.IsNestedLoading && line.IsWorking, "the nested list is being read");
            window.UpdateLayout();

            // The view told the page which of its lists is shown: the line speaks about that list.
            Assert.Equal(MembershipPageViewModel.NestedListIndex, page.SelectedListIndex);
            Assert.Same(page.NestedStatus, page.Status.Front);
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Reading the “All (nested)” list…", line.Message);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));

            // Refresh is an action button over the commands of the page - while the list is read it is the Cancel of
            // that read, and it stays enabled although its start command cannot execute. There is no separate Stop
            // button and no progress bar.
            button = Assert.Single(UiWait.Descendants<ActionButton>(window), b => b.Name == "NestedRefreshButton");
            Assert.True(button.IsVisible);
            Assert.Same(page.LoadNestedCommand, button.Command);
            Assert.Same(page.NestedStatus.CancelCommand, button.CancelCommand);
            Assert.Equal(ActionButtonState.Running, button.State);
            Assert.True(button.IsEnabled);
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && !other.IsDescendantOf(line) && other.Content is string text && text == "Stop");
            Assert.Empty(UiWait.Descendants<ProgressBar>(window));

            // Cancel where you started: the read ends well before the domain controller would have answered.
            ControlHost.Press(button);
            await UiWait.UntilAsync(() => button.State == ActionButtonState.Idle && !page.IsNestedLoading, "the read of the nested list stopped");
        }
        finally
        {
            session.Latency = TimeSpan.Zero;
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: attention, Refresh as the way on, and it can be closed; the button is "Refresh" again.
        Assert.False(page.IsNestedLoaded);
        Assert.Equal(PageStatusKind.Attention, line.Kind);
        Assert.Equal("Reading the “All (nested)” list was stopped before all results arrived. Press Refresh to read it again.", line.Message);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(page.LoadNestedCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, button.State);
        Assert.True(button.IsEnabled);

        // The direct list in front again: the line says its count, nothing about the other list.
        tabs.SelectedIndex = 0;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(0, page.SelectedListIndex);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(direct, line.Message);

        // Shown again, the list that was not read yet reads itself: its count is its summary in the line.
        tabs.SelectedIndex = MembershipPageViewModel.NestedListIndex;
        await UiWait.UntilAsync(() => page.IsNestedLoaded && !page.IsNestedLoading && line.Kind == PageStatusKind.Idle, "the nested list was read");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(page.NestedItems.Count > 0);
        Assert.Equal(PageStatusText.Objects(context.Services.GetRequiredService<ILocalizer>(), page.NestedItems.Count), line.Message);
        Assert.Equal(page.NestedItems.Count, Assert.Single(UiWait.Descendants<ListView>(window), l => l.Name == "NestedList").Items.Count);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ATabThatCouldNotBeRead_SaysSoInTheLineWhileItIsSelected_WithRetry() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var jan = context.Company.JanKowalski;
        var window = await OpenSheetAsync(context, jan);
        var sheet = window.ViewModel;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var editor = Assert.Single(sheet.Pages.OfType<AttributeEditorPageViewModel>());
        var summary = line.Message;

        // The tab reads when it is shown, and the domain controller refuses that read.
        context.Directory.Session.FailNext(FakeOperationKind.Get, jan, DirectoryErrorKind.AccessDenied);
        sheet.SelectedPage = editor;
        await UiWait.UntilAsync(() => !editor.IsLoading && editor.Error is not null, "the failed read of the tab");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The line of the window says why the tab is empty - with Retry of the tab, and nothing to close.
        var reason = Assert.IsType<string>(editor.Error);
        Assert.Null(sheet.LoadError);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.StartsWith($"Could not read the “{editor.Title}” tab: {reason}", line.Message, StringComparison.Ordinal);
        Assert.Contains(" Press Retry to try again.", line.Message, StringComparison.Ordinal);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(editor.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text.Contains(reason, StringComparison.Ordinal));

        // Another tab in front: nothing of it. The failed tab again: it says so again.
        sheet.SelectedPage = sheet.Pages[0];
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(summary, line.Message);
        sheet.SelectedPage = editor;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.Equal(Visibility.Visible, action.Visibility);

        // Retry, pressed in the line, reads the tab.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !editor.IsLoading && editor.Rows.Count > 0 && line.Kind == PageStatusKind.Idle, "the tab was read after Retry");
        Assert.Null(editor.Error);
        Assert.Equal(summary, line.Message);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The property sheet of an object of the sample company, connected and read.</summary>
    private static async Task<PropertiesWindow> OpenSheetAsync(UiTestContext context, string distinguishedName)
    {
        await context.ConnectAsync();
        var factory = context.Services.GetRequiredService<IDialogViewModelFactory>();
        var window = (PropertiesWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(factory.CreatePropertySheet(distinguishedName));
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Null(window.ViewModel.LoadError);
        Assert.False(window.ViewModel.IsLoading);
        Assert.NotEmpty(window.ViewModel.Pages);
        return window;
    }

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;
}
