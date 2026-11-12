using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Presentation.Views;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the editor of a saved view (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"),
/// through the real window over the fake directory: one line at the top - empty at rest, the test of the view with Stop
/// and the Test button as its Cancel, how many objects match in the search base, a test that was stopped or failed, why
/// it was not started - and a filter that cannot be read as a note under the filter. A slow domain controller is a
/// search the test holds and releases, a failing one an exception of the fake session; every state that is asserted
/// lasts until the test changes it. How a view is edited and saved is covered by <see cref="DialogWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SavedViewEditorStatusUiTests
{
    private const string Base = "OU=Księgowość,OU=Pracownicy,OU=Firma,{domain}";
    private const string BasePath = "contoso.test/Firma/Pracownicy/Księgowość";
    private const string Users = "(objectClass=user)";

    /// <summary>How long a held search waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_AndAFilterThatCannotBeReadIsANote() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, editor) = await OpenAsync(context);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, at the top - above the name - of the fixed
        // height, and empty: nothing was tested.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(editor);
        Assert.Same(editor.Status.Current, line.Status);
        Assert.Same(editor.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.NameBox, window), "the status line is not above the name");
        Assert.True(TopOf(window.NameBox, window) < TopOf(window.FilterBox, window), "the name is not above the filter");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);

        // Test is an action button - over the command of before and the Cancel of the window's operation; Save is still
        // the default button. No note: the filter can be read.
        Assert.Equal(ActionButtonState.Idle, window.TestButton.State);
        Assert.Same(editor.TestCommand, window.TestButton.Command);
        Assert.Same(editor.Status.CancelCommand, window.TestButton.CancelCommand);
        Assert.True(window.TestButton.IsEnabled);
        Assert.False(window.TestButton.IsDefault);
        Assert.True(window.SaveButton.IsDefault);
        Assert.False(window.FilterErrorNote.IsVisible);

        // A filter that cannot be read: a note under the filter - not a banner, and not the line - and Test cannot be
        // pressed. The dialog sizes to its content: the Test button moved down by the note.
        var testTop = TopOf(window.TestButton, window);
        editor.Filter = "(&(objectClass=user)";
        await UiTestSupport.WaitForIdleAsync();
        Assert.NotNull(editor.FilterError);
        Assert.True(window.FilterErrorNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Error"), window.FilterErrorNote.Style);
        Assert.Equal(editor.FilterError, window.FilterErrorText.Text);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(window.FilterErrorText));
        Assert.True(TopOf(window.FilterBox, window) < TopOf(window.FilterErrorNote, window), "the note is not under the filter");
        Assert.True(TopOf(window.FilterErrorNote, window) < TopOf(window.TestButton, window), "the Test button is not under the note");
        Assert.True(TopOf(window.TestButton, window) > testTop, "the note took no room");
        Assert.False(window.TestButton.IsEnabled);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);

        editor.Filter = Users;
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.FilterErrorNote.IsVisible);
        Assert.True(window.TestButton.IsEnabled);
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
        var nameTop = TopOf(window.NameBox, window);

        // The search of the test waits for the test, so "counting" lasts - and "stopping" after Stop.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        session.BeforeOperation = operation =>
        {
            if (operation is { Kind: FakeOperationKind.Search, Search.Filter: Users } && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.TestButton);
            await UiWait.UntilAsync(() => held.IsSet && line.Kind == PageStatusKind.Working, "the matching objects are being counted");

            // Working: what is counted and where - the search base as a path - with Stop, and nothing to close. The
            // Test button is the Cancel of the test: enabled, and there is no separate Stop button.
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Counting the matching objects in “{BasePath}”…", line.Message);
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

        // Stopped: what was not done, and Test as the way to count again. The dialog sizes to its content: the fields
        // moved down by as much as the line grew.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Counting the matching objects was stopped: the view was not tested. Press “Test” to test again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Test", Assert.IsType<string>(action.Content));
        Assert.Same(editor.TestCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.TestButton.State);
        window.UpdateLayout();
        Assert.Equal(nameTop + GrowthOf(line), TopOf(window.NameBox, window), 0.5);

        // Test, pressed in the line, counts: done - how many, where, how long it took.
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Done && !editor.TestCommand.IsRunning, "the matching objects were counted");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Done", line.Title);
        Assert.StartsWith($"Matching objects in “{BasePath}”: 3 (", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(" s).", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusOk, line.IconKey);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);

        // The count is said in the line and nowhere else in the window.
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == editor.TestResult);

        // Another scope: the result was about the view of before and goes with it.
        editor.Scope = SearchScope.OneLevel;
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result went with its view");
        Assert.Equal(string.Empty, line.Message);

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

        // The domain controller refuses the search of the test.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation is { Kind: FakeOperationKind.Search, Search.Filter: Users })
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "access to the container was denied");
            }
        };
        ControlHost.Press(window.TestButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Error && !editor.TestCommand.IsRunning, "the test failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // What was tried, the reason, what to do, the code - with Retry; the editor is as it was, so it can be closed.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(editor.TestResult);
        Assert.Equal($"Could not test the view in “{BasePath}”: {editor.TestResult} Press Retry to try again. (AccessDenied)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(editor.TestCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.TestButton.State);
        Assert.False(window.FilterErrorNote.IsVisible);
        AssertNoOldFeedback(window);

        // Retry, pressed in the line, counts.
        Volatile.Write(ref fail, 0);
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Done && !editor.TestCommand.IsRunning, "the matching objects were counted");
        session.BeforeOperation = null;
        Assert.StartsWith($"Matching objects in “{BasePath}”: 3 (", line.Message, StringComparison.Ordinal);

        // Without a session the view can still be edited and saved; a test says why it was not started - an attention
        // the operator can close, not a state of the window.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Done, line.Kind);
        ControlHost.Press(window.TestButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention && !editor.TestCommand.IsRunning, "why nothing was tested");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("The view was not tested: " + context.Localizer()[L.Errors.NotConnected], line.Message);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the attention was closed");
        Assert.Equal(string.Empty, line.Message);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The editor of a new view over the users of OU=Księgowość (three of the sample company), shown off-screen. The view
    /// model comes from the container, as the application's own editor of a new view does.
    /// </summary>
    private static async Task<(SavedViewEditorWindow Window, SavedViewEditorViewModel Editor)> OpenAsync(UiTestContext context)
    {
        await context.ConnectAsync();
        var editor = context.Services.GetRequiredService<SavedViewEditorViewModel>();
        editor.Name = "Księgowość";
        editor.BaseDn = Base;
        editor.Filter = Users;
        var window = (SavedViewEditorWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(editor);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(BasePath, editor.BaseDnDisplay);
        return (window, editor);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(SavedViewEditorWindow window)
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
