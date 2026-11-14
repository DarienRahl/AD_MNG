using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of the GPO picker (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"), through
/// the real window over the fake directory: one line at the top - above the filter -, the read of the GPOs with Stop, the
/// count of the list as the summary, a read that was stopped or failed with Retry, the end of the session. A slow domain
/// controller is a read the test holds and releases, a failing one an exception of the fake session; every state that is
/// asserted lasts until the test changes it. How a GPO is chosen is covered by <see cref="GroupPolicyWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GpoPickerStatusUiTests
{
    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_AndTheCountOfTheListIsItsSummary() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await ConnectAsync(context);
        var (window, picker) = await OpenAsync(context);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the dialog, at the top - above the filter and the list -
        // of the fixed height, and it says how many GPOs the list shows.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(picker);
        Assert.Same(picker.Status.Current, line.Status);
        Assert.Same(picker.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.FilterBox, window), "the status line is not above the filter");
        Assert.True(TopOf(window.FilterBox, window) < TopOf(window.GpoList, window), "the filter is not above the list");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal($"Group policy objects: {picker.Items.Count}", line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);

        // The filter narrows the list, and the line says how many of them are shown - without moving anything.
        var listTop = TopOf(window.GpoList, window);
        picker.FilterText = "PULPIT ZDALNY";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Matches(@"^1 of \d+ group policy objects shown$", line.Message);
        window.UpdateLayout();
        Assert.Equal(listTop, TopOf(window.GpoList, window));

        // OK is still the default button, Cancel the cancel button.
        Assert.True(window.OkButton.IsDefault);
        Assert.True(window.CancelButton.IsCancel);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheRead_SaysReadingWithStop_AndAStoppedReadIsAttentionWithRetry() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var session = await ConnectAsync(context);
        var domain = session.Info.DomainDnsName;

        // The first read of the GPOs waits for the test, so "reading" lasts - and "stopping" after Stop.
        using var held = new HeldRead(context.Directory.Session);
        var (window, picker) = await OpenAsync(context, awaitRead: false);
        var line = window.PageStatus;
        await UiWait.UntilAsync(() => held.IsReached && line.Kind == PageStatusKind.Working, "the dialog is reading");
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var listTop = TopOf(window.GpoList, window);

        // Working: where the dialog reads - with Stop, and nothing to close; no bar in the middle of the empty list.
        Assert.Equal("Working", line.Title);
        Assert.Equal($"Reading the group policy objects of {domain}…", line.Message);
        Assert.True(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Stop", Assert.IsType<string>(action.Content));
        Assert.Same(picker.Status.CancelCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.True(picker.CancelCommand.CanExecute(null));
        AssertNoOldFeedback(window);
        window.UpdateLayout();
        Assert.Equal(listTop, TopOf(window.GpoList, window));

        // Stop, pressed in the line: "Stopping…" at once - the read that is on its way still has to return.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => picker.Status.IsCancelling && line.Message == "Stopping…", "the read is stopping");
        Assert.Equal(PageStatusKind.Working, line.Kind);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.True(picker.IsLoading);
        held.Release();
        await UiWait.UntilAsync(() => !picker.IsLoading && line.Kind == PageStatusKind.Attention, "the read was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: the list is empty, the line says so and offers Retry, and it cannot be closed (it is the only
        // explanation of the empty list). What lies under the line moved down by as much as the line grew.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Reading the group policy objects was stopped: the list is empty. Press “Retry” to read them again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(picker.LoadCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Empty(picker.Items);
        window.UpdateLayout();
        Assert.Equal(listTop + GrowthOf(line), TopOf(window.GpoList, window), 0.5);

        // Retry, pressed in the line, reads the GPOs: the list is there and the line says how many.
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !picker.IsLoading && picker.Items.Count > 0 && line.Kind == PageStatusKind.Idle, "the GPOs were listed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal($"Group policy objects: {picker.Items.Count}", line.Message);
        window.UpdateLayout();
        Assert.Equal(listTop, TopOf(window.GpoList, window), 0.5);
        context.AssertHealthy();
    });

    [Fact]
    public Task AReadThatFailed_IsAnErrorWithRetry_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var session = await ConnectAsync(context);
        var directory = context.Directory.Session;

        // The domain controller refuses the list of the GPOs.
        var fail = 1;
        directory.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation.Kind == FakeOperationKind.Search
                && operation.DistinguishedName.StartsWith("CN=Policies", StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "access to the policies container was denied");
            }
        };
        var (window, picker) = await OpenAsync(context);
        var line = window.PageStatus;
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Error, "the read failed");
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // The error says what was tried, the reason, what to do and the code - with Retry - and cannot be closed.
        Assert.Equal("Error", line.Title);
        Assert.Equal($"Could not read the group policy objects of {session.Info.DomainDnsName}: {picker.Error} Press Retry to try again. (AccessDenied)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.OkButton.IsEnabled);
        Assert.True(window.CancelButton.IsEnabled);
        AssertNoOldFeedback(window);

        // Retry, with a domain controller that answers, reads the list.
        Volatile.Write(ref fail, 0);
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => picker.Items.Count > 0 && line.Kind == PageStatusKind.Idle, "the list was read");
        directory.BeforeOperation = null;
        Assert.Null(picker.Error);

        // The session of the dialog ends: an error that cannot be closed takes the line; the dialog can still be cancelled.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => picker.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.True(window.CancelButton.IsEnabled);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>Connects to the fake directory with the GPO management sample in it.</summary>
    private static async Task<IDirectorySession> ConnectAsync(UiTestContext context)
    {
        context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        return await context.ConnectAsync();
    }

    /// <summary>The picker of the GPOs of the domain, shown off-screen; with <paramref name="awaitRead"/> it is awaited until the GPOs were read.</summary>
    private static async Task<(GpoPickerWindow Window, GpoPickerViewModel Picker)> OpenAsync(UiTestContext context, bool awaitRead = true)
    {
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var picker = new GpoPickerViewModel(gp.Ui, gp.Reader, "Połącz istniejący GPO z Komputery", multiSelect: false, []);
        var window = (GpoPickerWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(picker);
        UiTestSupport.ShowOffScreen(window);
        if (awaitRead)
        {
            await UiWait.InitializedAsync(window.Initialization);
            await UiTestSupport.WaitForIdleAsync();
        }

        return (window, picker);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(GpoPickerWindow window) => GroupPolicyStatusUiSupport.AssertNoOldFeedback(window);
}
