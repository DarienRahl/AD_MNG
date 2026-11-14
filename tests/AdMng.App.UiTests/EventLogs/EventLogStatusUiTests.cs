using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.EventLogs;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.EventLogs;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using Microsoft.Extensions.DependencyInjection;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.EventLogs;

/// <summary>
/// The status line of the three event log documents (track V, stage V2b-4; docs/dev/v-interface-renewal.md "V2b-4 as built"),
/// through the real views over the fake directory and the fake event logs: one line under the header of each - the summary of
/// the last run, the run with its stage and Stop, the sources that were not read, a stopped run with "Refresh", the window that
/// cannot read event logs - and the run buttons as action buttons that are the Cancel of their own run. A slow event log is a
/// read the test holds; every state that is asserted lasts until the test changes it. What the documents read and show is
/// covered by <see cref="EventLogDocumentsUiTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class EventLogStatusUiTests
{
    private static async Task<MainWindow> OpenMainAsync(UiTestContext context)
    {
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        return main;
    }

    private static async Task<(EventLogReaderView View, EventLogReaderDocumentViewModel Reader)> OpenReaderAsync(MainWindow main)
    {
        main.Shell.OpenEventLogsCommand.Execute(null);
        var reader = Assert.IsType<EventLogReaderDocumentViewModel>(main.Shell.SelectedTab);
        await UiWait.UntilAsync(() => reader.Sources.Count == 2, "the domain controllers as sources");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<EventLogReaderView>(main));
        Assert.Same(reader, view.DataContext);
        return (view, reader);
    }

    /// <summary>
    /// The first read of an event log waits until the test lets it go. It does not hear the Stop while it waits - the read that
    /// is on its way to the event log has not returned when the operator presses Stop - so "Stopping…" lasts until the test
    /// releases it (and ends with the token then, as the real reader does).
    /// </summary>
    private static (TaskCompletionSource Reading, TaskCompletionSource Release) Hold(Testing.EventLogs.FakeEventLogConnector connector)
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.BeforeRead = async (_, _, token) =>
        {
            reading.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(60));
            token.ThrowIfCancellationRequested();
        };
        return (reading, release);
    }

    [Fact]
    public Task EveryDocument_HasOneStatusLine_UnderItsHeader_WithItsSummary_AndTheRunButtonIsAnActionButton() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        EventLogUiSupport.AddFailedLogons(connector);
        await using var context = await UiTestContext.CreateAsync("en", EventLogUiSupport.Use(connector));
        await EventLogUiSupport.AddDomainControllersAsync(context);
        EventLogUiSupport.LockOutJan(context, connector);
        var main = await OpenMainAsync(context);
        var shell = main.Shell;

        // The reader: before the first run it says what to do, afterwards what was read.
        var (reader, readerDocument) = await OpenReaderAsync(main);
        var line = reader.PageStatus;
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(reader)));
        Assert.IsAssignableFrom<IHasPageStatus>(readerDocument);
        Assert.Same(readerDocument.Status.Current, line.Status);
        Assert.Same(readerDocument.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(reader.TitleText, reader) < TopOf(line, reader), "the status line is not under the header");
        Assert.True(TopOf(line, reader) < TopOf(reader.QueryScroll, reader), "the status line is not above the query");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(context.Localizer()[L.EventLogs.NeverRun], line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        await readerDocument.RunCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(readerDocument.StatusText, line.Message);
        Assert.DoesNotContain(UiWait.Descendants<TextBlock>(reader), text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == readerDocument.StatusText);
        AssertActionButtons(reader.RunButton, readerDocument.RunCommand, readerDocument.Status);
        AssertActionButtons(reader.LoadChannelsButton, readerDocument.LoadChannelsCommand, readerDocument.Status);
        AssertNoOldFeedback(reader);

        // The failed logons and the lockout investigation say the same things in the same places.
        var failed = Assert.IsType<FailedLogonsDocumentViewModel>(shell.OpenEventLogDocument(new EventLogDocumentRequest(EventLogDocumentKind.FailedLogons)));
        failed.EnumerationThreshold = 3;
        await failed.RunCommand.ExecuteAsync(null);
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var failedView = Assert.Single(UiWait.Descendants<FailedLogonsView>(main));
        Assert.Same(failedView.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(failedView)));
        Assert.True(TopOf(failedView.TitleText, failedView) < TopOf(failedView.PageStatus, failedView), "the status line is not under the header");
        Assert.True(TopOf(failedView.PageStatus, failedView) < TopOf(failedView.WindowBox, failedView), "the status line is not above the window");
        Assert.Same(failed.Status.Current, failedView.PageStatus.Status);
        Assert.Equal(PageStatusKind.Idle, failedView.PageStatus.Kind);
        Assert.StartsWith(failed.SummaryText, failedView.PageStatus.Message, StringComparison.Ordinal);
        AssertActionButtons(failedView.RunButton, failed.RunCommand, failed.Status);
        AssertNoOldFeedback(failedView);

        await shell.OpenLockoutSourceCommand.ExecuteAsync(context.Company.JanKowalski);
        var lockout = Assert.IsType<LockoutDocumentViewModel>(shell.SelectedTab);
        await UiWait.UntilAsync(() => !lockout.IsLoading && lockout.HasReport, "the lockout report");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var lockoutView = Assert.Single(UiWait.Descendants<LockoutView>(main));
        Assert.Same(lockoutView.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(lockoutView)));
        Assert.True(TopOf(lockoutView.TitleText, lockoutView) < TopOf(lockoutView.PageStatus, lockoutView), "the status line is not under the header");
        Assert.True(TopOf(lockoutView.PageStatus, lockoutView) < TopOf(lockoutView.WindowBox, lockoutView), "the status line is not above the window");
        Assert.Equal(PageStatusKind.Idle, lockoutView.PageStatus.Kind);
        Assert.StartsWith(lockout.LoadedText, lockoutView.PageStatus.Message, StringComparison.Ordinal);
        AssertActionButtons(lockoutView.RunButton, lockout.RunCommand, lockout.Status);
        AssertNoOldFeedback(lockoutView);
        context.AssertHealthy();
    });

    [Fact]
    public Task ARun_SaysItsSourcesAndEvents_TheRunButtonIsItsCancel_AndAStoppedRunIsAnAttentionWithRefresh() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        await using var context = await UiTestContext.CreateAsync("en", EventLogUiSupport.Use(connector));
        await EventLogUiSupport.AddDomainControllersAsync(context);
        EventLogUiSupport.LockOutJan(context, connector);
        var main = await OpenMainAsync(context);
        var (view, reader) = await OpenReaderAsync(main);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var queryTop = TopOf(view.QueryScroll, view);
        var (reading, release) = Hold(connector);

        ControlHost.Press(view.RunButton);
        await UiWait.UntilAsync(() => reading.Task.IsCompleted && reader.IsLoading && line.IsWorking, "the run is held");

        // Working: what is read and how far it is - with Stop, and nothing to close.
        Assert.Equal(PageStatusKind.Working, line.Kind);
        Assert.Equal("Working", line.Title);
        Assert.Matches(@"^Reading the event logs( – sources: \d of 2, events: \d+)?…$", line.Message);
        Assert.True(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Stop", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Collapsed, close.Visibility);

        // The run button is the Cancel of the run - enabled although its command cannot execute -, the other start button
        // waits, and no other button says "Cancel" or "Stop".
        Assert.Equal(ActionButtonState.Running, view.RunButton.State);
        Assert.True(view.RunButton.IsEnabled);
        Assert.False(reader.RunCommand.CanExecute(null));
        Assert.Equal(ActionButtonState.Idle, view.LoadChannelsButton.State);
        Assert.False(view.LoadChannelsButton.IsEnabled);
        Assert.DoesNotContain(
            UiWait.Descendants<Button>(view),
            button => button is not ActionButton && !button.IsDescendantOf(line) && (button.Content as string is "Cancel" or "Stop"));
        AssertNoOldFeedback(view);

        // The line was there before: the query did not move.
        view.UpdateLayout();
        Assert.Equal(queryTop, TopOf(view.QueryScroll, view));

        // Cancel where you started: "Stopping…" on the button and in the line at once.
        ControlHost.Press(view.RunButton);
        await UiWait.UntilAsync(() => view.RunButton.State == ActionButtonState.Cancelling && reader.Status.IsCancelling, "the run is stopping");
        Assert.Equal(PageStatusKind.Working, line.Kind);
        Assert.Equal("Stopping…", line.Message);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.True(view.RunButton.IsEnabled);

        // The read that was on its way returns: the run ends.
        release.TrySetResult();
        await UiWait.UntilAsync(() => !reader.IsLoading && line.Kind == PageStatusKind.Attention, "the run was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what was read stays and is counted, and "Refresh" reads again.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Reading the event logs was stopped; events read and shown: 0. Press “Refresh” to read again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(reader.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, view.RunButton.State);
        Assert.True(view.RunButton.IsEnabled);
        view.UpdateLayout();
        Assert.Equal(queryTop + GrowthOf(line), TopOf(view.QueryScroll, view), 0.5);

        // "Refresh", pressed in the line, reads again: the summary of the run.
        connector.BeforeRead = null;
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !reader.IsLoading && line.Kind == PageStatusKind.Idle && reader.Rows.Count == 7, "the run ran again");
        Assert.Equal(reader.StatusText, line.Message);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ASourceThatCouldNotBeRead_IsNamedByTheLine_AndItsReasonStaysInItsRow() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        await using var context = await UiTestContext.CreateAsync("en", EventLogUiSupport.Use(connector));
        await EventLogUiSupport.AddDomainControllersAsync(context);
        EventLogUiSupport.LockOutJan(context, connector);
        connector.FailHost(EventLogUiSupport.Dc02, EventLogAccessError.RpcUnavailable, 1722);
        var main = await OpenMainAsync(context);
        var (view, reader) = await OpenReaderAsync(main);
        var line = view.PageStatus;
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        await reader.RunCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal(PageStatusKind.Attention, line.Kind);
        Assert.Equal(
            "Reading the event logs: sources not read: 1 of 2 (dc02.contoso.test). The reason of each is in “Coverage and access”.",
            line.Message);
        Assert.False(line.HasAction);
        Assert.Equal(Visibility.Visible, close.Visibility);

        // The reason is the row of the panel, with its remedy: the line names the source, the row says why.
        Assert.Equal(2, view.HostsList.Items.Count);
        Assert.Contains("1722", reader.Hosts.Single(h => h.Host == EventLogUiSupport.Dc02).ErrorText, StringComparison.Ordinal);
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal(reader.StatusText, line.Message);
        AssertNoOldFeedback(view);
        context.AssertHealthy();
    });

    [Fact]
    public Task AWindowThatCannotReadEventLogs_SaysSoInTheLine_OfTheReaderAndTheTwoAnalyses() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        connector.IsSupported = false;
        await using var context = await UiTestContext.CreateAsync("en", EventLogUiSupport.Use(connector));
        await EventLogUiSupport.AddDomainControllersAsync(context);
        var main = await OpenMainAsync(context);
        var shell = main.Shell;

        var reader = Assert.IsType<EventLogReaderDocumentViewModel>(shell.OpenEventLogDocument(new EventLogDocumentRequest(EventLogDocumentKind.Reader)));
        var failed = Assert.IsType<FailedLogonsDocumentViewModel>(shell.OpenEventLogDocument(new EventLogDocumentRequest(EventLogDocumentKind.FailedLogons)));
        var lockout = Assert.IsType<LockoutDocumentViewModel>(shell.OpenEventLogDocument(
            new EventLogDocumentRequest(EventLogDocumentKind.Lockout) { UserDn = context.Company.JanKowalski, Name = "Jan Kowalski" }));

        var expected = context.Localizer()[L.EventLogs.NotSupported];
        foreach (var (document, viewType) in new (EventLogDocumentBase, Type)[]
        {
            (reader, typeof(EventLogReaderView)),
            (failed, typeof(FailedLogonsView)),
            (lockout, typeof(LockoutView)),
        })
        {
            // The view of a tab exists while the tab is the selected one.
            shell.SelectedTab = document;
            main.UpdateLayout();
            await UiTestSupport.WaitForIdleAsync(rounds: 3);
            var view = Assert.Single(UiWait.Descendants<UserControl>(main), control => control.GetType() == viewType);
            var line = UiWait.Descendants<StatusLine>(view).Single();
            Assert.Equal(PageStatusKind.Attention, line.Kind);
            Assert.Equal(expected, line.Message);
            Assert.False(line.HasAction);
            Assert.False(line.CanClose);
            Assert.Equal(Visibility.Collapsed, ControlHost.Part<Button>(line, "PART_CloseButton").Visibility);
            Assert.False(document.RunCommandIsEnabled());
            AssertNoOldFeedback(view);
        }

        context.AssertHealthy();
    });

    [Fact]
    public Task LoadingTheChannels_IsAnActionButtonOfItsOwn_AndTheSummaryOfTheLastRunStays() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        await using var context = await UiTestContext.CreateAsync("en", EventLogUiSupport.Use(connector));
        await EventLogUiSupport.AddDomainControllersAsync(context);
        EventLogUiSupport.LockOutJan(context, connector);
        var main = await OpenMainAsync(context);
        var (view, reader) = await OpenReaderAsync(main);
        var line = view.PageStatus;
        await reader.RunCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var summary = line.Message;

        ControlHost.Press(view.LoadChannelsButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Done, "the channels were loaded");

        Assert.Equal("Done", line.Title);
        Assert.Equal($"{EventLogUiSupport.Dc01}: 2 channels loaded.", line.Message);
        Assert.Equal(AppIcons.StatusOk, line.IconKey);
        Assert.Equal(ActionButtonState.Idle, view.LoadChannelsButton.State);
        Assert.Equal(summary, reader.StatusText);
        ControlHost.Press(ControlHost.Part<Button>(line, "PART_CloseButton"));
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal(summary, line.Message);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A run button at rest: an action button over the command of before and the Cancel of the document's holder.</summary>
    private static void AssertActionButtons(ActionButton button, System.Windows.Input.ICommand command, PageStatusHolder status)
    {
        Assert.Equal(ActionButtonState.Idle, button.State);
        Assert.Same(command, button.Command);
        Assert.Same(status.CancelCommand, button.CancelCommand);
    }

    /// <summary>The old places of feedback are gone from the view: no progress bar, no banner of the styles of before, one line.</summary>
    private static void AssertNoOldFeedback(FrameworkElement view)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(view));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(view),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Single(UiWait.Descendants<StatusLine>(view));
    }
}

internal static class EventLogDocumentExtensions
{
    /// <summary>The run command of a document can run (the three documents have one of their own).</summary>
    public static bool RunCommandIsEnabled(this EventLogDocumentBase document) => document switch
    {
        EventLogReaderDocumentViewModel reader => reader.RunCommand.CanExecute(null),
        FailedLogonsDocumentViewModel failed => failed.RunCommand.CanExecute(null),
        LockoutDocumentViewModel lockout => lockout.RunCommand.CanExecute(null),
        _ => false,
    };
}
