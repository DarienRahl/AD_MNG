using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// Feedback of the main window itself (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-1 as built"). The shell
/// is not a page: it has no status line of its own - a document says its status in its own line. What the shell shows
/// is sorted: "busy" is a progress ring (the quick search, the activities of the connection), how far one activity is
/// stays a bar in the row of that activity, and what is true of the selected object is a note in the details pane.
/// Every state that is asserted lasts until the test changes it.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class MainWindowFeedbackUiTests
{
    /// <summary>Longer than any wait of a test: a search that is not cancelled fails the test instead of ending by itself.</summary>
    private static readonly TimeSpan SlowDomainController = TimeSpan.FromSeconds(30);

    [Fact]
    public Task RunningActivities_AreARingInTheStatusBar_AndTheProgressOfOneActivityStaysInItsRow() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = await OpenAsync(context);
        var ring = window.ActivityRing;

        // The shell has no status line of its own: the one in the window belongs to the object list of the tab.
        var line = Assert.Single(UiWait.Descendants<StatusLine>(window));
        Assert.NotNull(window.SelectedListView);
        Assert.True(line.IsDescendantOf(window.SelectedListView!));

        // Nothing runs: no ring, and no progress bar anywhere in the window.
        Assert.False(window.Shell.Activities.HasRunning);
        Assert.False(ring.IsVisible);
        Assert.False(ring.IsActive);
        Assert.False(ring.IsTurning);
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));

        // An operation runs in this connection: the ring next to the activity button turns ("busy" - how many run is
        // the text next to it, how far each one is is its row in the list of background operations).
        var (activity, execution) = window.Shell.Activities.Start("Zmiana nazw", 2, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        await UiWait.UntilAsync(() => ring.IsVisible && ring.IsTurning, "the ring of the running activity");
        Assert.True(window.Shell.Activities.HasRunning);
        Assert.True(ring.IsActive);
        Assert.True(ring.IsDescendantOf(window.ActivityToggle));
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));

        // The row of an activity keeps its bar: the progress of one row stays in that row.
        var template = Assert.IsType<DataTemplate>(window.FindResource("ActivityTemplate"));
        var row = Assert.IsAssignableFrom<FrameworkElement>(template.LoadContent());
        var bar = Assert.Single(LogicalDescendants<ProgressBar>(row));
        Assert.Equal("Fraction", BindingOperations.GetBinding(bar, RangeBase.ValueProperty)?.Path.Path);
        Assert.False(bar.IsIndeterminate);

        // The operation ended: the ring is gone.
        activity.CancelCommand.Execute(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await activity.Completion;
        await UiWait.UntilAsync(() => !window.Shell.Activities.HasRunning && !ring.IsVisible, "the ring is gone with the activity");
        Assert.False(ring.IsActive);
        Assert.False(ring.IsTurning);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheQuickSearch_ShowsARingInPlaceOfTheMagnifier_WhileItRuns() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = await OpenAsync(context);
        var shell = window.Shell;
        var session = context.Directory.Session;

        // At rest: the magnifier, no ring, and the box has no status.
        Assert.False(shell.IsSearching);
        Assert.True(window.SearchGlyph.IsVisible);
        Assert.False(window.SearchRing.IsActive);
        Assert.False(window.SearchRing.IsTurning);
        Assert.Equal(string.Empty, AutomationProperties.GetItemStatus(window.SearchBox));

        // A domain controller that takes its time: the search runs until the test ends it.
        session.Latency = SlowDomainController;
        try
        {
            shell.SearchText = "Kowal";
            await UiWait.UntilAsync(() => shell.IsSearching && window.SearchRing.IsTurning, "the quick search runs");

            // The ring takes the place of the magnifier; a screen reader finds "Searching…" as the status of the box
            // (the ring is not an element of its own). No progress bar under the box.
            Assert.True(window.SearchRing.IsActive);
            Assert.True(window.SearchRing.IsDescendantOf(window.ToolbarCard));
            Assert.False(window.SearchGlyph.IsVisible);
            Assert.Equal(context.Localizer()[L.Ui.Searching], AutomationProperties.GetItemStatus(window.SearchBox));
            Assert.Empty(UiWait.Descendants<ProgressBar>(window));

            // The operator clears the box: the search is over.
            shell.SearchText = string.Empty;
            await UiWait.UntilAsync(() => !shell.IsSearching, "the quick search ended");
        }
        finally
        {
            session.Latency = TimeSpan.Zero;
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(window.SearchGlyph.IsVisible);
        Assert.False(window.SearchRing.IsActive);
        Assert.False(window.SearchRing.IsTurning);
        Assert.Equal(string.Empty, AutomationProperties.GetItemStatus(window.SearchBox));
        context.AssertHealthy();
    });

    [Fact]
    public Task TheDetailsPane_SaysThatTheSelectedObjectIsProtected_AsANote() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var jan = context.Company.JanKowalski;

        // An account that AdminSDHolder protects (adminCount = 1).
        context.Directory.Session.SetValues(jan, Attr.AdminCount, "1");
        var window = await OpenAsync(context);
        var document = window.Shell.SelectedDocument!;
        var list = window.SelectedListView!.ListControl;
        var protectedRow = Assert.Single(document.Items, r => string.Equals(r.Dn, jan, StringComparison.OrdinalIgnoreCase));
        var otherRow = document.Items.First(r => !ReferenceEquals(r, protectedRow));

        // An object that is not protected: no note.
        list.SelectedItem = otherRow;
        await UiWait.UntilAsync(
            () => window.Shell.SelectedObject is { IsProtected: false } && window.DetailsContent.Visibility == Visibility.Visible,
            "the details of an object that is not protected");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.False(window.ProtectedNote.IsVisible);

        // The protected account: the note about it, in the look of a note - not a banner - for as long as it is shown.
        list.SelectedItem = protectedRow;
        await UiWait.UntilAsync(() => window.Shell.SelectedObject is { IsProtected: true }, "the details of the protected account");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(window.ProtectedNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.ProtectedNote.Style);
        Assert.True(window.ProtectedNoteText.IsVisible);
        Assert.Equal(context.Localizer()[L.Details.ProtectedObject], window.ProtectedNoteText.Text);
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window.DetailsPane),
            border => ReferenceEquals(border.Style, warningBar) || ReferenceEquals(border.Style, errorBar));

        // Another object again: the note goes with the object it was about.
        list.SelectedItem = otherRow;
        await UiWait.UntilAsync(() => window.Shell.SelectedObject is { IsProtected: false }, "the details of the other object again");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.False(window.ProtectedNote.IsVisible);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The main window, connected to the sample company, with the list of the IT department in its tab.</summary>
    private static async Task<MainWindow> OpenAsync(UiTestContext context)
    {
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await window.Shell.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        await UiTestSupport.WaitForIdleAsync();
        Assert.NotNull(window.SelectedListView);
        return window;
    }

    /// <summary>The elements of a template's content (it is not in a visual tree) of type <typeparamref name="T"/>.</summary>
    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in LogicalDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
