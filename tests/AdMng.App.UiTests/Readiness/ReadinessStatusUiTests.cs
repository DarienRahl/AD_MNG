using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Readiness;
using AdMng.App.UiTests.EventLogs;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Readiness;
using AdMng.Presentation.Session;
using Microsoft.Extensions.DependencyInjection;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.Readiness;

/// <summary>
/// The status line of the "Gotowość środowiska" tab (track V, stage V2b-4; docs/dev/v-interface-renewal.md "V2b-4 as
/// built"), through the real view over the fake directory and the fake event logs: one line under the header - what the last
/// check found and when, the running check with the area it is at and Stop, the check that was stopped with "Check again",
/// the end of the session -, the two check buttons as action buttons that are the Cancel of their check, and the read-only
/// and export sentences as a note. A slow event log is a read the test holds; every state that is asserted lasts until the
/// test changes it. What the checks find is covered by <see cref="ReadinessUiTests"/> and the Presentation tests.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ReadinessStatusUiTests
{
    /// <summary>The tab of a window connected to the sample domain with two DCs; the quick check has run.</summary>
    private static async Task<(MainWindow Main, ReadinessView View, ReadinessDocumentViewModel Document)> OpenAsync(UiTestContext context)
    {
        await EventLogUiSupport.AddDomainControllersAsync(context);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        main.Shell.ShowReadinessCommand.Execute(null);
        var document = Assert.IsType<ReadinessDocumentViewModel>(main.Shell.SelectedTab);
        await UiWait.UntilAsync(() => !document.IsLoading && document.HasResult && !document.Status.IsWorking, "the quick check");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<ReadinessView>(main));
        Assert.Same(document, view.DataContext);
        return (main, view, document);
    }

    [Fact]
    public Task TheTab_HasOneStatusLine_UnderTheHeader_WithTheSummary_AndTheSentencesOfTheTabAreANote() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        await using var context = await UiTestContext.CreateAsync("en", EventLogUiSupport.Use(connector));
        var (main, view, document) = await OpenAsync(context);
        var line = view.PageStatus;

        // One status line, named PageStatus, over the status of the tab, under the header and above the test computer, the
        // note and the areas, of the fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(view)));
        Assert.IsAssignableFrom<IHasPageStatus>(document);
        Assert.Same(document.Status.Current, line.Status);
        Assert.Same(document.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(view.TitleText, view) < TopOf(line, view), "the status line is not under the header");
        Assert.True(TopOf(line, view) < TopOf(view.TestComputerBox, view), "the status line is not above the test computer");
        Assert.True(TopOf(view.TestComputerBox, view) < TopOf(view.NotesBar, view), "the note is not under the test computer");
        Assert.True(TopOf(view.NotesBar, view) < TopOf(view.GroupsList, view), "the areas are not under the note");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // What the quick check found and when and where it was made - in the line, and not a second time in the tab.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal($"{document.SummaryText} · {document.LastRunText}", line.Message);
        Assert.StartsWith("OK: ", line.Message, StringComparison.Ordinal);
        Assert.Contains("Last check: quick, ", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && (text.Text == document.SummaryText || text.Text == document.LastRunText));

        // What the check is and what an export holds are a note - information, true for as long as the tab is open.
        Assert.Same(Application.Current.FindResource("Ad.Note.Info"), view.NotesBar.Style);
        Assert.True(view.NotesBar.IsVisible);
        var localizer = context.Localizer();
        Assert.Equal(localizer[L.Readiness.ReadOnlyNote], view.ReadOnlyNoteText.Text);
        Assert.Equal(localizer[L.Readiness.ExportNote], view.ExportNoteText.Text);

        // The two check buttons are action buttons over the commands of before and the Cancel of the tab's check; there is
        // no separate Cancel button any more.
        Assert.Equal(ActionButtonState.Idle, view.QuickCheckButton.State);
        Assert.Same(document.QuickCheckCommand, view.QuickCheckButton.Command);
        Assert.Same(document.Status.CancelCommand, view.QuickCheckButton.CancelCommand);
        Assert.Equal(ActionButtonState.Idle, view.FullCheckButton.State);
        Assert.Same(document.FullCheckCommand, view.FullCheckButton.Command);
        Assert.Same(document.Status.CancelCommand, view.FullCheckButton.CancelCommand);
        Assert.True(view.QuickCheckButton.IsEnabled);
        Assert.True(view.FullCheckButton.IsEnabled);
        Assert.DoesNotContain(
            UiWait.Descendants<Button>(view),
            button => button is not ActionButton && !button.IsDescendantOf(line) && (button.Content as string is "Cancel" or "Stop"));
        AssertNoOldFeedback(view);
        context.AssertHealthy();
    });

    [Fact]
    public Task AFullCheck_SaysTheAreaItIsAt_ItsButtonIsItsCancel_AndAStoppedCheckLastsWithCheckAgain() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        await using var context = await UiTestContext.CreateAsync("en", EventLogUiSupport.Use(connector));
        var (main, view, document) = await OpenAsync(context);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var areasTop = TopOf(view.GroupsList, view);

        // The first read of an event log waits until the check is stopped.
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.BeforeRead = async (_, _, cancellationToken) =>
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };

        ControlHost.Press(view.FullCheckButton);
        await UiWait.UntilAsync(() => reading.Task.IsCompleted && document.IsLoading && line.IsWorking, "the full check runs");

        // Working: the check, how far it is and the area it is at - with Stop, and nothing to close.
        Assert.Equal(PageStatusKind.Working, line.Kind);
        Assert.Equal("Working", line.Title);
        Assert.Equal("Full check: 5 of 8 – Event logs (E)…", line.Message);
        Assert.True(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Stop", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Collapsed, close.Visibility);

        // The button that started the check is its Cancel - enabled although its command cannot execute -, the other one
        // waits, and nothing else says "Stop".
        Assert.Equal(ActionButtonState.Running, view.FullCheckButton.State);
        Assert.True(view.FullCheckButton.IsEnabled);
        Assert.False(document.FullCheckCommand.CanExecute(null));
        Assert.Equal(ActionButtonState.Idle, view.QuickCheckButton.State);
        Assert.False(view.QuickCheckButton.IsEnabled);
        AssertNoOldFeedback(view);

        // The line was there before: the areas did not move.
        view.UpdateLayout();
        Assert.Equal(areasTop, TopOf(view.GroupsList, view));

        // Cancel where you started: "Stopping…" on the button and in the line at once.
        ControlHost.Press(view.FullCheckButton);
        await UiWait.UntilAsync(() => view.FullCheckButton.State == ActionButtonState.Cancelling && document.Status.IsCancelling, "the check is stopping");
        Assert.Equal(PageStatusKind.Working, line.Kind);
        Assert.Equal("Stopping…", line.Message);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.True(view.FullCheckButton.IsEnabled);

        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Attention, "the check was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what the tab shows is incomplete - the state of the tab, which cannot be closed - and "Check again"
        // is the way out.
        Assert.Equal("Attention", line.Title);
        Assert.Equal(
            "The check was stopped: the checks it did not reach are shown as not checked. Press “Check again” to check again.",
            line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Check again", Assert.IsType<string>(action.Content));
        Assert.Same(document.CheckAgainCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, view.FullCheckButton.State);
        Assert.True(view.FullCheckButton.IsEnabled);
        view.UpdateLayout();
        Assert.Equal(areasTop + GrowthOf(line), TopOf(view.GroupsList, view), 0.5);

        // "Check again", pressed in the line, runs the depth of the shown check once more: the summary of a full check.
        connector.BeforeRead = null;
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Idle && line.Message.Length > 0, "the check ran again");
        Assert.Contains("Last check: full, ", line.Message, StringComparison.Ordinal);
        Assert.False(document.WasCancelled);
        Assert.Equal(ReadinessDepth.Full, document.LastDepth);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheEndOfTheConnection_IsAnErrorThatLasts_AndNothingCanBeChecked() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        await using var context = await UiTestContext.CreateAsync("en", EventLogUiSupport.Use(connector));
        var (main, view, document) = await OpenAsync(context);
        var line = view.PageStatus;
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => !document.HasSession && line.Kind == PageStatusKind.Error, "the end of the connection in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        Assert.Equal("Error", line.Title);
        Assert.Equal(context.Localizer()[L.Errors.NotConnected], line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(view.QuickCheckButton.IsEnabled);
        Assert.False(view.FullCheckButton.IsEnabled);
        Assert.Equal(ActionButtonState.Idle, view.QuickCheckButton.State);
        AssertNoOldFeedback(view);
        context.AssertHealthy();
    });
}
