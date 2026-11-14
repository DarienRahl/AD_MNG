using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of the comparison of two GPOs (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as
/// built"), through the real window over the fake directory: one line under the title - the comparison with Stop, the
/// count of differences as the summary, a comparison that was stopped or failed with its action, the templates that name
/// the policies as a stage -, and what SYSVOL could not give as a note above the rows. A slow SYSVOL is a call the test
/// holds and releases (a held call on the UI thread would hold the test), a failing domain controller an exception of the
/// fake session; every state that is asserted lasts until the test changes it. The comparison itself is covered by
/// <see cref="GroupPolicyWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GpoComparisonStatusUiTests
{
    [Fact]
    public Task TheWindow_HasOneStatusLine_UnderTheTitle_AndTheDifferencesAreItsSummary() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var (window, comparison) = await OpenAsync(context, sample.WorkstationPasswords, sample.WorkstationRemoteDesktop);
        var line = window.PageStatus;
        await UiWait.UntilAsync(() => !comparison.Status.IsWorking && comparison.Rows.Count > 0, "the comparison");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // One status line, named PageStatus, over the status of the window, under the title and its options and above the
        // rows, of the fixed height - and it says how many differences the rows hold, where the footer said it before.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(comparison);
        Assert.Same(comparison.Status.Current, line.Status);
        Assert.Same(comparison.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(window.OnlyDifferencesBox, window) < TopOf(line, window), "the status line is not under the header");
        Assert.True(TopOf(line, window) < TopOf(window.ComparisonList, window), "the status line is not above the rows");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Matches(@"^\d+ differences in \d+ items$", line.Message);
        Assert.Equal(comparison.SummaryText, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);

        // The footer holds the buttons and nothing else; the two header controls are where they were.
        Assert.True(window.CloseButton.IsCancel);
        Assert.True(window.ExportButton.IsEnabled);
        Assert.True(window.RefreshButton.IsEnabled);

        // "Only differences" off: more rows, the same summary, and nothing moved.
        var listTop = TopOf(window.ComparisonList, window);
        var rows = window.ComparisonList.Items.Count;
        comparison.OnlyDifferences = false;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(window.ComparisonList.Items.Count >= rows);
        Assert.Equal(comparison.SummaryText, line.Message);
        window.UpdateLayout();
        Assert.Equal(listTop, TopOf(window.ComparisonList, window));
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheComparison_SaysComparingWithStop_AndAStoppedOneIsAttentionWithRefresh() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();

        // The first read of a GPO folder waits for the test, so "comparing" lasts - and "stopping" after Stop.
        var hold = context.Sysvol.HoldNext("Read");
        var (window, comparison) = await OpenAsync(context, sample.WorkstationPasswords, sample.WorkstationRemoteDesktop);
        var line = window.PageStatus;
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Working, "the window is comparing");
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var listTop = TopOf(window.ComparisonList, window);

        // Working: what is compared, with Stop and nothing to close; no bar in the middle of the empty list.
        Assert.Equal("Working", line.Title);
        Assert.Equal("Comparing “Stacje robocze – Zasady haseł” with “Stacje robocze – Pulpit zdalny”…", line.Message);
        Assert.True(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Stop", Assert.IsType<string>(action.Content));
        Assert.Same(comparison.Status.CancelCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.True(window.CloseButton.IsEnabled);
        AssertNoOldFeedback(window);
        window.UpdateLayout();
        Assert.Equal(listTop, TopOf(window.ComparisonList, window));

        // Stop, pressed in the line: "Stopping…" at once - the read that is on its way still has to return.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => comparison.Status.IsCancelling && line.Message == "Stopping…", "the comparison is stopping");
        Assert.Equal(PageStatusKind.Working, line.Kind);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.True(comparison.IsLoading);
        hold.Release();
        await UiWait.UntilAsync(() => !comparison.IsLoading && line.Kind == PageStatusKind.Attention, "the comparison was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped before anything was shown: the state of the window - it cannot be closed - with Refresh. What lies
        // under the line moved down by as much as the line grew.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Comparing was stopped: nothing is shown. Press “Refresh” to compare again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(comparison.LoadCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Empty(comparison.Rows);
        window.UpdateLayout();
        Assert.Equal(listTop + GrowthOf(line), TopOf(window.ComparisonList, window), 0.5);

        // Refresh, pressed in the line, compares: the rows and the count of differences.
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !comparison.Status.IsWorking && comparison.Rows.Count > 0 && line.Kind == PageStatusKind.Idle, "the rows were compared");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(comparison.SummaryText, line.Message);
        window.UpdateLayout();
        Assert.Equal(listTop, TopOf(window.ComparisonList, window), 0.5);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheTemplatesThatNameThePolicies_AreAStageOfTheComparison_AndStoppingThemIsAResultThatCanBeClosed() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        var cleanup = context.Directory.AddGroupPolicyCleanupSample(context.Company, context.Sysvol);
        foreach (var (path, content) in SampleAdministrativeTemplates.Files(polish: false))
        {
            context.Sysvol.PutPolicyDefinition(path, content);
        }

        await context.ConnectAsync();

        // The administrative templates are listed by a call the test holds: the rows are shown, the line says the stage.
        var hold = context.Sysvol.HoldNext("ListDefinitions");
        var (window, comparison) = await OpenAsync(context, cleanup.EmptyGpo, cleanup.Templates);
        var line = window.PageStatus;
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        await UiWait.UntilAsync(() => comparison.Rows.Count > 0 && line.Message.StartsWith("Reading the administrative templates", StringComparison.Ordinal), "the stage of the templates");
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        Assert.Equal(PageStatusKind.Working, line.Kind);
        Assert.Equal("Reading the administrative templates to name the policies…", line.Message);
        Assert.Equal("Stop", Assert.IsType<string>(action.Content));
        Assert.False(comparison.IsLoading);
        Assert.NotEmpty(window.ComparisonList.Items);

        // Stop: the rows stay, under the registry names of the settings; the line says so - a result that can be closed.
        var summary = comparison.SummaryText;
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => comparison.Status.IsCancelling && line.Message == "Stopping…", "the templates are being stopped");
        hold.Release();
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention, "the templates were stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(
            "Reading the administrative templates was stopped: the policies are shown under their registry names. Press “Refresh” to compare again.",
            line.Message);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.NotEmpty(window.ComparisonList.Items);

        // Closed, the line says the count of differences again.
        await UiWait.UntilAsync(() => close.IsEnabled, "the close button of the line can be pressed");
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal(summary, line.Message);
        context.AssertHealthy();
    });

    [Fact]
    public Task AFailedComparison_IsAnErrorWithRetry_WhatSysvolCouldNotGiveIsANote_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var directory = context.Directory.Session;

        // The domain controller refuses the GPO containers (from the moment the window opens).
        var fail = 0;
        directory.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation.DistinguishedName.Contains("CN=Policies", StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "access to the GPO container was denied");
            }
        };
        var (window, comparison) = await OpenAsync(context, sample.WorkstationPasswords, sample.WorkstationRemoteDesktop, () => Volatile.Write(ref fail, 1));
        var line = window.PageStatus;
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Error, "the comparison failed");
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // The error says what was tried, the reason, what to do and the code - with Retry - and cannot be closed.
        Assert.Equal("Error", line.Title);
        Assert.Equal(
            $"Could not compare “Stacje robocze – Zasady haseł” with “Stacje robocze – Pulpit zdalny”: {comparison.Error} Press Retry to try again. (AccessDenied)",
            line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Empty(window.ComparisonList.Items);
        Assert.False(window.WarningNote.IsVisible);
        AssertNoOldFeedback(window);

        // SYSVOL cannot be read: the settings and files are not compared - a note above the rows, the line keeps the count.
        Volatile.Write(ref fail, 0);
        context.Sysvol.Unavailable = SysvolUnavailableReason.AccessDenied;
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !comparison.Status.IsWorking && comparison.Rows.Count > 0 && line.Kind == PageStatusKind.Idle, "the containers were compared");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        directory.BeforeOperation = null;
        Assert.Equal(comparison.SummaryText, line.Message);
        Assert.True(window.WarningNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.WarningNote.Style);
        Assert.Contains("were not compared", window.WarningText.Text, StringComparison.Ordinal);
        Assert.Equal(comparison.Warning, window.WarningText.Text);
        Assert.True(TopOf(line, window) < TopOf(window.WarningNote, window), "the note is not under the line");
        Assert.True(TopOf(window.WarningNote, window) < TopOf(window.ComparisonList, window), "the note is not above the rows");

        // The session of the window ends: an error that cannot be closed takes the line; the window can still be closed.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => comparison.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.True(window.CloseButton.IsEnabled);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The comparison window of two GPOs, shown off-screen; its comparison starts at once (<paramref name="beforeOpen"/> runs just before).</summary>
    private static async Task<(GpoComparisonWindow Window, GpoComparisonViewModel Comparison)> OpenAsync(
        UiTestContext context, Guid a, Guid b, Action? beforeOpen = null)
    {
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var session = context.Directory.Session;
        var gpoA = (await gp.Reader.ReadPolicyAsync(session, a, CancellationToken.None))!;
        var gpoB = (await gp.Reader.ReadPolicyAsync(session, b, CancellationToken.None))!;
        beforeOpen?.Invoke();
        var comparison = new GpoComparisonViewModel(gp, gpoA, gpoB);
        var window = (GpoComparisonWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(comparison);
        UiTestSupport.ShowOffScreen(window);
        return (window, comparison);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(GpoComparisonWindow window) => GroupPolicyStatusUiSupport.AssertNoOldFeedback(window);
}
