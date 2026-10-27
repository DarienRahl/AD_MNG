using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Watchdog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Settings;

/// <summary>
/// Settings dialog: language, theme, LDAP page size, batch parallelism, confirmation threshold, inactive days,
/// live updates, the view options, the Watchdog certificate pin, the messages to users (tab "Wiadomości e-mail", U1) (shown in groups of four hex digits, "Usuń
/// przypięcie" – stage B design 18 item 1). Save applies immediately (language without restart, theme); a removed pin
/// counts from the next connection to the Watchdog service.
/// </summary>
/// <remarks>
/// The settings are shared by every connection window. "Advanced Features" is only stored: the shell of every window reacts
/// to the settings change and reloads its tree and container tabs.
/// </remarks>
public sealed partial class SettingsViewModel : WindowViewModelBase
{
    public const int MinPageSize = 50;
    public const int MaxPageSize = 5000;
    public const int MinParallelism = 1;
    public const int MaxParallelism = 16;
    public const int MinThreshold = 1;
    public const int MaxThreshold = 100_000;
    public const int MinInactiveDays = 1;
    public const int MaxInactiveDays = 3650;

    private readonly IThemeService _theme;

    public SettingsViewModel(DialogServices services, IThemeService theme, UserMessages.UserMessageServices? userMessages = null)
        : base(services)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        if (userMessages is not null)
        {
            Messages = new UserMessages.UserMessageSettingsViewModel(services, userMessages);
            Messages.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserMessages.UserMessageSettingsViewModel.Error))
                {
                    OnPropertyChanged(nameof(Error));
                    SaveCommand.NotifyCanExecuteChanged();
                }
            };
        }

        // Computers (K1): the "Komputery" tab.
        Computers = new AdMng.Presentation.Computers.ComputerSettingsViewModel(services);
        Computers.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AdMng.Presentation.Computers.ComputerSettingsViewModel.Error))
            {
                OnPropertyChanged(nameof(Error));
                SaveCommand.NotifyCanExecuteChanged();
            }
        };

        Languages =
        [
            new(string.Empty, Localizer[L.Settings.LanguageSystem]),
            new("pl", Localizer[L.Settings.LanguagePolish]),
            new("en", Localizer[L.Settings.LanguageEnglish]),
        ];
        Themes = [.. Enum.GetValues<ThemePreference>().Select(t => new Option<ThemePreference>(t, Localizer[DisplayNames.ThemeKey(t)]))];
        var current = services.Settings.Current;
        Language = Languages.FirstOrDefault(l => l.Value == current.Language) ?? Languages[0];
        Theme = Themes.First(t => t.Value == current.Theme);
        PageSize = current.PageSize;
        BatchMaxParallelism = current.BatchMaxParallelism;
        ConfirmationThreshold = current.ConfirmationThreshold;
        InactiveDays = current.InactiveDays;
        LiveUpdates = current.LiveUpdates;
        ShowDetailsPane = current.ShowDetailsPane;
        ShowAdvancedFeatures = current.ShowAdvancedFeatures;
        PinnedCertificate = current.Watchdog.PinnedCertificateSha256;
    }

    public override string WindowTitle => Localizer[L.Settings.TitleShared];

    /// <summary>Application-wide; never acts in the directory session, so it does not end with it.</summary>
    protected override bool UsesSession => false;

    public IReadOnlyList<Option<string>> Languages { get; }

    [ObservableProperty]
    public partial Option<string> Language { get; set; }

    public IReadOnlyList<Option<ThemePreference>> Themes { get; }

    [ObservableProperty]
    public partial Option<ThemePreference> Theme { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial int PageSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial int BatchMaxParallelism { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial int ConfirmationThreshold { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial int InactiveDays { get; set; }

    [ObservableProperty]
    public partial bool LiveUpdates { get; set; }

    [ObservableProperty]
    public partial bool ShowDetailsPane { get; set; }

    [ObservableProperty]
    public partial bool ShowAdvancedFeatures { get; set; }

    /// <summary>The pinned SHA-256 of the Watchdog service certificate (until Save removes it), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPinnedCertificate), nameof(PinnedCertificateText))]
    [NotifyCanExecuteChangedFor(nameof(RemovePinCommand))]
    public partial string? PinnedCertificate { get; private set; }

    public bool HasPinnedCertificate => PinnedCertificate is not null;

    /// <summary>"3F2A 9C1E …" (two lines of eight groups), or "Żaden certyfikat nie jest przypięty."</summary>
    public string PinnedCertificateText => PinnedCertificate is { } pin
        ? WatchdogCertificateTrustViewModel.FormatFingerprint(pin)
        : Localizer[L.WatchdogLinux.CertificateNotPinned];

    /// <summary>"Usuń przypięcie" was chosen: Save removes the stored pin.</summary>
    [ObservableProperty]
    public partial bool IsPinRemoved { get; private set; }

    /// <summary>The "Wiadomości e-mail" tab (README U1), or null when the host registered no messages to users.</summary>
    public UserMessages.UserMessageSettingsViewModel? Messages { get; }

    /// <summary>The "Komputery" tab (K1): WinRM, restart defaults, placement rules, catalogue overrides.</summary>
    public AdMng.Presentation.Computers.ComputerSettingsViewModel Computers { get; }

    /// <summary>Localized range error of the numeric settings, or null.</summary>
    public string? Error =>
        Range(L.Settings.PageSize, PageSize, MinPageSize, MaxPageSize)
        ?? Range(L.Settings.BatchParallelism, BatchMaxParallelism, MinParallelism, MaxParallelism)
        ?? Range(L.Settings.ConfirmationThreshold, ConfirmationThreshold, MinThreshold, MaxThreshold)
        ?? Range(L.Settings.InactiveDays, InactiveDays, MinInactiveDays, MaxInactiveDays)
        ?? Messages?.Error
        ?? Computers.Error;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Error is not null)
        {
            return;
        }

        var language = Language.Value;
        var theme = Theme.Value;
        var advanced = ShowAdvancedFeatures;
        await Services.Settings.UpdateAsync(s =>
        {
            s.Language = language;
            s.Theme = theme;
            s.PageSize = PageSize;
            s.BatchMaxParallelism = BatchMaxParallelism;
            s.ConfirmationThreshold = ConfirmationThreshold;
            s.InactiveDays = InactiveDays;
            s.LiveUpdates = LiveUpdates;
            s.ShowDetailsPane = ShowDetailsPane;
            s.ShowAdvancedFeatures = advanced;
            if (IsPinRemoved)
            {
                s.Watchdog.PinnedCertificateSha256 = null;
            }

            Messages?.Apply(s);
            Computers.Apply(s);
        }).ConfigureAwait(true);

        Localizer.SetCulture(ResxLocalizer.ResolveCulture(language));
        _theme.Apply(theme);
        RequestClose(true);
    }

    private bool CanSave() => Error is null;

    [RelayCommand]
    private void Cancel() => RequestClose(false);

    /// <summary>"Usuń przypięcie": the pin is removed on Save; the next connection asks for a confirmation again.</summary>
    [RelayCommand(CanExecute = nameof(HasPinnedCertificate))]
    private void RemovePin()
    {
        PinnedCertificate = null;
        IsPinRemoved = true;
    }

    private string? Range(string labelKey, int value, int min, int max) =>
        value < min || value > max ? Localizer.Format(L.Settings.OutOfRange, Localizer[labelKey], min, max) : null;
}
