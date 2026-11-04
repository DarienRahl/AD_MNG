using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Watchdog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Diagnostics;

/// <summary>
/// "Pomoc → Zgłoś problem…" (design note s-pilot-readiness.md 3): a read-only, value-free report of this connection
/// window – version, operating system, .NET, UI language, the logon type and transport flags of the window's connection,
/// the optional components, the last readiness results as codes and the diagnostic buffer as templates. Built in memory;
/// written to disk only when the operator saves it to a file of their choice. One window per connection window.
/// </summary>
public sealed partial class ProblemReportViewModel : WindowViewModelBase
{
    private readonly ProblemReportServices _report;

    public ProblemReportViewModel(DialogServices services, ProblemReportServices report)
        : base(services)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        Text = Build();
    }

    public override string WindowTitle => Localizer[L.ProblemReport.WindowTitle];

    /// <summary>Reads no directory data: it does not end with the session (Refresh shows the current connection).</summary>
    protected override bool UsesSession => false;

    /// <summary>The report as the operator copies or saves it.</summary>
    [ObservableProperty]
    public partial string Text { get; private set; }

    /// <summary>The facts of the report now (the current session of this window, the buffer, the readiness results).</summary>
    public ProblemReportContent Collect() => new()
    {
        CreatedAt = Services.Time.GetUtcNow(),
        Version = AppVersion.Display,
        OperatingSystem = RuntimeInformation.OSDescription,
        OperatingSystemArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        Runtime = RuntimeInformation.FrameworkDescription,
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        UiCulture = Localizer.Culture.Name,
        FormatCulture = CultureInfo.CurrentCulture.Name,
        Connection = Services.Sessions.Current is { } session ? ProblemReportConnection.Of(session.Info) : null,
        Outlook = _report.OutlookFeature,
        WebView2 = _report.WebView2Feature,
        WatchdogConfigured = WatchdogConnectionService.TryParseAddress(Services.Settings.Current.Watchdog.ServiceUrl, out _),
        Readiness = [.. _report.Readiness.Last],
        ReadinessRunAt = _report.Readiness.LastRunAt,
        ReadinessDepth = _report.Readiness.LastDepth,
        ReadinessCancelled = _report.Readiness.LastCancelled,
        Log = _report.Log.Snapshot(),
        LogTotal = _report.Log.Total,
    };

    /// <summary>Builds the report again (newer log entries, readiness results, another connection).</summary>
    [RelayCommand]
    private void Refresh() => Text = Build();

    [RelayCommand]
    private void Copy() => ClipboardCopy.Text(Services.Clipboard, Services.Notifications, Text, Localizer[L.ProblemReport.Copied]);

    /// <summary>Saves the report as UTF-8 text to a file the operator chooses (the only way it reaches a disk).</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        var title = Localizer[L.ProblemReport.SaveTitle];
        var time = TimeZoneInfo.ConvertTime(Services.Time.GetUtcNow(), Services.Time.LocalTimeZone);
        var path = await Services.Files.SaveFileAsync(title, string.Create(CultureInfo.InvariantCulture, $"admng-problem-report-{time:yyyyMMdd-HHmmss}.txt"),
            [new FileFilter(Localizer[L.ProblemReport.FileFilter], "*.txt")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.ProblemReport.Saved, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Close() => RequestClose(true);

    /// <summary>The labels follow the UI language.</summary>
    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        Text = Build();
    }

    private string Build() => ProblemReportBuilder.Build(Collect(), Localizer);
}
