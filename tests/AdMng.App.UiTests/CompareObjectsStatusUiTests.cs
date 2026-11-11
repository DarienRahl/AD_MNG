using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Presentation.Comparison;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of "Compare with an object in another connection" (track V, stage V2b;
/// docs/dev/v-interface-renewal.md "V2b-1 as built"), through the real window over two fake directories: one line at
/// the top - the running comparison with Stop, which object was found, a comparison that was stopped or failed with
/// its action, the end of the session - and "Compare" as an action button that is the Cancel of the comparison. What
/// belongs to one part of the window is a note there, not a status. A slow domain controller is a read the test holds
/// and releases, a failing one an exception of the fake session; every state that is asserted lasts until the test
/// changes it. What is compared is covered by <see cref="CompareObjectsWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CompareObjectsStatusUiTests
{
    private const string Name = "Łukasz Wiśniewski";

    /// <summary>How long a held read waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_WithTheObjectThatWasFound() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, comparison, _, company, _) = await OpenAsync(context);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, above the connection box and the tabs, of
        // the fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(comparison);
        Assert.Same(comparison.Status.Current, line.Status);
        Assert.Same(comparison.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.TargetBox, window), "the status line is not above the connection box");
        Assert.True(TopOf(window.TargetBox, window) < TopOf(window.Tabs, window), "the connection box is not above the tabs");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: which object was found - in the line, and not a second time in the footer.
        Assert.True(comparison.HasResult);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("Found: " + company.LukaszWisniewski, line.Message);
        Assert.Equal(comparison.StatusText, line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == comparison.StatusText);

        // "Compare" is an action button at rest - over the command of before and the Cancel of the window's operation -
        // and still the default button; there is no separate Stop button.
        Assert.Equal(ActionButtonState.Idle, window.CompareButton.State);
        Assert.Same(comparison.CompareCommand, window.CompareButton.Command);
        Assert.Same(comparison.Status.CancelCommand, window.CompareButton.CancelCommand);
        Assert.True(window.CompareButton.IsDefault);
        Assert.True(window.CompareButton.IsEnabled);
        Assert.True(window.RefreshButton.IsEnabled);
        Assert.DoesNotContain(UiWait.Descendants<Button>(window), other => other.IsVisible && other.Content as string == "Stop");

        // Nothing to note: another connection is open, both sides are connected, the objects are two.
        Assert.False(window.NoTargetsBar.IsVisible);
        Assert.False(window.SameObjectBar.IsVisible);
        Assert.Empty(VisibleNotes(window.SideAHeader));
        Assert.Empty(VisibleNotes(window.SideBHeader));
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task AComparison_IsSaidInTheLine_CompareIsItsCancel_AndTheStoppedComparisonIsAttentionWithCompare() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, comparison, fabrikam, _, _) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var connection = comparison.SelectedTarget!.Label;
        var tabsTop = TopOf(window.Tabs, window);

        // The read of the matched object in the other connection waits for the test - and answers like a cancelled
        // request once it is let go (by then the comparison was stopped).
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        fabrikam.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Get
                && operation.DistinguishedName.Contains(Name, StringComparison.Ordinal)
                && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
                throw new OperationCanceledException();
            }
        };
        try
        {
            ControlHost.Press(window.CompareButton);
            await UiWait.UntilAsync(() => held.IsSet && comparison.IsRunning && line.IsWorking, "the comparison runs");

            // Working: what is compared and with which connection - with Stop, and nothing to close.
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Comparing “{Name}” with the connection {connection}…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);

            // "Compare" is the Cancel of the comparison: enabled although its command cannot execute. Refresh, which
            // would start the same comparison, waits. There is no separate Stop button and no progress bar.
            Assert.Equal(ActionButtonState.Running, window.CompareButton.State);
            Assert.True(window.CompareButton.IsEnabled);
            Assert.False(comparison.CompareCommand.CanExecute(null));
            Assert.False(window.RefreshButton.IsEnabled);
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && other is not ActionButton && !other.IsDescendantOf(line) && other.Content as string == "Stop");
            AssertNoOldFeedback(window);

            // The line was there before: the tabs did not move.
            window.UpdateLayout();
            Assert.Equal(tabsTop, TopOf(window.Tabs, window));

            // Cancel where you started: "Stopping…" on the button and in the line at once - the read that is on its
            // way to the domain controller still has to return.
            ControlHost.Press(window.CompareButton);
            await UiWait.UntilAsync(
                () => window.CompareButton.State == ActionButtonState.Cancelling && comparison.Status.IsCancelling, "the comparison is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(window.CompareButton.IsEnabled);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => !comparison.IsRunning && line.Kind == PageStatusKind.Attention, "the comparison was stopped");
        fabrikam.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what the tabs may lack, and "Compare" as the way to compare again - a result that can be closed.
        Assert.Equal("Attention", line.Title);
        Assert.Equal($"Comparing “{Name}” was stopped: the tabs may not show everything. Press “Compare” to compare again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Compare", Assert.IsType<string>(action.Content));
        Assert.Same(comparison.CompareCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.CompareButton.State);
        window.UpdateLayout();
        Assert.Equal(tabsTop, TopOf(window.Tabs, window));

        // "Compare", pressed in the line, compares: which object was found.
        await UiWait.UntilAsync(() => action.IsEnabled && window.CompareButton.IsEnabled, "a comparison can be started again");
        ControlHost.Press(action);
        await UiWait.UntilAsync(
            () => !comparison.IsRunning && !comparison.IsNestedLoading && line.Kind == PageStatusKind.Idle, "the comparison ran");
        Assert.True(comparison.HasResult);
        Assert.StartsWith("Found: ", line.Message, StringComparison.Ordinal);
        Assert.Equal(comparison.StatusText, line.Message);
        Assert.Equal(ActionButtonState.Idle, window.CompareButton.State);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AComparisonThatFails_IsAnErrorWithRetry_ASideThatEndedIsANote_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, comparison, fabrikam, _, other) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // The other domain controller does not answer the read of the matched object any more.
        var fail = 1;
        fabrikam.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1
                && operation.Kind == FakeOperationKind.Get
                && operation.DistinguishedName.Contains(Name, StringComparison.Ordinal))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(window.CompareButton);
        await UiWait.UntilAsync(() => !comparison.IsRunning && line.Kind == PageStatusKind.Error, "the comparison failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The line says why: what was tried, the reason, what to do, the code - with Retry, and it cannot be closed.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(comparison.ErrorText);
        Assert.Equal($"Could not compare “{Name}”: {comparison.ErrorText} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(comparison.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.CompareButton.State);
        AssertNoOldFeedback(window);

        // Retry, pressed in the line, compares again.
        Volatile.Write(ref fail, 0);
        await UiWait.UntilAsync(() => action.IsEnabled, "Retry can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(
            () => !comparison.IsRunning && !comparison.IsNestedLoading && line.Kind == PageStatusKind.Idle, "the comparison ran");
        fabrikam.BeforeOperation = null;
        Assert.Null(comparison.ErrorText);
        Assert.Equal(comparison.StatusText, line.Message);
        var found = line.Message;
        Assert.StartsWith("Found: ", found, StringComparison.Ordinal);

        // The other connection changes its session: a note in the card of that side - the results stay, and the line
        // keeps saying what the tabs show.
        await other.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => comparison.SideB.IsEnded, "side B ended");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.NotNull(comparison.SideB.EndedMessage);
        var note = Assert.Single(VisibleNotes(window.SideBHeader));
        Assert.Contains(UiWait.Descendants<TextBlock>(note), text => text.IsVisible && text.Text == comparison.SideB.EndedMessage);
        Assert.Empty(VisibleNotes(window.SideAHeader));
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(found, line.Message);
        Assert.True(comparison.HasResult);
        AssertNoOldFeedback(window);

        // The session of the window ends: an error that cannot be closed takes the line - said once, not a second time
        // in the card of side A - and nothing can be compared any more.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => comparison.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.True(comparison.SideA.IsEnded);
        Assert.Empty(VisibleNotes(window.SideAHeader));
        Assert.False(window.CompareButton.IsEnabled);
        Assert.False(window.RefreshButton.IsEnabled);
        Assert.Equal(ActionButtonState.Idle, window.CompareButton.State);
        Assert.True(window.CloseButton.IsEnabled);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheSameObjectOnBothSides_IsANoteAboveTheTabs_NotAStatus() => WpfTestHost.RunAsync(async () =>
    {
        // Two accounts in one domain: both sides read the same object.
        await using var context = await UiTestContext.CreateAsync();
        context.Connector.NonOwning = true;
        await UiTestContext.ConnectAsync(context.Services, FakeDirectoryBuilder.DefaultDomain, "jan.kowalski");
        await UiTestContext.ConnectAsync(context.CreateConnection(), FakeDirectoryBuilder.DefaultDomain, "anna.zielinska");
        var (window, comparison) = await ShowAsync(context);

        Assert.True(comparison.HasResult);
        Assert.True(comparison.IsSameObject);
        Assert.True(window.SameObjectBar.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.SameObjectBar.Style);
        Assert.True(window.SameObjectText.IsVisible);
        Assert.Equal(context.Localizer()[L.Comparison.SameObject], window.SameObjectText.Text);
        Assert.True(TopOf(window.SameObjectBar, window) < TopOf(window.Tabs, window), "the note is not above the tabs");

        // The line says what the window did - which object was found - and nothing about how to read the tabs.
        Assert.Equal(PageStatusKind.Idle, window.PageStatus.Kind);
        Assert.Equal("Found: " + context.Company.LukaszWisniewski, window.PageStatus.Message);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The comparison window of Łukasz Wiśniewski in contoso.test (as jan.kowalski) with one other connection open -
    /// fabrikam.test as anna.zielinska - after the comparison the window starts by itself, with its background reads
    /// through. Also returns the fake directory of fabrikam.test (the hooks of a test go there), its sample company
    /// and the scope of the other connection.
    /// </summary>
    private static async Task<Opened> OpenAsync(UiTestContext context)
    {
        var company = await ComparisonFixture.AddFabrikamAsync(context);
        await UiTestContext.ConnectAsync(context.Services, FakeDirectoryBuilder.DefaultDomain, "jan.kowalski");
        var other = context.CreateConnection();
        var session = await UiTestContext.ConnectAsync(other, ComparisonFixture.Fabrikam, "anna.zielinska");
        var fabrikam = Assert.IsType<FakeDomainControllerSession>(session).Directory;
        var (window, comparison) = await ShowAsync(context);
        return new Opened(window, comparison, fabrikam, company, other);
    }

    /// <summary>Shows the comparison window of Łukasz Wiśniewski, owned by the main window, and waits for its first comparison.</summary>
    private static async Task<(CompareObjectsWindow Window, ObjectComparisonViewModel Comparison)> ShowAsync(UiTestContext context)
    {
        UiTestSupport.ShowOffScreen(context.Services.GetRequiredService<MainWindow>());
        var comparison = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateObjectComparison(context.Company.LukaszWisniewski);
        var window = (CompareObjectsWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(comparison);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiWait.UntilAsync(
            () => comparison.HasResult && !comparison.IsRunning && !comparison.IsNestedLoading && window.PageStatus.Kind == PageStatusKind.Idle,
            "the first comparison");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Null(comparison.ErrorText);
        return (window, comparison);
    }

    /// <summary>The notes that are shown inside <paramref name="root"/> (the card of one side).</summary>
    private static List<ContentControl> VisibleNotes(DependencyObject root)
    {
        var attention = Application.Current.FindResource("Ad.Note.Attention");
        return [.. UiWait.Descendants<ContentControl>(root).Where(note => ReferenceEquals(note.Style, attention) && note.IsVisible)];
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(CompareObjectsWindow window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Same(window.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(window)));
    }

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;

    /// <summary>What <see cref="OpenAsync"/> opened.</summary>
    /// <param name="Window">The comparison window.</param>
    /// <param name="Comparison">Its view model.</param>
    /// <param name="Fabrikam">The fake directory of the other connection.</param>
    /// <param name="Company">The sample company of that directory.</param>
    /// <param name="Other">The scope of the other connection.</param>
    private sealed record Opened(
        CompareObjectsWindow Window, ObjectComparisonViewModel Comparison, FakeDirectorySession Fabrikam, SampleCompany Company, IServiceProvider Other);
}
