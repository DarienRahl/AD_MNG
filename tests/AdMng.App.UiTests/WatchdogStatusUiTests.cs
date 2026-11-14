using System.Windows;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.App.Views.Watchdog;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the Watchdog tab (track V, stage V2b-4, unit 4; docs/dev/v-interface-renewal.md "V2b-4 as built"),
/// through the real tab over the sample client: one line under the connection card that says the summary of the page shown,
/// the read of the feed with Stop, the connection problem with Retry, the save of the configuration and a delivery test - and
/// no progress bar and no banner of the old styles anywhere in the tab. A slow service is a call the test holds on the fake
/// client and releases; every state that is asserted lasts until the test changes it. What the pages show is covered by
/// <see cref="WatchdogViewTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class WatchdogStatusUiTests
{
    [Fact]
    public Task TheTab_HasOneStatusLine_UnderTheConnectionCard_WithTheSummaryOfThePageShown() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        await using var context = await UiTestContext.CreateAsync(configure: WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context);
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);
        var line = view.PageStatus;

        // One status line, named PageStatus, over the status of the tab, between the card and the pages, of the fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(view)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(tab);
        Assert.Same(tab.Status.Current, line.Status);
        Assert.Same(tab.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(view.ConnectionBanner, view) < TopOf(line, view), "the status line is not under the connection card");
        Assert.True(TopOf(line, view) < TopOf(view.SubTabs, view), "the status line is not above the pages");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // The feed is shown: its summary, once - not again in the status bar of the main window.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith("Events: 30 · unacknowledged: ", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.Equal(string.Empty, window.Shell.StatusBarText);

        // The connection card says which service and as whom, and nothing about the state: no retry, no certificate button.
        Assert.DoesNotContain(UiWait.Descendants<Button>(view.ConnectionBanner), b => b.IsVisible && b.Name is "RetryButton" or "ReviewCertificateButton");
        Assert.True(view.ChangeAddressButton.IsVisible);

        // Every page says its own summary in the same line, and none of them has a bar, a progress bar or a second line.
        foreach (var page in new[] { WatchdogSubTab.Rules, WatchdogSubTab.Channels, WatchdogSubTab.Digest, WatchdogSubTab.Sources })
        {
            await WatchdogUiSupport.SelectAsync(tab, page);
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.Equal(tab.Config.VersionText, line.Message);
            AssertNoOldFeedback(view);
        }

        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Status);
        await UiWait.UntilAsync(() => tab.ServiceStatus.HasStatus && line.Message.StartsWith("Status read ", StringComparison.Ordinal), "the summary of the status tab");
        AssertNoOldFeedback(view);
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Events);
        AssertNoOldFeedback(view);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheRead_OfTheFeed_SaysWorkingWithStop_AndAStoppedReadIsAttentionWithRefresh() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        await using var context = await UiTestContext.CreateAsync(configure: WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context);
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var listTop = TopOf(view.EventsView.EventsList, view);

        // The first page of the feed waits for the test (the live long-poll of the visible tab keeps running).
        client.HoldEvents(q => q.After is null && q.Before is null);
        try
        {
            tab.Events.RefreshCommand.Execute(null);
            await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Working && line.Message == "Reading the events…", "the feed is being read");

            // Working: what is read - with Stop, and nothing to close; no progress bar and the list did not move.
            Assert.Equal("Working", line.Title);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            AssertNoOldFeedback(view);
            view.UpdateLayout();
            Assert.Equal(listTop, TopOf(view.EventsView.EventsList, view));

            ControlHost.Press(action);
        }
        finally
        {
            client.ReleaseEvents();
        }

        await UiWait.UntilAsync(() => !tab.Events.IsLoading && line.Kind == PageStatusKind.Attention, "the read was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: the feed is empty and paused - the state of the page, which cannot be closed - and Refresh is the way out.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Reading the events was stopped: the list is empty and live updates are paused. Press “Refresh” to read it again.", line.Message);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(tab.Events.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(Visibility.Collapsed, view.EventsView.EmptyHint.Visibility);
        view.UpdateLayout();
        Assert.Equal(listTop + GrowthOf(line), TopOf(view.EventsView.EventsList, view), 0.5);

        // Refresh, pressed in the line, reads the feed again: the summary.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle && tab.Events.Items.Count == 30, "the feed is read again");
        AssertNoOldFeedback(view);
        context.AssertHealthy();
    });

    [Fact]
    public Task AConnectionProblem_IsTheErrorOfTheLine_WithRetry_AndTheCardKeepsTheHost() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        client.FailWith(WatchdogClientErrorKind.Unreachable);
        await using var context = await UiTestContext.CreateAsync(configure: WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context);
        var (tab, view) = await WatchdogUiSupport.OpenAsync(window);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // The state of the tab that lasts: an error with the host and the reason, Retry as its one action, nothing to close.
        Assert.Equal(WatchdogConnectionState.Unreachable, tab.ConnectionState);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.Equal("Error", line.Title);
        Assert.StartsWith("Could not connect to admng-wd.contoso.com:8443: ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again. (Unreachable)", line.Message, StringComparison.Ordinal);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(tab.RefreshNowCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(Visibility.Collapsed, view.SubTabs.Visibility);

        // The card says where the credentials would go - once, without the state - and still offers another address.
        Assert.True(view.ConnectionBanner.IsVisible);
        var cardTexts = UiWait.Descendants<TextBlock>(view.ConnectionBanner).Where(t => t.IsVisible).Select(t => t.Text).ToList();
        Assert.Single(cardTexts, t => t.Contains("admng-wd.contoso.com:8443", StringComparison.Ordinal));
        Assert.True(view.ChangeAddressButton.IsVisible);
        AssertNoOldFeedback(view);

        // Retry, pressed in the line, connects: the error is gone and the pages are there.
        client.ClearFailures();
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => tab.IsConnected && line.Kind != PageStatusKind.Error, "the tab is connected");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Equal(Visibility.Visible, view.SubTabs.Visibility);
        Assert.False(line.CanClose);
        context.AssertHealthy();
    });

    [Fact]
    public Task SavingTheConfiguration_IsAWriteWithoutStop_AndItsResultMovesTheContentByAsMuchAsTheLineGrew() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        await using var context = await UiTestContext.CreateAsync(configure: WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context);
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Rules);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var contentTop = TopOf(view.SubTabs, view);

        tab.Rules.SelectedRule!.IsEnabled = !tab.Rules.SelectedRule.IsEnabled;
        await UiWait.UntilAsync(() => view.RulesView.ConfigBar.SaveButton.IsEnabled, "Save is possible");
        Assert.Equal(Visibility.Visible, view.RulesView.ConfigBar.DirtyText.Visibility);
        client.Hold(nameof(IWatchdogClient.SaveConfigAsync));
        try
        {
            ControlHost.Press(view.RulesView.ConfigBar.SaveButton);
            await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Working, "the configuration is being saved");

            // A write: nothing to stop, nothing to close - and the content under the line did not move.
            Assert.Equal("Saving the configuration…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            AssertNoOldFeedback(view);
            view.UpdateLayout();
            Assert.Equal(contentTop, TopOf(view.SubTabs, view));
        }
        finally
        {
            client.Release(nameof(IWatchdogClient.SaveConfigAsync));
        }

        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Done, "the configuration is saved");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Done", line.Title);
        Assert.Equal("Configuration saved (version 8).", line.Message);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(Visibility.Collapsed, view.RulesView.ConfigBar.DirtyText.Visibility);
        view.UpdateLayout();
        Assert.Equal(contentTop + GrowthOf(line), TopOf(view.SubTabs, view), 0.5);

        // Closed: the version again, in the same place.
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result is closed");
        Assert.Equal(tab.Config.VersionText, line.Message);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheTestButtons_AreActionButtons_TheTestIsStoppedWhereItWasStarted_AndItsStagesStayInTheirRows() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        await using var context = await UiTestContext.CreateAsync(configure: WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context);
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Channels);
        var line = view.PageStatus;
        var channels = tab.Channels;
        var button = view.ChannelsView.SendTestEmailButton;
        tab.Channels.TestRecipients = "jan.kowalski@contoso.com";
        await UiTestSupport.WaitForIdleAsync();

        // One action button over the start command and the Cancel of the test.
        Assert.Equal(ActionButtonState.Idle, button.State);
        Assert.Same(channels.SendTestEmailCommand, button.Command);
        Assert.Same(channels.EmailTest.CancelCommand, button.CancelCommand);
        Assert.True(button.IsEnabled);
        Assert.DoesNotContain(
            UiWait.Descendants<Button>(view.ChannelsView.EmailTestResult),
            other => other.IsVisible && (other.Content as string) is "Cancel" or "Stop");

        client.Hold(nameof(IWatchdogClient.SendTestEmailAsync));
        try
        {
            ControlHost.Press(button);
            await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Working && channels.EmailTest.IsRunning, "the test is running");

            // Working: the line says it, with Stop; the button is its Cancel; no progress bar in the card.
            Assert.Equal("Sending the test…", line.Message);
            Assert.Equal(ActionButtonState.Running, button.State);
            Assert.True(button.IsEnabled);
            AssertNoOldFeedback(view);
        }
        finally
        {
            client.Release(nameof(IWatchdogClient.SendTestEmailAsync));
        }

        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Done && !channels.EmailTest.IsRunning, "the test is over");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // How it ended is said by the line; each stage keeps its own result in its row.
        Assert.Equal("The test succeeded (840 ms).", line.Message);
        Assert.Equal(ActionButtonState.Idle, button.State);
        Assert.Equal(7, view.ChannelsView.EmailTestResult.StagesList.Items.Count);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheNotes_AreNotes_TheViewerSeesWhyEveryEditorIsReadOnly_AndTheFindingsOfTheServiceAreAList() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient(admin: false);
        await using var context = await UiTestContext.CreateAsync(configure: WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context);
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Rules);

        // Why the editors cannot be used is a note of the configuration bar, with the look of a note.
        var bar = view.RulesView.ConfigBar;
        Assert.True(bar.ReadOnlyBar.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), bar.ReadOnlyBar.Style);
        Assert.Same(bar.ReadOnlyText, bar.ReadOnlyBar.Content);
        Assert.False(string.IsNullOrWhiteSpace(bar.ReadOnlyText.Text));
        Assert.Same(Application.Current.FindResource("Ad.Note.Info"), bar.RedactedNote.Style);
        AssertNoOldFeedback(view);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheDetailsPane_ShowsARing_WhileItsReadRuns_AndWhyItFailedIsANote() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        await using var context = await UiTestContext.CreateAsync(configure: WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context);
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);
        var details = view.EventsView.DetailsView;
        var line = view.PageStatus;

        // The read of the details of the event: held, so the ring lasts - a ring in the pane, the line says nothing.
        client.Hold(nameof(IWatchdogClient.GetEventAsync));
        try
        {
            view.EventsView.EventsList.SelectedItem = tab.Events.Items[0];
            await UiWait.UntilAsync(() => tab.Events.Details.IsLoading, "the details are being read");
            await UiTestSupport.WaitForIdleAsync();
            Assert.True(details.LoadingRing.IsActive);
            Assert.True(details.LoadingRing.IsVisible);
            Assert.NotEqual(PageStatusKind.Working, line.Kind);
        }
        finally
        {
            client.FailNext(nameof(IWatchdogClient.GetEventAsync), new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down"));
            client.Release(nameof(IWatchdogClient.GetEventAsync));
        }

        // The read failed: a sentence about this event - a note under its title - and nothing in the line.
        await UiWait.UntilAsync(() => !tab.Events.Details.IsLoading, "the read of the details ended");
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(details.LoadingRing.IsVisible);
        Assert.NotNull(tab.Events.Details.Error);
        Assert.True(details.ErrorNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Error"), details.ErrorNote.Style);
        Assert.Equal(tab.Events.Details.Error, details.ErrorText.Text);
        Assert.NotEqual(PageStatusKind.Error, line.Kind);
        context.AssertHealthy();
    });

    /// <summary>The old places of feedback are gone from the tab: no progress bar and no banner of the styles of before, one line.</summary>
    private static void AssertNoOldFeedback(WatchdogView view)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(view));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(view),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Same(view.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(view)));
    }

    /// <summary>
    /// How much higher than one line the status line is right now: a result that does not fit one line wraps (to three
    /// lines at most), and what lies under the line moves down by as much - by nothing when the result fits.
    /// </summary>
    private static double GrowthOf(StatusLine line)
    {
        var growth = line.ActualHeight - (double)Application.Current.FindResource("Ad.Size.StatusLine");
        Assert.InRange(growth, -0.5, 40.5);
        return growth;
    }

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;

    private static async Task<MainWindow> ShowMainWindowAsync(UiTestContext context)
    {
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();
        return window;
    }
}
