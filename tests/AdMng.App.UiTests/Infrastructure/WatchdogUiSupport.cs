using System.Windows;
using AdMng.App.Views;
using AdMng.App.Views.Watchdog;
using AdMng.Core.Csv;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Testing.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// The Watchdog tab over <see cref="FakeWatchdogClient"/> (the Polish sample data) instead of the HTTPS client: no
/// service, no network. UI-thread members.
/// </summary>
internal static class WatchdogUiSupport
{
    /// <summary>The address stored in the settings (the fake accepts any https address).</summary>
    public const string ServiceUrl = "https://admng-wd.contoso.com:8443";

    /// <summary>
    /// A sample client on the system clock (or <paramref name="time"/>, see <see cref="UseClock"/>); <paramref name="admin"/>
    /// chooses the caller's role.
    /// </summary>
    public static FakeWatchdogClient CreateClient(bool admin = true, DateTimeOffset? rolloutUntil = null, TimeProvider? time = null)
    {
        var client = new FakeWatchdogClient(time ?? TimeProvider.System) { IsAdmin = admin };
        client.Status = SampleStatus.Create(rolloutUntil);

        // The application configures the client from the settings (WatchdogConnectionService).
        client.Configure(null);
        return client;
    }

    /// <summary>Registers <paramref name="client"/> as the application's <see cref="IWatchdogClient"/>.</summary>
    public static Action<IServiceCollection> Use(FakeWatchdogClient client) =>
        services => services.Replace(ServiceDescriptor.Singleton<IWatchdogClient>(client));

    /// <summary>
    /// The Watchdog tab on <paramref name="time"/> (polling rounds, "now"): a test moves a fake clock to the next status
    /// round instead of waiting 30 s. The rest of the application keeps the system clock.
    /// </summary>
    public static Action<IServiceCollection> UseClock(TimeProvider time) =>
        services => services.Replace(ServiceDescriptor.Singleton(sp => new WatchdogServices(
            sp.GetRequiredService<WatchdogConnectionService>(),
            sp.GetRequiredService<DialogServices>(),
            sp.GetRequiredService<ICsvCodec>(),
            time)));

    /// <summary>Stores the service address, as the operator would in the tab's address editor.</summary>
    public static Task ConfigureAddressAsync(UiTestContext context) =>
        context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(s => s.Watchdog.ServiceUrl = ServiceUrl);

    /// <summary>Tools → Watchdog in <paramref name="window"/>; waits for the connection attempt and the tab's view.</summary>
    public static async Task<(WatchdogDocumentViewModel Tab, WatchdogView View)> OpenAsync(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Assert.True(window.Shell.ShowWatchdogCommand.CanExecute(null));
        window.Shell.ShowWatchdogCommand.Execute(null);
        var tab = Assert.IsType<WatchdogDocumentViewModel>(window.Shell.SelectedTab);
        await tab.Connecting.WaitAsync(TimeSpan.FromSeconds(30));
        await UiWait.UntilAsync(() => FindView(window) is not null, "the view of the Watchdog tab");
        return (tab, FindView(window)!);
    }

    /// <summary>Opens the tab of a connected client and waits for the feed and the configuration.</summary>
    public static async Task<(WatchdogDocumentViewModel Tab, WatchdogView View)> OpenConnectedAsync(MainWindow window)
    {
        var (tab, view) = await OpenAsync(window);
        Assert.True(tab.IsConnected, $"The Watchdog tab did not connect: {tab.ConnectionState} {tab.ConnectionMessage}");
        await tab.Events.Loading.WaitAsync(TimeSpan.FromSeconds(30));
        await tab.Config.Loading.WaitAsync(TimeSpan.FromSeconds(30));
        await UiWait.UntilAsync(() => view.EventsView.EventsList.Items.Count == tab.Events.Items.Count && tab.Events.Items.Count > 0, "the event feed");
        return (tab, view);
    }

    /// <summary>Selects a sub-tab and lets the layout settle.</summary>
    public static async Task SelectAsync(WatchdogDocumentViewModel tab, WatchdogSubTab subTab)
    {
        tab.SelectedSubTab = subTab;
        await AdMng.App.Infrastructure.UiTestSupport.WaitForIdleAsync(rounds: 3);
    }

    /// <summary>The visible Watchdog view of the window, if any.</summary>
    public static WatchdogView? FindView(Window window) => UiWait.Descendants<WatchdogView>(window).FirstOrDefault(v => v.IsVisible);
}
