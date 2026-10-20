using System.Globalization;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// The "Źródła i retencja" sub-tab (stage B design 18 item 4): who-and-where attribution – the source offers "OpenWEC
/// (WEF)" only (a stored Windows source is shown with a hint until it is replaced), the collector settings of 7.8 (computer
/// and anonymous logons dropped at ingestion, silence warning/error per DC) –, GPO content tracking (8.5: on/off, SYSVOL
/// lag tolerance, read limits) and the retention of logon records (7.7). Edits go to the shared
/// <see cref="WatchdogConfigSession"/> (save bar, validation issues by path, Viewers read-only).
/// </summary>
public sealed partial class WatchdogSourcesViewModel : ObservableObject, IDisposable
{
    private readonly WatchdogServices _services;
    private bool _loading;

    public WatchdogSourcesViewModel(WatchdogServices services, WatchdogConfigSession session)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Session.Reloaded += OnReloaded;
        Session.IssuesChanged += OnIssuesChanged;
        Session.PropertyChanged += OnSessionPropertyChanged;
        Load();
    }

    public WatchdogConfigSession Session { get; }

    public bool CanEdit => Session.CanEdit;

    /// <summary>Validation messages by configuration path ("attribution.source", "directory.gpoContent.maxFileMb").</summary>
    public WatchdogFieldErrors Errors { get; } = new();

    // ---- Attribution and collector (7.8) ----------------------------------------------------------------------

    [ObservableProperty]
    public partial bool AttributionEnabled { get; set; }

    /// <summary>"OpenWEC (WEF)", plus the stored source when it is another one.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Option<AttributionSourceKind>> SourceOptions { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceWarning), nameof(HasSourceWarning))]
    public partial Option<AttributionSourceKind>? Source { get; set; }

    /// <summary>The selected source works only with the Windows service.</summary>
    public string SourceWarning => Source?.Value is AttributionSourceKind.DomainControllers or AttributionSourceKind.ForwardedEvents
        ? _services.Localizer[L.WatchdogLinux.AttributionSourceUnsupported]
        : string.Empty;

    public bool HasSourceWarning => SourceWarning.Length > 0;

    [ObservableProperty]
    public partial bool DropComputerLogons { get; set; }

    [ObservableProperty]
    public partial bool DropAnonymousLogons { get; set; }

    /// <summary>Minutes without a record from a DC before health Warning <c>wec.&lt;dc&gt;.silent</c>.</summary>
    [ObservableProperty]
    public partial string CollectorSilenceWarningMinutes { get; set; } = string.Empty;

    /// <summary>Minutes without a record from a DC before health Error (and HealthAttributionDegraded).</summary>
    [ObservableProperty]
    public partial string CollectorSilenceErrorMinutes { get; set; } = string.Empty;

    // ---- GPO content (8.5) ------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool GpoContentEnabled { get; set; }

    [ObservableProperty]
    public partial string GpoSysvolLagToleranceMinutes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GpoMaxFileMb { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GpoMaxTotalMb { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GpoMaxFiles { get; set; } = string.Empty;

    // ---- Retention (7.7) --------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string LogonRecordsHours { get; set; } = string.Empty;

    /// <summary>The configuration with the fields applied (unparsable numbers keep the loaded value).</summary>
    public WatchdogConfiguration Build(WatchdogConfiguration working)
    {
        ArgumentNullException.ThrowIfNull(working);
        var attribution = working.Attribution;
        var gpo = working.Directory.GpoContent;
        return working with
        {
            Attribution = attribution with
            {
                Enabled = AttributionEnabled,
                Source = Source?.Value ?? attribution.Source,
                DropComputerLogons = DropComputerLogons,
                DropAnonymousLogons = DropAnonymousLogons,
                CollectorSilenceWarning = Minutes(CollectorSilenceWarningMinutes, attribution.CollectorSilenceWarning),
                CollectorSilenceError = Minutes(CollectorSilenceErrorMinutes, attribution.CollectorSilenceError),
            },
            Directory = working.Directory with
            {
                GpoContent = gpo with
                {
                    Enabled = GpoContentEnabled,
                    SysvolLagTolerance = Minutes(GpoSysvolLagToleranceMinutes, gpo.SysvolLagTolerance),
                    MaxFileMb = Number(GpoMaxFileMb, gpo.MaxFileMb),
                    MaxTotalMb = Number(GpoMaxTotalMb, gpo.MaxTotalMb),
                    MaxFiles = Number(GpoMaxFiles, gpo.MaxFiles),
                },
            },
            Retention = working.Retention with { LogonRecordsHours = Number(LogonRecordsHours, working.Retention.LogonRecordsHours) },
        };
    }

    /// <summary>Texts after a language switch.</summary>
    public void Relocalize()
    {
        Load();
        OnPropertyChanged(nameof(SourceWarning));
    }

    public void Dispose()
    {
        Session.Reloaded -= OnReloaded;
        Session.IssuesChanged -= OnIssuesChanged;
        Session.PropertyChanged -= OnSessionPropertyChanged;
    }

    partial void OnAttributionEnabledChanged(bool value) => Commit();

    partial void OnSourceChanged(Option<AttributionSourceKind>? value) => Commit();

    partial void OnDropComputerLogonsChanged(bool value) => Commit();

    partial void OnDropAnonymousLogonsChanged(bool value) => Commit();

    partial void OnCollectorSilenceWarningMinutesChanged(string value) => Commit();

    partial void OnCollectorSilenceErrorMinutesChanged(string value) => Commit();

    partial void OnGpoContentEnabledChanged(bool value) => Commit();

    partial void OnGpoSysvolLagToleranceMinutesChanged(string value) => Commit();

    partial void OnGpoMaxFileMbChanged(string value) => Commit();

    partial void OnGpoMaxTotalMbChanged(string value) => Commit();

    partial void OnGpoMaxFilesChanged(string value) => Commit();

    partial void OnLogonRecordsHoursChanged(string value) => Commit();

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(TimeSpan value) => ((long)value.TotalMinutes).ToString(CultureInfo.InvariantCulture);

    private static int Number(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static TimeSpan Minutes(string text, TimeSpan fallback) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ? TimeSpan.FromMinutes(minutes) : fallback;

    private void Load()
    {
        _loading = true;
        try
        {
            var working = Session.Working ?? new WatchdogConfiguration();
            var localizer = _services.Localizer;
            var attribution = working.Attribution;
            var kinds = attribution.Source == AttributionSourceKind.OpenWec
                ? [AttributionSourceKind.OpenWec]
                : new[] { AttributionSourceKind.OpenWec, attribution.Source };
            SourceOptions = [.. kinds.Select(k => new Option<AttributionSourceKind>(k, localizer.AttributionSourceName(k)))];
            Source = SourceOptions.First(o => o.Value == attribution.Source);
            AttributionEnabled = attribution.Enabled;
            DropComputerLogons = attribution.DropComputerLogons;
            DropAnonymousLogons = attribution.DropAnonymousLogons;
            CollectorSilenceWarningMinutes = Text(attribution.CollectorSilenceWarning);
            CollectorSilenceErrorMinutes = Text(attribution.CollectorSilenceError);

            var gpo = working.Directory.GpoContent;
            GpoContentEnabled = gpo.Enabled;
            GpoSysvolLagToleranceMinutes = Text(gpo.SysvolLagTolerance);
            GpoMaxFileMb = Text(gpo.MaxFileMb);
            GpoMaxTotalMb = Text(gpo.MaxTotalMb);
            GpoMaxFiles = Text(gpo.MaxFiles);
            LogonRecordsHours = Text(working.Retention.LogonRecordsHours);
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(CanEdit));
        OnIssuesChanged(this, EventArgs.Empty);
    }

    private void Commit()
    {
        if (_loading || !CanEdit)
        {
            return;
        }

        Session.Update(Build);
    }

    private void OnReloaded(object? sender, EventArgs e) => Load();

    private void OnIssuesChanged(object? sender, EventArgs e) =>
        Errors.Set(Session.IssuesFor(string.Empty).Where(i => IsOwnPath(i.Issue.Path)), string.Empty);

    private static bool IsOwnPath(string path) =>
        path.StartsWith("attribution.", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("directory.gpoContent", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("retention.logonRecordsHours", StringComparison.OrdinalIgnoreCase);

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatchdogConfigSession.IsAdmin))
        {
            Load();
        }
    }
}
