using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Correspondence;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.Correspondence;

/// <summary>
/// The one status line of the Korespondencja window (track V, stage V2b-4, unit 5; docs/dev/v-interface-renewal.md "V2b-4 as
/// built"), through the real window over the fake directory and the in-memory Outlook: the line above the tabs, what it says
/// for the tab in front (the counts of the recipients, a run of the send page with Stop, the search for the owners, the two
/// reads of Sent Items), the states that last (the session, Outlook), the notes that stay in their places, and no banner and
/// no progress bar left. A run that is held waits on a pool thread for the test, so every state that is asserted lasts until
/// the test lets it go; "Stopping…" is looked at only while the held call is still on its way. How the pages work is covered
/// by the Presentation tests and <see cref="CorrespondenceWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CorrespondenceStatusUiTests
{
    /// <summary>How long a held call waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AboveTheTabs_WithTheCountsOfThePageInFront() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var window = await CorrespondenceWindowTests.OpenAsync(context, new CorrespondenceRequest([]));
        var vm = window.ViewModel;
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, above the tabs, of the fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(vm);
        Assert.Same(vm.Status.Current, line.Status);
        Assert.Same(vm.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.CorrespondenceTabs, window), "the status line is not above the tabs");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: the counts of the recipients - in the line, and not a second time anywhere else.
        Assert.Equal(CorrespondenceTab.Recipients, vm.SelectedTab);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(vm.Recipients.CountsText, line.Message);
        Assert.StartsWith("Recipients: 0", line.Message, StringComparison.Ordinal);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == vm.Recipients.CountsText);

        // The line follows the tab: the matches of the mailbox owners, nothing on the message tab.
        window.ShowMailboxesButton.Command.Execute(null);
        await UiWait.UntilAsync(() => line.Message == vm.Mailboxes.SummaryText, "the summary of the mailbox owners in the line");
        window.CorrespondenceTabs.SelectedItem = window.MessageTab;
        await UiWait.UntilAsync(() => line.Message.Length == 0, "an empty line on the message tab");
        Assert.Equal(PageStatusKind.Idle, line.Kind);

        // The footer keeps the state of the Outlook connection and has a ring, not a bar, for the window that loads.
        Assert.Equal(vm.Outlook.StateText, window.OutlookStateText.Text);
        Assert.False(window.BusyRing.IsActive);
        Assert.False(window.BusyRing.IsVisible);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task WhatIsTrueOfAFieldIsANote_InItsPlace_WithTheLookOfANote() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var window = await CorrespondenceWindowTests.OpenAsync(context, new CorrespondenceRequest([]));
        var vm = window.ViewModel;
        var attention = Application.Current.FindResource("Ad.Note.Attention");

        // What pasting into the body left out: a note under the editor, not a message of the window.
        window.CorrespondenceTabs.SelectedItem = window.MessageTab;
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.PasteProblemsNote.IsVisible);
        vm.Message.ReportEditorError("A table was pasted as text.");
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.PasteProblemsNote.IsVisible);
        Assert.Same(attention, window.PasteProblemsNote.Style);
        Assert.Equal("A table was pasted as text.", window.PasteProblemsText.Text);
        Assert.Equal(PageStatusKind.Idle, window.PageStatus.Kind);   // the line says nothing of it
        Assert.Equal(string.Empty, window.PageStatus.Message);

        // The preview says where it came from in an information note.
        window.CorrespondenceTabs.SelectedItem = window.PreviewTab;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Same(Application.Current.FindResource("Ad.Note.Info"), window.PreviewFromOutlookNote.Style);
        Assert.False(window.PreviewFromOutlookNote.IsVisible);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task ARunOfTheSendPage_IsItsOperation_TheButtonThatStartedItIsItsCancel_AndTheLineSaysHowItEnded() => WpfTestHost.RunAsync(async () =>
    {
        var (context, dialogs) = await TestDialogService.CreateContextAsync("en");
        await using var _ = context;
        await context.ConnectAsync();
        var outlook = context.Outlook!;
        dialogs.ConfirmAnswer = true;
        var window = await CorrespondenceWindowTests.OpenAsync(context, new CorrespondenceRequest([]));
        var vm = window.ViewModel;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        vm.Recipients.AddRows(
        [
            new ImportedContact("anna.zielinska@contoso.pl", "Anna Zielińska", new Dictionary<string, string>()),
            new ImportedContact("jan.nowak@contoso.pl", "Jan Nowak", new Dictionary<string, string>()),
            new ImportedContact("ewa.lis@contoso.pl", "Ewa Lis", new Dictionary<string, string>()),
        ], "kontakty.csv");
        vm.Message.Subject = "Zaproszenie dla {{nazwa}}";
        vm.Message.BodyHtml = "<p>{{zwrot}},</p><p>zapraszamy.</p>";
        vm.Message.DelaySeconds = 0;
        window.CorrespondenceTabs.SelectedItem = window.SendTab;
        await UiTestSupport.WaitForIdleAsync();
        var tabsTop = TopOf(window.CorrespondenceTabs, window);

        // At rest: the four buttons that start a run are action buttons over the one operation of the page, and there is no
        // separate Stop button and no bar.
        Assert.All(
            new[] { window.SendTestButton, window.CreateDraftsButton, window.SendButton, window.RetryFailedButton },
            button =>
            {
                Assert.Equal(ActionButtonState.Idle, button.State);
                Assert.Same(vm.Send.Status.CancelCommand, button.CancelCommand);
            });
        Assert.Same(vm.Send.SendCommand, window.SendButton.Command);
        AssertNoOldFeedback(window);

        // The first message is being sent: the test holds it.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        outlook.BeforeSend = (_, _) =>
        {
            held.Set();
            release.Wait(HoldLimit);
        };
        try
        {
            ControlHost.Press(window.SendButton);
            await UiWait.UntilAsync(() => held.IsSet && line.Kind == PageStatusKind.Working, "the first message is being sent");

            // Working: how far, with Stop, nothing to close - and the button is the Cancel of the run.
            Assert.Equal("Working", line.Title);
            Assert.Equal("Sending messages: 0 of 3…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Running, window.SendButton.State);
            Assert.True(window.SendButton.IsEnabled);
            Assert.Equal(ActionButtonState.Idle, window.SendTestButton.State);
            Assert.False(window.SendTestButton.IsEnabled);
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && other is not ActionButton && !other.IsDescendantOf(line)
                    && (AutomationProperties.GetName(other) == "Stop" || other.Content as string == "Stop"));
            AssertNoOldFeedback(window);
            window.UpdateLayout();
            Assert.Equal(tabsTop, TopOf(window.CorrespondenceTabs, window));   // the line was there before: nothing moved

            // Cancel where you started: "Stopping…" at once, in the button and in the line, while the message is on its way.
            ControlHost.Press(window.SendButton);
            await UiWait.UntilAsync(() => window.SendButton.State == ActionButtonState.Cancelling && vm.Send.Status.IsCancelling, "the run is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => !vm.Send.IsSending && line.Kind == PageStatusKind.Attention, "the run was stopped");
        outlook.BeforeSend = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what was sent, what was not, and what to press - a result, so the line may have grown, and the content with it.
        Assert.Equal("Attention", line.Title);
        Assert.StartsWith("Sending stopped: 1 succeeded, 0 failed, 2 not sent. Press “Send” to send the rest", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.SendButton.State);
        Assert.Equal("1", window.SentCountText.Text);
        window.UpdateLayout();
        Assert.Equal(tabsTop + GrowthOf(line), TopOf(window.CorrespondenceTabs, window), 0.5);

        // Closed: the page has nothing of its own to say.
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal(string.Empty, line.Message);
        Assert.Single(outlook.Succeeded(OutlookSendMode.Send));
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheSearchForTheOwners_IsStoppedByTheButtonThatStartedIt_AndTheLineOffersRetry() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _) = await TestDialogService.CreateContextAsync("en");
        await using var _ = context;
        context.Directory.AddMailboxOwnerSample(context.Company);
        await context.ConnectAsync();
        var window = await CorrespondenceWindowTests.OpenAsync(context, new CorrespondenceRequest([], CorrespondenceStart.MailboxOwners));
        var vm = window.ViewModel;
        var mailboxes = vm.Mailboxes;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var session = context.Directory.Session;
        Assert.Same(window.MailboxesTab, window.CorrespondenceTabs.SelectedItem);
        Assert.Same(mailboxes.FindOwnersCommand, window.FindOwnersButton.Command);
        Assert.Same(mailboxes.Status.CancelCommand, window.FindOwnersButton.CancelCommand);
        Assert.Equal(mailboxes.SummaryText, line.Message);

        // The persons are read after the mailboxes: that read waits for the test.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        session.BeforeOperation = operation =>
        {
            if (operation.Search?.Filter.Contains("givenName=*", StringComparison.Ordinal) == true && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.FindOwnersButton);
            await UiWait.UntilAsync(() => held.IsSet && line.Kind == PageStatusKind.Working, "the owners are being searched");

            // Working with Stop; the button is the Cancel, and there is no separate Stop button or bar.
            Assert.StartsWith("Searching for mailbox owners", line.Message, StringComparison.Ordinal);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(ActionButtonState.Running, window.FindOwnersButton.State);
            Assert.True(window.FindOwnersButton.IsEnabled);
            AssertNoOldFeedback(window);

            ControlHost.Press(window.FindOwnersButton);
            await UiWait.UntilAsync(() => window.FindOwnersButton.State == ActionButtonState.Cancelling && mailboxes.Status.IsCancelling, "the search is stopping");
            Assert.Equal("Stopping…", line.Message);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => !mailboxes.IsRunning && line.Kind == PageStatusKind.Attention, "the search was stopped");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: the list may be incomplete, and Retry is the action of the line - it searches again, to the end.
        Assert.Equal(
            "The search for mailbox owners was stopped: The list may be incomplete. Press “Retry” to search again.",
            line.Message);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(mailboxes.FindOwnersCommand, action.Command);
        Assert.Equal(ActionButtonState.Idle, window.FindOwnersButton.State);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !mailboxes.IsRunning && line.Kind == PageStatusKind.Idle && mailboxes.Rows.Count > 0, "the search ran to its end");
        Assert.Equal(mailboxes.SummaryText, line.Message);
        Assert.Equal(mailboxes.View.Count, window.MailboxesGrid.Items.Count);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheTwoReadsOfSentItems_AreActionButtons_AndTheirSummaryIsTheLines() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _) = await TestDialogService.CreateContextAsync("en");
        await using var _ = context;
        await context.ConnectAsync();
        var sent = new DateTimeOffset(DateTime.Now.Date.AddDays(-3), TimeSpan.Zero);
        context.Outlook!.Campaigns.Add(new OutlookCampaign("4f1c2a9e0b7d4e6f8a3b5c7d9e1f2a3b", "Czy skrzynka jest nadal potrzebna?", sent, sent.AddMinutes(5), 2));
        context.Outlook.Responses["4f1c2a9e0b7d4e6f8a3b5c7d9e1f2a3b"] =
        [
            new OutlookVoteResponse("jan.kowalski@contoso.test", null, null, sent, "Czy skrzynka jest nadal potrzebna?", ["sekretariat@contoso.test"]),
            new OutlookVoteResponse("anna.nowak@contoso.test", null, null, sent, "Czy skrzynka jest nadal potrzebna?", ["biuro@contoso.test"]),
        ];
        var window = await CorrespondenceWindowTests.OpenAsync(context, new CorrespondenceRequest([]));
        var vm = window.ViewModel;
        var line = window.PageStatus;
        window.CorrespondenceTabs.SelectedItem = window.ResponsesTab;
        await UiTestSupport.WaitForIdleAsync();

        Assert.Same(vm.Responses.LoadCampaignsCommand, window.LoadCampaignsButton.Command);
        Assert.Same(vm.Responses.Status.CancelCommand, window.LoadCampaignsButton.CancelCommand);
        Assert.Same(vm.Responses.LoadResponsesCommand, window.LoadResponsesButton.Command);
        Assert.Same(vm.Responses.Status.CancelCommand, window.LoadResponsesButton.CancelCommand);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);

        ControlHost.Press(window.LoadCampaignsButton);
        await UiWait.UntilAsync(() => !vm.Responses.IsLoading && vm.Responses.Campaigns.Count == 1, "the campaigns were found");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("Runs found: 1 – choose one and read its responses.", line.Message);

        ControlHost.Press(window.LoadResponsesButton);
        await UiWait.UntilAsync(() => !vm.Responses.IsLoading && vm.Responses.Rows.Count == 2, "the responses were read");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(vm.Responses.SummaryText, line.Message);
        Assert.Equal(ActionButtonState.Idle, window.LoadResponsesButton.State);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == vm.Responses.SummaryText);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task OutlookThatCannotBeUsed_IsTheStateOfThePagesThatNeedIt_WithItsWayOut() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _) = await TestDialogService.CreateContextAsync("en");
        await using var _ = context;
        context.Outlook!.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.NotResponding, "connect");
        await context.ConnectAsync();
        var window = await CorrespondenceWindowTests.OpenAsync(context, new CorrespondenceRequest([]));
        var vm = window.ViewModel;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // The recipients do not need Outlook: nothing but their counts.
        Assert.Equal(PageStatusKind.Idle, line.Kind);

        // Sending does: the state of the page, which cannot be closed, with the connection as its action.
        window.CorrespondenceTabs.SelectedItem = window.SendTab;
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention, "the state of Outlook on the send page");
        Assert.StartsWith("Outlook is not available: ", line.Message, StringComparison.Ordinal);
        Assert.Contains("Outlook does not respond", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal("Connect to Outlook", Assert.IsType<string>(action.Content));
        Assert.Same(vm.Outlook.ConnectCommand, action.Command);
        window.CorrespondenceTabs.SelectedItem = window.ResponsesTab;
        await UiWait.UntilAsync(() => line.Message.StartsWith("Outlook is not available: ", StringComparison.Ordinal), "the same state on the responses page");

        // Outlook answers again: pressing the action connects, and the state is gone.
        context.Outlook.ConnectFailure = null;
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => vm.Outlook.IsConnected && line.Kind == PageStatusKind.Idle, "Outlook is connected");
        Assert.Equal(string.Empty, line.Message);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheEndOfTheSession_IsSaidByThePagesThatReadTheDirectory_ToldApartFromWhatStillWorks() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var window = await CorrespondenceWindowTests.OpenAsync(context, new CorrespondenceRequest([]));
        var vm = window.ViewModel;
        var line = window.PageStatus;
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => vm.IsSessionEnded && line.Kind == PageStatusKind.Attention, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // On the recipients tab: what ended, what is off and what still works; it cannot be closed.
        Assert.StartsWith("The session this window was opened in has ended", line.Message, StringComparison.Ordinal);
        Assert.EndsWith("Sending through Outlook still works.", line.Message, StringComparison.Ordinal);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.PickUsersButton.IsEnabled);
        Assert.False(window.AddGroupMembersButton.IsEnabled);

        // On the send tab nothing of it: the messages are sent through Outlook.
        window.CorrespondenceTabs.SelectedItem = window.SendTab;
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the send tab says nothing of the session");
        Assert.Null(vm.Status.Lasting);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(CorrespondenceWindow window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<System.Windows.Controls.ProgressBar>(window));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Same(window.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(window)));
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
}
