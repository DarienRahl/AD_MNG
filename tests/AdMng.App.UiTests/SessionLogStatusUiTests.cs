using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the session log (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"), through
/// the real window over the fake directory: one line at the top - how many entries the list shows, the text the footer
/// had - and, once the session of the window ended, that it did, for as long as the window is open. Every state that is
/// asserted lasts until the test changes it. How entries are listed, filtered and opened is covered by
/// <see cref="ToolWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SessionLogStatusUiTests
{
    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_WithTheNumberOfEntries() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var (window, log) = await OpenAsync(context);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, above the filter and the list, of the
        // fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(log);
        Assert.Same(log.Status.Current, line.Status);
        Assert.Same(log.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.FilterBox, window), "the status line is not above the filter");
        Assert.True(TopOf(window.FilterBox, window) < TopOf(window.EntriesList, window), "the filter is not above the list");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing was changed in this session yet: the summary says so - in the line, and not a second time in the footer.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("Entries: 0 of 0", line.Message);
        Assert.Equal(log.CountText, line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == log.CountText);

        // A change made while the window is open: the summary follows, and so it does the filter. The list does not move.
        var listTop = TopOf(window.EntriesList, window);
        await ApplyAsync(context, context.Company.MalgorzataWojcik, Attr.Title, "Dyrektor finansowa");
        await UiWait.UntilAsync(() => line.Message == "Entries: 1 of 1", "the new entry in the summary");
        Assert.Single(window.EntriesList.Items);
        log.FilterText = "nie ma takiego wpisu";
        await UiWait.UntilAsync(() => line.Message == "Entries: 0 of 1", "the filter in the summary");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        window.UpdateLayout();
        Assert.Equal(listTop, TopOf(window.EntriesList, window));

        // The footer holds the two buttons of before; Close is still the cancel button.
        Assert.True(window.ExportButton.IsVisible);
        Assert.True(window.CloseButton.IsCancel);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheEndOfTheSession_LastsInTheLine_AndTheEntriesStay() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var (window, log) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        await ApplyAsync(context, context.Company.MalgorzataWojcik, Attr.Title, "Dyrektor finansowa");
        await UiWait.UntilAsync(() => line.Message == "Entries: 1 of 1", "the new entry in the summary");
        var listTop = TopOf(window.EntriesList, window);
        log.SelectedEntry = log.Entries[0];
        Assert.True(log.OpenCommand.CanExecute(null));

        // The session of the window ends: an error that cannot be closed takes the line - in the place where the
        // summary was, not in a banner of its own.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => log.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Error", line.Title);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        AssertNoOldFeedback(window);

        // The entries stay - moved down by as much as the line grew - and can still be filtered and exported; an
        // object can no longer be opened from here.
        Assert.Single(window.EntriesList.Items);
        window.UpdateLayout();
        Assert.Equal(listTop + GrowthOf(line), TopOf(window.EntriesList, window), 0.5);
        Assert.False(log.OpenCommand.CanExecute(null));
        Assert.True(window.ExportButton.IsEnabled);
        Assert.True(window.CloseButton.IsEnabled);
        log.FilterText = "nie ma takiego wpisu";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Empty(window.EntriesList.Items);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>A context whose confirmations are answered with yes, so a change can be applied without a dialog.</summary>
    private static Task<UiTestContext> CreateContextAsync()
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
        return UiTestContext.CreateAsync(configure: services => services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs)));
    }

    /// <summary>The session log of a fresh session, shown off-screen.</summary>
    private static async Task<(SessionLogWindow Window, SessionLogViewModel Log)> OpenAsync(UiTestContext context)
    {
        await context.ConnectAsync();
        var log = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateSessionLog();
        var window = (SessionLogWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(log);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Empty(window.EntriesList.Items);
        return (window, log);
    }

    private static async Task ApplyAsync(UiTestContext context, string dn, string attribute, string value)
    {
        var outcome = await context.Services.GetRequiredService<IChangeCoordinator>()
            .ApplyAsync(new ChangeRequest([dn], [new SetAttributeOperation(attribute, value)], "Zmiana atrybutu"));
        Assert.Equal(ChangeOutcome.Executed, outcome);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(SessionLogWindow window)
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
