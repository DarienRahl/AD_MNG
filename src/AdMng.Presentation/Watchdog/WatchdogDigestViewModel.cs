using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// The "Raport dzienny" sub-tab: digest settings (time, days, recipients, channels, sections) and the preview – subject
/// and the plain-text alternative in a read-only box, kept in memory only, never written to disk – plus "Wyślij teraz"
/// (Admin).
/// </summary>
public sealed partial class WatchdogDigestViewModel : ObservableObject, IDisposable
{
    private readonly WatchdogServices _services;
    private DigestSettings _original = new();
    private bool _loading;

    public WatchdogDigestViewModel(WatchdogServices services, WatchdogConfigSession session)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Weekdays = CreateWeekdays();

        // Not an edit: Load() fills every field from the working copy without writing back.
        _loading = true;
        WeeklySectionsDay = Weekdays[1];
        _loading = false;
        Session.Reloaded += OnReloaded;
        Session.IssuesChanged += OnIssuesChanged;
        Session.PropertyChanged += OnSessionPropertyChanged;
        Load();
    }

    public WatchdogConfigSession Session { get; }

    public bool CanEdit => Session.CanEdit;

    public WatchdogFieldErrors Errors { get; } = new();

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    /// <summary>Local time "07:30".</summary>
    [ObservableProperty]
    public partial string Time { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? TimeError { get; private set; }

    public ObservableCollection<WatchdogCheckOption<DayOfWeek>> Days { get; } = [];

    public ObservableCollection<WatchdogCheckOption<string>> RecipientGroups { get; } = [];

    public ObservableCollection<WatchdogCheckOption<string>> Channels { get; } = [];

    [ObservableProperty]
    public partial bool SendWhenEmpty { get; set; }

    [ObservableProperty]
    public partial string MaxRowsPerSection { get; set; } = string.Empty;

    public IReadOnlyList<Option<DayOfWeek>> Weekdays { get; private set; }

    [ObservableProperty]
    public partial Option<DayOfWeek> WeeklySectionsDay { get; set; }

    [ObservableProperty]
    public partial string UpcomingExpiryDays { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CoverNonBusinessDays { get; set; }

    // ---- Preview ----------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(SendNowCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string PreviewSubject { get; private set; } = string.Empty;

    /// <summary>The plain-text alternative of the digest (read-only box).</summary>
    [ObservableProperty]
    public partial string PreviewText { get; private set; } = string.Empty;

    /// <summary>"Okres: … · zdarzeń: 27 · następna wysyłka: … · ostatnio: …".</summary>
    [ObservableProperty]
    public partial string PreviewInfo { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>The digest settings built from the fields.</summary>
    public DigestSettings Build() => _original with
    {
        Enabled = Enabled,
        Time = WatchdogEditValues.KeepUnchanged(_original.Time, Time, FormatTime, t => TryParseTime(t, out var time) ? time : _original.Time),
        Days = WatchdogEditValues.KeepOrder(_original.Days, Days.Where(d => d.IsChecked).Select(d => d.Value)),
        RecipientGroupIds = WatchdogEditValues.KeepOrder(_original.RecipientGroupIds, RecipientGroups.Where(g => g.IsChecked).Select(g => g.Value), StringComparer.Ordinal),
        ChannelIds = WatchdogEditValues.KeepOrder(_original.ChannelIds, Channels.Where(c => c.IsChecked).Select(c => c.Value), StringComparer.Ordinal),
        SendWhenEmpty = SendWhenEmpty,
        MaxRowsPerSection = Number(MaxRowsPerSection, _original.MaxRowsPerSection),
        WeeklySectionsDay = WeeklySectionsDay?.Value ?? _original.WeeklySectionsDay,
        UpcomingExpiryDays = Number(UpcomingExpiryDays, _original.UpcomingExpiryDays),
        CoverNonBusinessDays = CoverNonBusinessDays,
    };

    /// <summary>Texts after a language switch.</summary>
    public void Relocalize()
    {
        // A combo box may clear its selection while its items are replaced; that is not an edit.
        _loading = true;
        try
        {
            Weekdays = CreateWeekdays();
            OnPropertyChanged(nameof(Weekdays));
        }
        finally
        {
            _loading = false;
        }

        Load();
    }

    public void Dispose()
    {
        Session.Reloaded -= OnReloaded;
        Session.IssuesChanged -= OnIssuesChanged;
        Session.PropertyChanged -= OnSessionPropertyChanged;
    }

    /// <summary>The digest as it would be sent now (Viewers: their detail level).</summary>
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            var preview = await _services.Client.PreviewDigestAsync(CancellationToken.None).ConfigureAwait(true);
            var localizer = _services.Localizer;
            var zone = _services.TimeZone;
            PreviewSubject = preview.Subject;
            PreviewText = preview.Text;
            PreviewInfo = localizer.Format(
                L.Watchdog.DigestInfo,
                localizer.FormatTime(preview.PeriodStart, zone),
                localizer.FormatTime(preview.PeriodEnd, zone),
                preview.EventCount,
                preview.NextScheduledAt is { } next ? localizer.FormatTime(next, zone) : "—",
                preview.LastSentAt is { } last ? localizer.FormatTime(last, zone) : "—");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = _services.Connection.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanPreview() => !IsBusy;

    /// <summary>Sends the digest now (Admin; after a confirmation).</summary>
    [RelayCommand(CanExecute = nameof(CanSendNow))]
    private async Task SendNowAsync()
    {
        var localizer = _services.Localizer;
        if (!await _services.Dialogs.ConfirmAsync(localizer[L.Watchdog.DigestSendNow], localizer[L.Watchdog.DigestSendNowConfirm], destructive: false).ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            await _services.Client.SendDigestNowAsync(CancellationToken.None).ConfigureAwait(true);
            Message = localizer[L.Watchdog.DigestQueued];
            _services.Ui.Notifications.Show(NotificationSeverity.Success, Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = _services.Connection.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSendNow() => Session.IsAdmin && !IsBusy;

    partial void OnEnabledChanged(bool value) => Commit();

    partial void OnTimeChanged(string value) => Commit();

    partial void OnSendWhenEmptyChanged(bool value) => Commit();

    partial void OnMaxRowsPerSectionChanged(string value) => Commit();

    partial void OnWeeklySectionsDayChanged(Option<DayOfWeek> value) => Commit();

    partial void OnUpcomingExpiryDaysChanged(string value) => Commit();

    partial void OnCoverNonBusinessDaysChanged(bool value) => Commit();

    private static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static int Number(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private List<Option<DayOfWeek>> CreateWeekdays()
    {
        var names = _services.Localizer.Culture.DateTimeFormat.DayNames;
        return [.. Enum.GetValues<DayOfWeek>().Select(d => new Option<DayOfWeek>(d, names[(int)d]))];
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var working = Session.Working;
            _original = working?.Digest ?? new DigestSettings();
            var names = _services.Localizer.Culture.DateTimeFormat.DayNames;
            Enabled = _original.Enabled;
            Time = FormatTime(_original.Time);
            Days.Clear();
            var monday = (int)DayOfWeek.Monday;
            foreach (var day in Enum.GetValues<DayOfWeek>().OrderBy(d => ((int)d - monday + 7) % 7))
            {
                Days.Add(new WatchdogCheckOption<DayOfWeek>(day, names[(int)day], _original.Days.Contains(day), OnOptionChanged));
            }

            RecipientGroups.Clear();
            foreach (var group in working?.RecipientGroups ?? [])
            {
                RecipientGroups.Add(new WatchdogCheckOption<string>(group.Id, group.Name, _original.RecipientGroupIds.Contains(group.Id), OnOptionChanged));
            }

            Channels.Clear();
            foreach (var channel in working?.Channels ?? [])
            {
                Channels.Add(new WatchdogCheckOption<string>(channel.Id, channel.Name, _original.ChannelIds.Contains(channel.Id), OnOptionChanged));
            }

            SendWhenEmpty = _original.SendWhenEmpty;
            MaxRowsPerSection = _original.MaxRowsPerSection.ToString(CultureInfo.InvariantCulture);
            WeeklySectionsDay = Weekdays.First(d => d.Value == _original.WeeklySectionsDay);
            UpcomingExpiryDays = _original.UpcomingExpiryDays.ToString(CultureInfo.InvariantCulture);
            CoverNonBusinessDays = _original.CoverNonBusinessDays;
            TimeError = null;
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(CanEdit));
        SendNowCommand.NotifyCanExecuteChanged();
        OnIssuesChanged(this, EventArgs.Empty);
    }

    private void OnOptionChanged<T>(WatchdogCheckOption<T> option) => Commit();

    private void Commit()
    {
        if (_loading || !CanEdit)
        {
            return;
        }

        TimeError = TryParseTime(Time, out _) ? null : _services.Localizer[L.Watchdog.DigestTimeFormat];
        var digest = Build();
        Session.Update(c => c with { Digest = digest });
    }

    private void OnReloaded(object? sender, EventArgs e) => Load();

    private void OnIssuesChanged(object? sender, EventArgs e) => Errors.Set(Session.IssuesFor("digest."), "digest.");

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatchdogConfigSession.IsAdmin))
        {
            Load();
        }
    }
}
