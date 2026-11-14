using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of "Reset Password" (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"), through
/// the real window over the fake directory: one line at the top - empty at rest, why nothing was reset, the check of
/// the accounts with Stop and OK as its Cancel, how the reset ended and, behind that, the counts of the result list,
/// the end of the session. A slow domain controller is a read the test holds and releases, a refusing one an injected
/// failure; every state that is asserted lasts until the test changes it. No password is ever in the line. How
/// passwords are reset is covered by <see cref="ObjectDialogTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ResetPasswordStatusUiTests
{
    private const string Typed = "Nowe!Haslo2026";

    /// <summary>How long a held read waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_AndWhatIsMissingIsSaidThere() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, dialog) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // One status line, named PageStatus, over the status of the window, at the top - above the accounts and the
        // password boxes - of the fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(dialog);
        Assert.Same(dialog.Status.Current, line.Status);
        Assert.Same(dialog.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.PasswordBox, window), "the status line is not above the password boxes");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // The accounts were read and nothing was asked for: the line is there, and empty.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);

        // OK is an action button at rest - over the command of before and the Cancel of the window's operation - and
        // still the default button; the dialog's own Cancel is still its cancel button.
        Assert.Equal(ActionButtonState.Idle, window.ResetButton.State);
        Assert.Same(dialog.ResetCommand, window.ResetButton.Command);
        Assert.Same(dialog.Status.CancelCommand, window.ResetButton.CancelCommand);
        Assert.True(window.ResetButton.IsDefault);
        Assert.True(window.ResetButton.IsEnabled);
        Assert.Same(Application.Current.FindResource("Ad.AccentDialogActionButton"), window.ResetButton.Style);
        Assert.True(window.CloseButton.IsCancel);
        Assert.True(window.CloseButton.IsEnabled);

        // Two passwords that do not match: nothing is reset, and the line says why - an attention that can be closed.
        // The boxes moved down by as much as the line grew.
        var boxesTop = TopOf(window.PasswordBox, window);
        window.PasswordBox.Password = Typed;
        window.ConfirmPasswordBox.Password = "Inne!Haslo2026";
        ControlHost.Press(window.ResetButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention, "what is wrong in the status line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Attention", line.Title);
        Assert.Equal("No password was reset: The passwords do not match.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Empty(context.Directory.Session.PasswordResets);
        Assert.Equal(ActionButtonState.Idle, window.ResetButton.State);
        Assert.True(window.ResetButton.IsEnabled);
        window.UpdateLayout();
        Assert.Equal(boxesTop + GrowthOf(line), TopOf(window.PasswordBox, window), 0.5);

        // Closed: the line is empty and one line high again.
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal(string.Empty, line.Message);
        window.UpdateLayout();
        Assert.Equal(boxesTop, TopOf(window.PasswordBox, window), 0.5);

        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheReset_SaysCheckingWithStop_OkIsItsCancel_AndAfterItTheLineSaysHowItEndedAndTheCounts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, dialog) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        var boxesTop = TopOf(window.PasswordBox, window);
        window.PasswordBox.Password = Typed;
        window.ConfirmPasswordBox.Password = Typed;

        // The first read of the planner waits for the test, so "checking" lasts - and "stopping" after Stop.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        session.BeforeOperation = _ =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.ResetButton);
            await UiWait.UntilAsync(() => held.IsSet && dialog.IsBusy && line.Kind == PageStatusKind.Working, "the accounts are being checked");

            // Working: what is checked - with Stop, and nothing to close. OK is the Cancel of the reset: enabled; the
            // dialog cannot be closed meanwhile.
            Assert.Equal("Working", line.Title);
            Assert.Equal("Checking the accounts (1)…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Running, window.ResetButton.State);
            Assert.True(window.ResetButton.IsEnabled);
            Assert.False(window.CloseButton.IsEnabled);
            AssertNoOldFeedback(window);

            // The line was there before: the boxes did not move.
            window.UpdateLayout();
            Assert.Equal(boxesTop, TopOf(window.PasswordBox, window));

            // Cancel where you started: "Stopping…" on the button and in the line at once.
            ControlHost.Press(window.ResetButton);
            await UiWait.UntilAsync(
                () => window.ResetButton.State == ActionButtonState.Cancelling && dialog.Status.IsCancelling, "the check is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(window.ResetButton.IsEnabled);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => !dialog.IsBusy && line.Kind == PageStatusKind.Attention, "the check was stopped");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: no password was reset - whatever the planner did after the Stop - and Retry resets again.
        Assert.Equal("Checking the accounts was stopped: no password was reset. Press Retry to try again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(dialog.ResetCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Empty(session.PasswordResets);
        Assert.False(dialog.IsCompleted);
        Assert.Equal(ActionButtonState.Idle, window.ResetButton.State);
        Assert.True(window.ResetButton.IsEnabled);
        Assert.True(window.CloseButton.IsEnabled);
        Assert.Equal(Typed, window.PasswordBox.Password);

        // Retry, pressed in the line, resets the password: done, with the domain - and never with the password.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !dialog.IsBusy && dialog.IsCompleted && line.Kind == PageStatusKind.Done, "the password was reset");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Done", line.Title);
        Assert.Equal($"“Reset Password” was executed in {domain}: 1 object changed. The report is in the list of background operations.", line.Message);
        Assert.DoesNotContain(Typed, line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusOk, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(Typed, Assert.Single(session.PasswordResets).Password);
        Assert.Equal(string.Empty, window.PasswordBox.Password);
        Assert.False(window.ResetButton.IsVisible);
        Assert.True(window.ResultsList.IsVisible);
        Assert.Single(window.ResultsList.Items);
        Assert.True(window.CloseButton.IsEnabled);

        // Behind the result: the counts of the result list - in the line, and not a second time above the list.
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal("Succeeded: 1, failed: 0, skipped: 0", line.Message);
        Assert.Equal(dialog.ResultSummary, line.Message);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == dialog.ResultSummary);
        AssertNoOldFeedback(window);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AResetThatTheDomainControllerRefused_IsAnError_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, dialog) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        window.PasswordBox.Password = Typed;
        window.ConfirmPasswordBox.Password = Typed;
        session.FailNext(FakeOperationKind.ResetPassword, context.Company.ZanetaSzymanska, DirectoryErrorKind.AccessDenied);

        ControlHost.Press(window.ResetButton);
        await UiWait.UntilAsync(() => !dialog.IsBusy && line.Kind == PageStatusKind.Error, "the reset was refused");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Nothing was changed: what was tried, how many failed, where the reasons are - which is the row of the list.
        Assert.Equal("Error", line.Title);
        Assert.Equal(
            $"Could not execute “Reset Password” in {domain}: nothing was changed, failed: 1. The reasons are in the list of results.",
            line.Message);
        Assert.DoesNotContain(Typed, line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.True(dialog.IsCompleted);
        var row = Assert.Single(dialog.Results);
        Assert.False(row.Succeeded);
        Assert.NotEmpty(row.Message);
        Assert.True(window.ResultsList.IsVisible);
        Assert.Empty(session.PasswordResets);
        AssertNoOldFeedback(window);

        // The session of the window ends: an error that cannot be closed takes the line; the dialog can still be closed.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => dialog.IsSessionEnded && line.Message == context.Localizer()[L.WindowSession.Ended], "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.True(window.CloseButton.IsEnabled);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The dialog for one account (Żaneta Szymańska), shown off-screen, its account read.</summary>
    private static async Task<(ResetPasswordWindow Window, ResetPasswordViewModel Dialog)> OpenAsync(UiTestContext context)
    {
        await context.ConnectAsync();
        var dialog = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateResetPassword([context.Company.ZanetaSzymanska]);
        var window = (ResetPasswordWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(dialog.IsSingle);
        Assert.Null(dialog.Error);
        return (window, dialog);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(ResetPasswordWindow window)
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
