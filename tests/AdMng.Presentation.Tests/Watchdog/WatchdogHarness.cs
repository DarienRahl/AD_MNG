using System.Globalization;
using AdMng.Core.Csv;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Watchdog;
using AdMng.Testing;
using AdMng.Watchdog.Testing.Client;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// The Watchdog tab's services over test doubles: <see cref="FakeWatchdogClient"/> with the Polish sample data, a
/// <see cref="FakeTimeProvider"/> for polling, debouncing and "now", a <see cref="TestUiThread"/> as the UI thread, and the
/// real AD side of <see cref="B2Harness"/> (sample company over FakeDirectorySession, real planner and executor) for the
/// pickers, "Pokaż w AD Manager" and unlock. Run view model code inside <see cref="TestUiThread.RunAsync(Func{Task})"/>.
/// </summary>
internal sealed class WatchdogHarness : IDisposable
{
    /// <summary>The service address of the sample client (as typed by the operator).</summary>
    public const string ServiceUrl = "https://admng-wd.contoso.com:8443";

    private readonly List<WatchdogDocumentViewModel> _documents = [];

    private WatchdogHarness(B2Harness ad, TimeZoneInfo? zone, ListLogger<PageStatusHolder>? statusLog)
    {
        Ad = ad;
        Time = new FakeTimeProvider(SampleEvents.Now);
        Time.SetLocalTimeZone(zone ?? TimeZoneInfo.Utc);
        Client = new FakeWatchdogClient(Time);
        Connection = new WatchdogConnectionService(Client, ad.Settings, ad.Localizer);
        Ui = new TestUiThread();
        StatusLog = statusLog;

        // The status log of the pages (value-free copies: key, counts, code) is the logger of the list services.
        var lists = statusLog is null
            ? ad.Lists
            : new ObjectListServices(ad.Sessions, ad.Localizer, ad.Dispatcher, ad.Settings, ad.Errors, ad.Changes, ad.Time, statusLog);
        var dialogServices = new DialogServices(
            ad.Sessions, ad.Localizer, Ui, ad.Settings, ad.Errors, ad.Changes, ad.Time, ad.Dialogs, ad.Files, ad.Clipboard,
            ad.Notifications, ad.Coordinator, ad.Activities, lists, ad.Regional);
        Services = new WatchdogServices(Connection, dialogServices, Csv, Time);
        Launcher = new WatchdogObjectHistoryLauncher(Connection);
    }

    /// <summary>The AD side: sample company, sessions, settings, scripted dialogs, files, clipboard, notifications.</summary>
    public B2Harness Ad { get; }

    public FakeTimeProvider Time { get; }

    public FakeWatchdogClient Client { get; }

    public WatchdogConnectionService Connection { get; }

    public WatchdogServices Services { get; }

    public WatchdogObjectHistoryLauncher Launcher { get; }

    public CsvCodec Csv { get; } = new();

    public TestUiThread Ui { get; }

    /// <summary>What the status holders of the pages wrote to the diagnostic log (null unless the harness was made with one).</summary>
    public ListLogger<PageStatusHolder>? StatusLog { get; }

    /// <param name="configured">A service address is stored in the settings.</param>
    /// <param name="adSession">An AD session (sample company) is open.</param>
    /// <param name="culture">UI language (default en-US).</param>
    /// <param name="zone">Local time zone of the operator (default UTC).</param>
    /// <param name="statusLog">A logger for the value-free copies of the statuses of the pages (a test reads what they wrote).</param>
    public static async Task<WatchdogHarness> CreateAsync(
        bool configured = true,
        bool adSession = true,
        CultureInfo? culture = null,
        TimeZoneInfo? zone = null,
        ListLogger<PageStatusHolder>? statusLog = null)
    {
        var ad = await B2Harness.CreateAsync(culture);
        if (!adSession)
        {
            await ad.Sessions.DisconnectAsync();
        }

        if (configured)
        {
            await ad.Settings.UpdateAsync(s => s.Watchdog.ServiceUrl = ServiceUrl);
        }

        return new WatchdogHarness(ad, zone, statusLog);
    }

    /// <summary>A new Watchdog tab (disposed with the harness). Create it on the UI thread.</summary>
    public WatchdogDocumentViewModel CreateDocument()
    {
        var document = new WatchdogDocumentViewModel(Services);
        _documents.Add(document);
        return document;
    }

    /// <summary>A Watchdog tab, connected, with the events (when visible) and the configuration loaded. UI thread.</summary>
    public async Task<WatchdogDocumentViewModel> OpenAsync(bool visible = true)
    {
        var document = CreateDocument();
        document.IsVisible = visible;
        await document.InitializeAsync();
        await document.Events.Loading;
        await document.Config.Loading;
        return document;
    }

    /// <summary>The main window's view model with the Watchdog tab factory and the object history launcher.</summary>
    public ShellViewModel CreateShell() => new(
        Ad.Lists,
        new NavigationTreeViewModel(Ad.Sessions, Ad.Settings, Ad.Localizer, Ad.Dispatcher, Ad.Errors, Ad.Views, Ad.Changes),
        Ad.Activities,
        Ad.Coordinator,
        Ad.Views,
        Ad.Dialogs,
        Ad.Files,
        Ad.Clipboard,
        Ad.Notifications,
        Csv,
        Ad.Regional,
        CreateDocument,
        Launcher);

    /// <summary>No exception escaped a callback on the UI thread.</summary>
    public void AssertNoUiErrors() => Assert.True(Ui.Errors.Count == 0, string.Join(Environment.NewLine, Ui.Errors));

    public void Dispose()
    {
        foreach (var document in _documents)
        {
            document.Dispose();
        }

        Ui.Dispose();
        Client.Dispose();
    }
}
