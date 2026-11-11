using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the AD Recycle Bin (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-1 as built"),
/// through the real window over the fake directory: one line at the top - the summary of the list, the read with the
/// number of objects so far and Stop, a read that was stopped or failed with its action, the end of the session - and
/// Refresh as an action button that is the Cancel of the read. A slow domain controller is a read the test holds and
/// releases, a failing one an exception of the fake session; every state that is asserted lasts until the test changes
/// it. How objects are listed and restored is covered by <see cref="RecycleBinWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class RecycleBinStatusUiTests
{
    /// <summary>How long a held read waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_WithTheSummaryOfTheList() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, bin) = await OpenAsync(context);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, above the note, the filters and the list,
        // of the fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(bin);
        Assert.Same(bin.Status.Current, line.Status);
        Assert.Same(bin.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.Banner, window), "the status line is not above the note");
        Assert.True(TopOf(window.Banner, window) < TopOf(window.NameFilterBox, window), "the note is not above the filters");
        Assert.True(TopOf(window.NameFilterBox, window) < TopOf(window.ItemsList, window), "the filters are not above the list");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: what the list says - in the line, and not a second time in the footer.
        Assert.Equal(8, bin.Items.Count);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith("8 objects · ", line.Message, StringComparison.Ordinal);
        Assert.Equal(bin.StatusText, line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == bin.StatusText);

        // A selection joins the summary.
        window.ItemsList.SelectedItem = bin.Items[0];
        await UiWait.UntilAsync(() => line.Message.EndsWith(" · 1 selected", StringComparison.Ordinal), "the selection in the status line");
        Assert.StartsWith(bin.StatusText, line.Message, StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Idle, line.Kind);

        // What can be restored in this forest is a note - information with the Recycle Bin - not a banner.
        Assert.True(window.EnabledNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Info"), window.EnabledNote.Style);
        Assert.True(window.BannerText.IsVisible);
        Assert.Equal(bin.BannerText, window.BannerText.Text);
        Assert.False(window.TombstoneNote.IsVisible);

        // Refresh is an action button at rest - over the command of before and the Cancel of the list's read - and so
        // is the button that restores (not shown before the preview).
        Assert.Equal(ActionButtonState.Idle, window.RefreshButton.State);
        Assert.Same(bin.RefreshCommand, window.RefreshButton.Command);
        Assert.Same(bin.ListStatus.CancelCommand, window.RefreshButton.CancelCommand);
        Assert.True(window.RefreshButton.IsEnabled);
        Assert.Same(bin.ConfirmRestoreCommand, window.ConfirmButton.Command);
        Assert.Same(bin.Status.CancelCommand, window.ConfirmButton.CancelCommand);
        Assert.False(window.ConfirmButton.IsVisible);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task ReadingTheList_SaysHowMuchSoFar_RefreshIsItsCancel_AndAStoppedReadIsAttentionWithRefresh() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, bin) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        var listTop = TopOf(window.ItemsList, window);

        // The deleted containers of the listed users are read one by one after the list: the first of those reads
        // waits for the test, so "reading, 8 objects so far" lasts - and after Stop the next one is not made.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        session.BeforeOperation = operation =>
        {
            if (operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.Base, Search.IncludeDeleted: true }
                && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.RefreshButton);
            await UiWait.UntilAsync(() => held.IsSet && bin.Items.Count == 8 && line.Message.Contains("so far", StringComparison.Ordinal), "the list is being read");

            // Working: what is read, where, and how much so far - with Stop, and nothing to close.
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Reading the deleted objects of {domain}: 8 objects so far…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);

            // The Refresh button is the Cancel of the read: enabled, and there is no separate Stop button or bar.
            Assert.Equal(ActionButtonState.Running, window.RefreshButton.State);
            Assert.True(window.RefreshButton.IsEnabled);
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && other is not ActionButton && !other.IsDescendantOf(line)
                    && (AutomationProperties.GetName(other) == "Stop" || other.Content as string == "Stop"));
            AssertNoOldFeedback(window);

            // The line was there before: the list did not move.
            window.UpdateLayout();
            Assert.Equal(listTop, TopOf(window.ItemsList, window));

            // Cancel where you started: "Stopping…" on the button and in the line at once - the read that is on its
            // way to the domain controller still has to return.
            ControlHost.Press(window.RefreshButton);
            await UiWait.UntilAsync(() => window.RefreshButton.State == ActionButtonState.Cancelling && bin.ListStatus.IsCancelling, "the read is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(window.RefreshButton.IsEnabled);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => !bin.IsLoading && line.Kind == PageStatusKind.Attention, "the read was stopped");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what is there, and Refresh as the way to the whole list.
        Assert.Equal("Attention", line.Title);
        Assert.Equal(
            "Reading the deleted objects was stopped before all results arrived: 8 objects loaded. Press Refresh to read the whole list.",
            line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(bin.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.RefreshButton.State);
        Assert.Equal(8, window.ItemsList.Items.Count);
        window.UpdateLayout();
        Assert.Equal(listTop + GrowthOf(line), TopOf(window.ItemsList, window), 0.5);

        // Refresh, pressed in the line, reads the whole list: the summary again.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !bin.IsLoading && line.Kind == PageStatusKind.Idle, "the list was read again");
        Assert.Equal(bin.StatusText, line.Message);
        Assert.Equal(8, bin.Items.Count);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AReadThatFails_IsAnErrorWithRetry_UntilItIsReadAgain_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, bin) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;

        // The domain controller does not answer the list any more.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel })
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(window.RefreshButton);
        await UiWait.UntilAsync(() => !bin.IsLoading && line.Kind == PageStatusKind.Error, "the read failed");

        // The list is empty and the line says why: what was tried, the reason, what to do, the code - with Retry, and
        // it cannot be closed. The hint of an empty list is not shown: the list is not known to be empty.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(bin.Error);
        Assert.Equal($"Could not read the deleted objects of {domain}: {bin.Error} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(bin.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Empty(bin.Items);
        Assert.False(window.EmptyHint.IsVisible);
        Assert.Equal(ActionButtonState.Idle, window.RefreshButton.State);
        AssertNoOldFeedback(window);

        // Retry, pressed in the line, reads the list.
        Volatile.Write(ref fail, 0);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !bin.IsLoading && line.Kind == PageStatusKind.Idle, "the list was read");
        session.BeforeOperation = null;
        Assert.Equal(8, bin.Items.Count);
        Assert.Equal(bin.StatusText, line.Message);

        // The session of the window ends: an error that cannot be closed takes the line, the note about the forest
        // goes with the forest, and nothing can be read or restored any more.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => bin.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.Banner.IsVisible);
        Assert.False(window.RefreshButton.IsEnabled);
        Assert.False(window.RestoreButton.IsEnabled);
        Assert.Equal(ActionButtonState.Idle, window.RefreshButton.State);
        Assert.True(window.CloseButton.IsEnabled);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The Recycle Bin window over a deleted branch of the sample company (OU=Pracownicy with everything in it), showing
    /// its eight users: their containers are deleted as well and not listed, so the window reads them after the list.
    /// </summary>
    private static async Task<(RecycleBinWindow Window, RecycleBinViewModel Bin)> OpenAsync(UiTestContext context)
    {
        await context.ConnectAsync();
        var session = context.Directory.Session;
        await session.DeleteAsync(context.Company.EmployeesOu, subtree: true, CancellationToken.None);

        await context.Services.GetRequiredService<IDialogService>().ShowRecycleBinAsync();
        await UiTestSupport.WaitForIdleAsync();
        var window = Assert.Single(Application.Current.Windows.OfType<RecycleBinWindow>());
        await UiWait.InitializedAsync(window.Initialization);
        var bin = window.ViewModel;
        bin.Kind = bin.Kinds.Single(k => k.Value == ObjectKind.User);
        await bin.Loading;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Null(bin.Error);
        return (window, bin);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(RecycleBinWindow window)
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
