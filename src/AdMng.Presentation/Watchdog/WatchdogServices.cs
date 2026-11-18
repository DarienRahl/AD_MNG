using AdMng.Core.Csv;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Watchdog.Client;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Services shared by the Watchdog view models (parameter object, singleton): the watchdog client and its connection,
/// the UI services of the dialogs (<see cref="DialogServices"/>: AD session, dialogs, files, clipboard, change pipeline),
/// the CSV codec of the operator-initiated export and the clock of polling and debouncing.
/// </summary>
public sealed class WatchdogServices(WatchdogConnectionService connection, DialogServices ui, ICsvCodec csv, TimeProvider time)
{
    public WatchdogConnectionService Connection { get; } = connection ?? throw new ArgumentNullException(nameof(connection));

    public IWatchdogClient Client => Connection.Client;

    /// <summary>AD session, dialogs, files, clipboard, notifications and the change pipeline of the main window.</summary>
    public DialogServices Ui { get; } = ui ?? throw new ArgumentNullException(nameof(ui));

    public ICsvCodec Csv { get; } = csv ?? throw new ArgumentNullException(nameof(csv));

    /// <summary>Clock of the poller, debouncing and "now" (tests use a fake time provider).</summary>
    public TimeProvider Time { get; } = time ?? throw new ArgumentNullException(nameof(time));

    public ILocalizer Localizer => Ui.Localizer;

    public IUiDispatcher Dispatcher => Ui.Dispatcher;

    public ISettingsStore Settings => Ui.Settings;

    public ISessionManager Sessions => Ui.Sessions;

    public IDialogService Dialogs => Ui.Dialogs;

    /// <summary>Time zone of displayed times (the operator's local zone).</summary>
    public TimeZoneInfo TimeZone => Time.LocalTimeZone;

    /// <summary>
    /// A new status holder for a page (or a part of one) of the Watchdog tab (track V, stage V2b-4): the localizer, the UI
    /// dispatcher and the status logger of the window's list services, as every converted page makes it. The holder belongs to
    /// the view model that asks for it and dies with it.
    /// </summary>
    public PageStatusHolder CreateStatus() => new(Localizer, Dispatcher, Ui.Lists.StatusLog);
}
