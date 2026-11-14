using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The feedback of the Logon Hours dialog (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"),
/// through the real window: the dialog starts no operation, so it has no status line - what it says is true of the
/// grid for as long as it is shown and stands next to it as a note: why the hours cannot be changed, a stored value the
/// grid cannot show, a schedule that permits no hour. No banner of the styles of before is left. How the grid is edited
/// is covered by <see cref="LogonHoursWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class LogonHoursNotesUiTests
{
    private static readonly DayOfWeek[] Workdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    [Fact]
    public Task TheDialog_HasNoStatusLine_AndAScheduleWithoutAnyHour_IsANoteUnderTheGrid() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var localizer = context.Services.GetRequiredService<ILocalizer>();
        var office = LogonHours.FromLocalGrid(LogonHoursGrid.Create((day, hour) => Workdays.Contains(day) && hour is >= 8 and < 16), 0);
        var dialog = new LogonHoursViewModel(localizer, office, TimeZoneInfo.Utc, DateTimeOffset.UtcNow, "Jan Kowalski");
        var window = (LogonHoursWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Pure editing: no status of its own, so no status line - and nothing to say while the schedule is in order.
        Assert.IsNotAssignableFrom<IHasPageStatus>(dialog);
        Assert.Empty(UiWait.Descendants<StatusLine>(window));
        Assert.False(window.ReadOnlyNote.IsVisible);
        Assert.False(window.InvalidValueNote.IsVisible);
        Assert.False(window.NoneWarningNote.IsVisible);
        AssertNoOldFeedback(window);

        // Every hour denied: the account could not log on at all - an attention note under the grid, while it is so.
        window.SelectAllButton.Command.Execute(null);
        window.DeniedRadio.IsChecked = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(dialog.IsNonePermitted);
        Assert.True(window.NoneWarningNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.NoneWarningNote.Style);
        Assert.True(window.NoneWarningText.IsVisible);
        Assert.True(window.NoneWarningText.IsDescendantOf(window.NoneWarningNote));
        Assert.Equal(localizer[L.LogonHours.NoneWarning], window.NoneWarningText.Text);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(window.NoneWarningText));
        Assert.True(TopOf(window.HourGrid, window) < TopOf(window.NoneWarningNote, window), "the note is not under the grid");
        AssertNoOldFeedback(window);

        // An hour permitted again: the note goes with what it was about.
        window.PermittedRadio.IsChecked = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.False(dialog.IsNonePermitted);
        Assert.False(window.NoneWarningNote.IsVisible);
        context.AssertHealthy();
    });

    [Fact]
    public Task WhyTheHoursCannotBeChanged_AndAValueTheGridCannotShow_AreNotesAboveTheGrid() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var localizer = context.Services.GetRequiredService<ILocalizer>();
        var dialog = new LogonHoursViewModel(localizer, null, TimeZoneInfo.Utc, DateTimeOffset.UtcNow, null, localizer[L.Fields.ReadOnlyPermission]);
        var window = (LogonHoursWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Both are true for as long as the dialog is open: notes with the note style, above the grid they are about.
        Assert.Empty(UiWait.Descendants<StatusLine>(window));
        Assert.True(window.ReadOnlyNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.ReadOnlyNote.Style);
        Assert.True(window.ReadOnlyReasonText.IsVisible);
        Assert.Equal(localizer[L.Fields.ReadOnlyPermission], window.ReadOnlyReasonText.Text);
        Assert.True(window.InvalidValueNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.InvalidValueNote.Style);
        Assert.True(window.InvalidValueText.IsVisible);
        Assert.Equal(localizer[L.LogonHours.InvalidStored], window.InvalidValueText.Text);
        Assert.True(TopOf(window.ReadOnlyNote, window) < TopOf(window.InvalidValueNote, window), "the two notes are not one under the other");
        Assert.True(TopOf(window.InvalidValueNote, window) < TopOf(window.HourGrid, window), "the notes are not above the grid");
        Assert.False(window.NoneWarningNote.IsVisible);

        // Nothing is a tab stop or a button of the notes: OK and Cancel are where they were.
        Assert.False(window.ReadOnlyNote.IsTabStop);
        Assert.False(window.InvalidValueNote.IsTabStop);
        Assert.True(window.OkButton.IsDefault);
        Assert.True(window.CancelButton.IsCancel);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    /// <summary>No banner of the styles of before, no progress bar - and no status line: the dialog has no status.</summary>
    private static void AssertNoOldFeedback(LogonHoursWindow window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Empty(UiWait.Descendants<StatusLine>(window));
    }

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;
}
