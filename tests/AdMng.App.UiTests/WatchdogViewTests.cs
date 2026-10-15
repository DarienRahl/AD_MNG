using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Testing.Client;
using Microsoft.Extensions.DependencyInjection;

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
