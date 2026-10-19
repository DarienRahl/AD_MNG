using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Services;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// The Watchdog tab of the main window over the sample client: every sub-tab renders without binding errors, Viewers
/// get a read-only tab, the rollout banner and the tab badge follow the status, and the keyboard order of the feed's
/// filter bar follows the layout.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class WatchdogViewTests
{
    [Fact]
    public Task NotConfigured_ShowsTheAddressEditor_AndRefusesHttp() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        await using var context = await UiTestContext.CreateAsync(configure: WatchdogUiSupport.Use(client));
        var window = await ShowMainWindowAsync(context, connect: false);

        var (tab, view) = await WatchdogUiSupport.OpenAsync(window);

        Assert.Equal(WatchdogConnectionState.NotConfigured, tab.ConnectionState);
        Assert.Equal(Visibility.Visible, view.AddressEditor.Visibility);
        Assert.Equal(Visibility.Collapsed, view.SubTabs.Visibility);
        Assert.Equal(Visibility.Collapsed, view.RolloutBanner.Visibility);

        tab.ServiceUrlInput = "http://admng-wd.contoso.com:8443";
        await tab.ConnectCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal(Visibility.Visible, view.AddressError.Visibility);
        Assert.Equal(0, client.Calls.Count(nameof(IWatchdogClient.GetMeAsync)));

        tab.ServiceUrlInput = WatchdogUiSupport.ServiceUrl;
        await tab.ConnectCommand.ExecuteAsync(null);
        await tab.Events.Loading;
        await UiTestSupport.WaitForIdleAsync();

        Assert.True(tab.IsConnected);
        Assert.Equal(Visibility.Collapsed, view.AddressEditor.Visibility);
        Assert.Equal(Visibility.Visible, view.SubTabs.Visibility);
        context.AssertHealthy();
    });

    [Fact]
    public Task Admin_EverySubTab_RendersTheSampleData() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        await using var context = await UiTestContext.CreateAsync("pl", WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context, connect: true);
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);

        // Events: the feed and the details of the selected lockout.
        var events = view.EventsView;
        Assert.Equal(Visibility.Collapsed, events.EmptyHint.Visibility);
        events.EventsList.SelectedItem = tab.Events.Items.Single(r => r.Id == SampleEvents.LockoutId);
        await UiWait.UntilAsync(() => tab.Events.Details.Event?.Id == SampleEvents.LockoutId, "the details of the lockout");
        await tab.Events.Details.Loading;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Collapsed, events.DetailsView.NoSelectionHint.Visibility);
        Assert.Contains("Żaneta Szymańska", events.DetailsView.ObjectNameText.Text, StringComparison.Ordinal);
        Assert.True(events.DetailsView.LockoutSourcesList.Items.Count > 0);

        // Rules: the list and the editor of the custom rule.
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Rules);
        Assert.Equal(25, view.RulesView.RulesList.Items.Count);
        tab.Rules.SelectedRule = tab.Rules.Rules[^1];
        await UiWait.UntilAsync(() => view.RulesView.EditorView.ViewModel?.Id == "oddzial-krakow", "the editor of the custom rule");
        Assert.True(view.RulesView.EditorView.NameBox.IsEnabled);
        Assert.False(view.RulesView.ConfigBar.SaveButton.IsEnabled);

        // Channels: SMTP, recipient groups and the webhook editor with its secret fields.
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Channels);
        Assert.Equal("poczta.contoso.com", view.ChannelsView.SmtpHostBox.Text);
        Assert.Equal(2, view.ChannelsView.ChannelsList.Items.Count);
        tab.Channels.SelectedChannel = tab.Channels.Channels.Single(c => c.Id == "siem");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var editor = view.ChannelsView.ChannelEditor;
        Assert.True(editor.SigningSecretField.IsVisible);
        Assert.False(editor.TeamsSecretField.IsVisible);
        Assert.Equal(string.Empty, editor.SigningSecretField.PasswordInput.Password);

        // Digest: settings and the preview (kept in memory).
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Digest);
        Assert.Equal("07:30", view.DigestView.DigestTimeBox.Text);
        await tab.Digest.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Contains("Raport dzienny", view.DigestView.PreviewSubject.Text, StringComparison.Ordinal);

        // Status: cards, checks, delivery log and audit.
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Status);
        await UiWait.UntilAsync(() => tab.Status.HasStatus && tab.Status.Audit.Count > 0 && tab.Status.DeliveryLog.Items.Count > 0, "the status");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Equal(6, view.StatusView.ChecksList.Items.Count);
        Assert.Equal(Visibility.Visible, view.StatusView.DeliveryLogCard.Visibility);
        Assert.Equal(Visibility.Visible, view.StatusView.AuditCard.Visibility);
        Assert.False(tab.Config.IsDirty);
        context.AssertHealthy();
    });

    [Fact]
    public Task Viewer_GetsAReadOnlyTab() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient(admin: false);
        await using var context = await UiTestContext.CreateAsync(configure: WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context, connect: true);
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);

        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Rules);
        Assert.Equal(Visibility.Visible, view.RulesView.ConfigBar.ReadOnlyBar.Visibility);
        Assert.False(view.RulesView.ConfigBar.SaveButton.IsEnabled);
        Assert.False(view.RulesView.ConfigBar.ValidateButton.IsEnabled);
        Assert.False(view.RulesView.NewRuleButton.IsEnabled);

        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Channels);
        Assert.False(view.ChannelsView.SmtpHostBox.IsEnabled);
        Assert.False(view.ChannelsView.SendTestEmailButton.IsEnabled);

        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Status);
        await UiWait.UntilAsync(() => tab.Status.HasStatus, "the status");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Equal(Visibility.Collapsed, view.StatusView.DeliveryLogCard.Visibility);
        Assert.Equal(Visibility.Collapsed, view.StatusView.AuditCard.Visibility);
        Assert.Equal(0, client.Calls.Count(nameof(IWatchdogClient.GetOutboxAsync)));
        context.AssertHealthy();
    });

    [Fact]
    public Task RolloutBanner_AndTabBadge_FollowTheStatus() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient(rolloutUntil: SampleEvents.Now.AddDays(5));
        await using var context = await UiTestContext.CreateAsync(configure: WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context, connect: true);
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);

        await UiWait.UntilAsync(() => view.RolloutBanner.Visibility == Visibility.Visible, "the rollout banner");
        Assert.True(view.EndRolloutButton.IsEnabled);
        Assert.Equal("5", tab.BadgeText);
        var badge = UiWait.Descendants<TextBlock>(window).FirstOrDefault(t => t.Text == "5" && t.IsVisible);
        Assert.NotNull(badge);

        // A status without rollout (read by the poller or the Status sub-tab) hides the banner.
        client.Status = SampleStatus.Create();
        await tab.Status.LoadAsync();
        await UiWait.UntilAsync(() => view.RolloutBanner.Visibility == Visibility.Collapsed, "the banner to disappear");
        context.AssertHealthy();
    });

    [Fact]
    public Task FilterBar_KeyboardOrder_FollowsTheLayout_AndIconButtonsHaveNames() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        await using var context = await UiTestContext.CreateAsync(configure: WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context, connect: false);
        var (_, view) = await WatchdogUiSupport.OpenConnectedAsync(window);
        var events = view.EventsView;

        Control[] expected =
        [
            events.FilterTextBox, events.SeverityBox, events.UnresolvedToggle, events.UnacknowledgedToggle, events.FromPicker,
            events.ToPicker, events.MoreFiltersToggle, events.ClearFiltersButton, events.PresetBox,
        ];
        var order = UiWait.Descendants<Control>(events).Where(c => expected.Contains(c)).ToList();
        Assert.Equal(expected, order);
        Assert.All(expected, c =>
        {
            // A DatePicker and an editable ComboBox pass the focus to their text box.
            Assert.True(c.IsTabStop || c is DatePicker || c is ComboBox { IsEditable: true }, $"{c.Name} is not a tab stop");
            Assert.Equal(int.MaxValue, KeyboardNavigation.GetTabIndex(c));
        });

        // Buttons that show only a glyph are named for screen readers.
        var unnamed = UiWait.Descendants<ButtonBase>(view)
            .Where(b => b.IsVisible && b.Content is string { Length: <= 2 } && string.IsNullOrWhiteSpace(AutomationProperties.GetName(b)))
            .Select(b => b.Name)
            .ToList();
        Assert.Empty(unnamed);
        context.AssertHealthy();
    });

    /// <summary>
    /// The Events sub-tab at 1440 × 900 (the screenshot size): the state and host once in the banner, no empty object
    /// details pane, no empty saved-filter list, event titles not cut off, Critical told apart from High, and the
    /// product name on the details button.
    /// </summary>
    [Fact]
    public Task EventsLayout_At1440_ShowsTitlesAndNoEmptyOrRepeatedParts() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        await using var context = await UiTestContext.CreateAsync("pl", WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, 1440, 900);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, window.DetailsPane.Visibility);
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        // The main window's object details pane would stay empty next to the Watchdog tab.
        Assert.Equal(Visibility.Collapsed, window.DetailsPane.Visibility);
        Assert.Equal(0, window.DetailsColumn.ActualWidth);

        // "Połączono admng-wd.contoso.com:8443" once – no second "Połączono z admng-wd.contoso.com:8443".
        var bannerTexts = UiWait.Descendants<TextBlock>(view.ConnectionBanner).Where(t => t.IsVisible).Select(t => t.Text).ToList();
        Assert.Single(bannerTexts, t => t.Contains("admng-wd.contoso.com:8443", StringComparison.Ordinal));
        Assert.Equal(Visibility.Collapsed, view.ConnectionMessage.Visibility);

        // No saved filters: no empty, unlabeled list next to the reset button.
        var events = view.EventsView;
        Assert.Equal(Visibility.Collapsed, events.PresetPanel.Visibility);

        // Every shown column fits the feed (no sideways scrolling); the title takes what the others leave, so the titles
        // are readable, and the object keeps its room.
        var list = events.EventsList;
        AssertColumnsFit(list);
        Assert.True(Column(list, "Zdarzenie").ActualWidth >= 260, $"The title column is {Column(list, "Zdarzenie").ActualWidth} px wide.");
        Assert.True(Column(list, "Obiekt").ActualWidth >= 140, $"The object column is {Column(list, "Obiekt").ActualWidth} px wide.");
        var titles = UiWait.Descendants<TextBlock>(list)
            .Where(t => t.IsVisible && t.DataContext is WatchdogEventRowViewModel row && t.Text == row.KindText)
            .ToList();
        Assert.NotEmpty(titles);
        Assert.All(titles, t => Assert.True(t.ActualWidth + 0.5 >= TextWidth(t), $"'{t.Text}' is cut off."));

        // Critical and High: both red, Critical with a ring (and its semibold text).
        var dots = UiWait.Descendants<Ellipse>(list).Where(e => e.IsVisible).ToList();
        var critical = dots.First(e => Equals(e.Tag, Severity.Critical));
        var high = dots.First(e => Equals(e.Tag, Severity.High));
        Assert.True(critical.StrokeThickness > 0);
        var ring = Assert.IsType<SolidColorBrush>(critical.Stroke, exactMatch: false);
        Assert.NotEqual(Assert.IsType<SolidColorBrush>(critical.Fill, exactMatch: false).Color, ring.Color);
        Assert.True(high.Stroke is null || high.StrokeThickness == 0);

        // The product name, without an underscore WPF would take for an access key.
        Assert.Equal("Pokaż w AD Manager", events.DetailsView.OpenInAdMngButton.Content);

        // Back on a list tab the operator's details pane returns.
        window.Shell.SelectedTab = window.Shell.ListDocuments.First();
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, window.DetailsPane.Visibility);
        Assert.True(window.DetailsColumn.ActualWidth > 0);
        context.AssertHealthy();
    });

    /// <summary>
    /// The feed and the rule list at 1440 × 900 do not scroll sideways (every column ends inside the list, none under the
    /// vertical scroll bar) and fit again when the window or the details pane is resized.
    /// </summary>
    [Fact]
    public Task ListColumns_FitTheList_At1440_AndAfterResizing() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        await using var context = await UiTestContext.CreateAsync("pl", WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, 1440, 900);
        await window.Shell.InitializeAsync();
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        // The feed next to the details pane: severity, time, title and object always, the optional columns while they fit.
        var list = view.EventsView.EventsList;
        AssertColumnsFit(list);
        var shown = Headers(list);
        Assert.Equal(["Ważność", "Czas", "Zdarzenie", "Obiekt"], shown.Take(4));

        // A wide window shows every column; back at 1440 the same columns as before.
        await ScreenshotWriter.ShowWithClientSizeAsync(window, 1920, 1080);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        AssertColumnsFit(list);
        Assert.Equal(GridViewColumnFit.AllColumns(list).Count, ((GridView)list.View).Columns.Count);
        await ScreenshotWriter.ShowWithClientSizeAsync(window, 1440, 900);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        AssertColumnsFit(list);
        Assert.Equal(shown, Headers(list));

        // The details pane dragged to its minimum: the feed's columns take the 100 px (a wider title or more columns).
        var before = ((GridView)list.View).Columns.Sum(c => c.ActualWidth);
        var panes = (Grid)view.EventsView.DetailsSplitter.Parent;
        panes.ColumnDefinitions[2].Width = new GridLength(300);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        AssertColumnsFit(list);
        Assert.True(((GridView)list.View).Columns.Sum(c => c.ActualWidth) >= before + 90, "The feed's columns did not follow the splitter.");
        Assert.Superset(shown.ToHashSet(), Headers(list).ToHashSet());

        // The rules next to the editor: the delivery column ends inside the list.
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Rules);
        var rules = view.RulesView.RulesList;
        AssertColumnsFit(rules);
        Assert.Equal(4, ((GridView)rules.View).Columns.Count);
        context.AssertHealthy();
    });

    /// <summary>Channels at 1440 × 900: the SMTP authentication and the sender are shown in full.</summary>
    [Fact]
    public Task ChannelsLayout_At1440_ShowsTheSmtpValuesInFull() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        await using var context = await UiTestContext.CreateAsync("pl", WatchdogUiSupport.Use(client));
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, 1440, 900);
        await window.Shell.InitializeAsync();
        var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Channels);
        tab.Channels.SelectedChannel = tab.Channels.Channels.Single(c => c.Id == "siem");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        var channels = view.ChannelsView;
        var authentication = channels.SmtpAuthenticationBox;
        var selected = Assert.IsType<Option<SmtpAuthentication>>(authentication.SelectedItem);
        Assert.Equal("Nazwa użytkownika i hasło", selected.Text);
        var shown = UiWait.Descendants<TextBlock>(authentication).First(t => t.IsVisible && t.Text == selected.Text);
        Assert.True(shown.ActualWidth + 0.5 >= TextWidth(shown), $"'{shown.Text}' is cut off ({shown.ActualWidth} px).");

        var from = channels.SmtpFromBox;
        Assert.Equal("AD Watchdog <watchdog@contoso.com>", from.Text);
        Assert.True(from.ExtentWidth <= from.ViewportWidth + 0.5, $"The sender is cut off ({from.ViewportWidth} of {from.ExtentWidth} px).");
        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_AsksBeforeClosing_WhenTheWatchdogConfigurationIsUnsaved() => WpfTestHost.RunAsync(async () =>
    {
        var client = WatchdogUiSupport.CreateClient();
        var dialogs = new ScriptedDialogService { ConfirmAnswer = false };
        await using var context = await UiTestContext.CreateAsync("pl", services =>
        {
            WatchdogUiSupport.Use(client)(services);
            services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs));
        });
        await WatchdogUiSupport.ConfigureAddressAsync(context);
        var window = await ShowMainWindowAsync(context, connect: true);
        var (tab, _) = await WatchdogUiSupport.OpenConnectedAsync(window);
        tab.Config.Update(c => c with { Smtp = c.Smtp with { From = "Strażnik AD <straznik@contoso.com>" } });
        Assert.True(tab.HasUnsavedChanges);

        // The close is cancelled first; the question comes after the Closing event. Declined: the window stays.
        window.Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.IsVisible);
        Assert.Single(dialogs.Confirmations);
        Assert.Contains(tab, window.Shell.Documents);
        Assert.True(tab.Config.IsDirty);

        dialogs.ConfirmAnswer = true;
        window.Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.IsVisible);
        Assert.Equal(2, dialogs.Confirmations.Count);
        context.AssertHealthy();
    });

    /// <summary>
    /// <paramref name="list"/> does not scroll sideways and every column header ends inside the list, left of the vertical
    /// scroll bar (which the Fluent theme lays over the rows).
    /// </summary>
    private static void AssertColumnsFit(ListView list)
    {
        var viewer = UiWait.Descendants<ScrollViewer>(list).First();
        Assert.True(viewer.ExtentWidth <= viewer.ViewportWidth + 0.5, $"{list.Name} scrolls sideways: {viewer.ExtentWidth} px in {viewer.ViewportWidth} px.");
        Assert.NotEqual(Visibility.Visible, viewer.ComputedHorizontalScrollBarVisibility);
        var bar = viewer.Template.FindName("PART_VerticalScrollBar", viewer) as ScrollBar;
        var right = bar is { IsVisible: true } && bar.TranslatePoint(default, viewer).X is var left && left < viewer.ViewportWidth
            ? left
            : viewer.ViewportWidth;
        var headers = UiWait.Descendants<GridViewColumnHeader>(list).Where(h => h.Column is not null && h.IsVisible).ToList();
        Assert.Equal(((GridView)list.View).Columns.Count, headers.Count);
        Assert.All(headers, h =>
        {
            var edge = h.TranslatePoint(new Point(h.ActualWidth, 0), viewer).X;
            Assert.True(edge <= right + 0.5, $"Column '{h.Column.Header}' of {list.Name} ends at {edge} px, the list shows {right} px.");
        });
    }

    /// <summary>The headers of the shown columns ("✓" for the glyph header).</summary>
    private static List<string> Headers(ListView list) =>
        [.. ((GridView)list.View).Columns.Select(c => c.Header as string ?? "✓")];

    private static GridViewColumn Column(ListView list, string header) =>
        ((GridView)list.View).Columns.Single(c => Equals(c.Header, header));

    /// <summary>Width of <paramref name="text"/>'s whole text in its font (what it needs to be shown uncut).</summary>
    private static double TextWidth(TextBlock text) =>
        new FormattedText(
            text.Text,
            CultureInfo.CurrentUICulture,
            text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private static async Task<MainWindow> ShowMainWindowAsync(UiTestContext context, bool connect)
    {
        if (connect)
        {
            await context.ConnectAsync();
        }

        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();
        return window;
    }
}
