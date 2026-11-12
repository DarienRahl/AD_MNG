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
using AdMng.Presentation.Filters;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the filter builder (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"), through
/// the real window over the fake directory: one line at the top - empty at rest, the test of the filter with Stop and
/// the Test button as its Cancel, how many objects match, a test that was stopped or failed, why it was not started -
/// and what is wrong with the filter as a note under its preview. A slow domain controller is a search the test holds
/// and releases, a failing one an exception of the fake session; every state that is asserted lasts until the test
/// changes it. How a filter is built is covered by <see cref="AccountReviewWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class FilterEditorStatusUiTests
{
    /// <summary>How long a held search waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_AndWhatIsWrongWithTheFilterIsANote() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, editor) = await OpenAsync(context);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, at the top - above the name and the
        // conditions - of the fixed height, and empty: nothing was tested.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(editor);
        Assert.Same(editor.Status.Current, line.Status);
        Assert.Same(editor.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.NameBox, window), "the status line is not above the name");
        Assert.True(TopOf(window.NameBox, window) < TopOf(window.GroupsList, window), "the name is not above the conditions");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);

        // A new filter has a condition without an attribute: a note under the preview - not a banner, and not the line.
        Assert.NotNull(editor.Error);
        Assert.True(window.ErrorNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Error"), window.ErrorNote.Style);
        Assert.True(window.ErrorText.IsVisible);
        Assert.Equal(editor.Error, window.ErrorText.Text);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(window.ErrorText));
        Assert.True(TopOf(window.PreviewBox, window) < TopOf(window.ErrorNote, window), "the note is not under the preview");

        // Test is an action button - over the command of before and the Cancel of the window's operation - and cannot
        // be pressed while the filter is not valid; Save is still the default button.
        Assert.Equal(ActionButtonState.Idle, window.TestButton.State);
        Assert.Same(editor.TestCommand, window.TestButton.Command);
        Assert.Same(editor.Status.CancelCommand, window.TestButton.CancelCommand);
        Assert.False(window.TestButton.IsEnabled);
        Assert.False(window.TestButton.IsDefault);
        Assert.True(window.SaveButton.IsDefault);
        Assert.True(TopOf(window.ErrorNote, window) < TopOf(window.TestButton, window), "the Test button is not under the note");

        // A valid filter: the note goes, Test can be pressed, the line still has nothing to say.
        Accounting(editor);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(editor.Error);
        Assert.False(window.ErrorNote.IsVisible);
        Assert.True(window.TestButton.IsEnabled);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheTest_SaysCountingWithStop_TestIsItsCancel_AndThenHowManyObjectsMatch() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, editor) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        Accounting(editor);
        await UiTestSupport.WaitForIdleAsync();
        var nameTop = TopOf(window.NameBox, window);

        // The search of the test waits for the test, so "counting" lasts - and "stopping" after Stop.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        session.BeforeOperation = operation =>
        {
            if (operation is { Kind: FakeOperationKind.Search, Search: { Scope: SearchScope.Subtree } search }
                && search.Filter.Contains(Attr.Department, StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.TestButton);
            await UiWait.UntilAsync(() => held.IsSet && line.Kind == PageStatusKind.Working, "the matching objects are being counted");

            // Working: what is counted and where - with Stop, and nothing to close. The Test button is the Cancel of
            // the test: enabled, and there is no separate Stop button.
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Counting the matching objects in {domain}…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Running, window.TestButton.State);
            Assert.True(window.TestButton.IsEnabled);
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && other is not ActionButton && !other.IsDescendantOf(line)
                    && (AutomationProperties.GetName(other) == "Stop" || other.Content as string == "Stop"));
            AssertNoOldFeedback(window);

            // The line was there before: the fields did not move.
            window.UpdateLayout();
            Assert.Equal(nameTop, TopOf(window.NameBox, window));

            // Cancel where you started: "Stopping…" on the button and in the line at once.
            ControlHost.Press(window.TestButton);
            await UiWait.UntilAsync(
                () => window.TestButton.State == ActionButtonState.Cancelling && editor.Status.IsCancelling, "the test is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(window.TestButton.IsEnabled);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention && !editor.TestCommand.IsRunning, "the test was stopped");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what was not done, and Test as the way to count again. The fields moved down by as much as the line grew.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Counting the matching objects was stopped: the filter was not tested. Press “Test” to test again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Test", Assert.IsType<string>(action.Content));
        Assert.Same(editor.TestCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.TestButton.State);
        Assert.True(window.TestButton.IsEnabled);
        window.UpdateLayout();
        Assert.Equal(nameTop + GrowthOf(line), TopOf(window.NameBox, window), 0.5);

        // Test, pressed in the line, counts: done - how many, where, how long it took.
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Done && !editor.TestCommand.IsRunning, "the matching objects were counted");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Done", line.Title);
        Assert.StartsWith($"Matching objects in {domain}: 3 (", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(" s).", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusOk, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.TestButton.State);

        // The count is said in the line and nowhere else in the window.
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == editor.TestResult);

        // Another value: the result was about the filter of before and goes with it.
        editor.Groups[0].Conditions[0].Value = "IT";
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result went with its filter");
        Assert.Equal(string.Empty, line.Message);
        window.UpdateLayout();
        Assert.Equal(nameTop, TopOf(window.NameBox, window), 0.5);

        AssertNoOldFeedback(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ATestThatFails_IsAnErrorWithRetry_AndWithoutASessionTheLineSaysWhyNothingWasTested() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, editor) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        Accounting(editor);
        await UiTestSupport.WaitForIdleAsync();

        // The domain controller does not answer the search of the test.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(window.TestButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Error && !editor.TestCommand.IsRunning, "the test failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // What was tried, the reason, what to do, the code - with Retry; the editor is as it was, so it can be closed.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(editor.TestResult);
        Assert.Equal($"Could not test the filter in {domain}: {editor.TestResult} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(editor.TestCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.TestButton.State);
        Assert.False(window.ErrorNote.IsVisible);
        AssertNoOldFeedback(window);

        // Retry, pressed in the line, counts.
        Volatile.Write(ref fail, 0);
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Done && !editor.TestCommand.IsRunning, "the matching objects were counted");
        session.BeforeOperation = null;
        Assert.StartsWith($"Matching objects in {domain}: 3 (", line.Message, StringComparison.Ordinal);

        // Without a session the filter can still be edited and saved; a test says why it was not started - an attention
        // the operator can close, not a state of the window.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Done, line.Kind);
        ControlHost.Press(window.TestButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention && !editor.TestCommand.IsRunning, "why nothing was tested");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("The filter was not tested: " + context.Localizer()[L.Errors.NotConnected], line.Message);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.TestButton.State);
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the attention was closed");
        Assert.Equal(string.Empty, line.Message);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The builder of a new filter, shown off-screen (connected to the sample company).</summary>
    private static async Task<(FilterEditorWindow Window, FilterEditorViewModel Editor)> OpenAsync(UiTestContext context)
    {
        await context.ConnectAsync();
        var services = context.Services.GetRequiredService<DialogServices>();
        var editor = new FilterEditorViewModel(
            services.Sessions, services.Dialogs, services.Localizer, services.Errors, services.Time, services.Dispatcher);
        var window = (FilterEditorWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(editor);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();
        return (window, editor);
    }

    /// <summary>department = Księgowość: three users of the sample company.</summary>
    private static void Accounting(FilterEditorViewModel editor)
    {
        var condition = editor.Groups[0].Conditions[0];
        condition.Attribute = Attr.Department;
        condition.Value = "Księgowość";
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(FilterEditorWindow window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
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
