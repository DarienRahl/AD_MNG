using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Settings;

/// <summary>
/// Settings dialog: language, theme, LDAP page size, batch parallelism, confirmation threshold, inactive days,
/// live updates and the view options. Save applies immediately (language without restart, theme).
/// </summary>
/// <remarks>
/// "Advanced Features" must go through the shell (it reloads the tree and the container tabs): the host sets
/// <see cref="ShellToggleAdvancedFeatures"/> to <c>ShellViewModel.ToggleAdvancedFeaturesCommand</c>. Without it the
/// setting is stored directly (it then applies to containers loaded later).
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

    public SettingsViewModel(DialogServices services, IThemeService theme)
        : base(services)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
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
    }

    public override string WindowTitle => Localizer[L.Settings.Title];

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

    /// <summary>The shell's Advanced Features toggle (set by the host; see the remarks of the class).</summary>
    public IAsyncRelayCommand? ShellToggleAdvancedFeatures { get; set; }

    /// <summary>Localized range error of the numeric settings, or null.</summary>
    public string? Error =>
        Range(L.Settings.PageSize, PageSize, MinPageSize, MaxPageSize)
        ?? Range(L.Settings.BatchParallelism, BatchMaxParallelism, MinParallelism, MaxParallelism)
        ?? Range(L.Settings.ConfirmationThreshold, ConfirmationThreshold, MinThreshold, MaxThreshold)
        ?? Range(L.Settings.InactiveDays, InactiveDays, MinInactiveDays, MaxInactiveDays);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Error is not null)
        {
            return;
        }

        var language = Language.Value;
        var theme = Theme.Value;
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
        }).ConfigureAwait(true);

        Localizer.SetCulture(ResxLocalizer.ResolveCulture(language));
        _theme.Apply(theme);
        if (ShowAdvancedFeatures != Services.Settings.Current.ShowAdvancedFeatures)
        {
            if (ShellToggleAdvancedFeatures is { } toggle)
            {
                await toggle.ExecuteAsync(null).ConfigureAwait(true);
            }
            else
            {
                var advanced = ShowAdvancedFeatures;
                await Services.Settings.UpdateAsync(s => s.ShowAdvancedFeatures = advanced).ConfigureAwait(true);
            }
        }

        RequestClose(true);
    }

    private bool CanSave() => Error is null;

    [RelayCommand]
    private void Cancel() => RequestClose(false);

    private string? Range(string labelKey, int value, int min, int max) =>
        value < min || value > max ? Localizer.Format(L.Settings.OutOfRange, Localizer[labelKey], min, max) : null;
}
