using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.App.Services;

/// <summary>
/// Applies the language and theme of the settings to the running application (at start and whenever the settings
/// change, e.g. from the View menu or the settings dialog) and offers the View › Theme / Language menu commands.
/// The <c>--lang</c> command line override wins until the operator picks a language explicitly.
/// </summary>
public sealed partial class AppearanceService : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly ILocalizer _localizer;
    private readonly IThemeService _theme;
    private readonly WpfUiDispatcher _dispatcher;
    private string? _languageOverride;
    private string? _appliedLanguage;
    private ThemePreference? _appliedTheme;

    public AppearanceService(ISettingsStore settings, ILocalizer localizer, IThemeService theme, WpfUiDispatcher dispatcher)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Theme of the settings (for menu check marks).</summary>
    public ThemePreference Theme => _settings.Current.Theme;

    /// <summary>Language of the settings: "" (system), "pl" or "en".</summary>
    public string Language => _settings.Current.Language;

    /// <summary>Applies the current settings and follows later changes. Call once after the settings were loaded.</summary>
    /// <param name="languageOverride">"pl"/"en" from the command line, or null.</param>
    public void Start(string? languageOverride)
    {
        _languageOverride = languageOverride;
        _settings.Changed -= OnSettingsChanged;
        _settings.Changed += OnSettingsChanged;
        Apply();
    }

    /// <summary>Stops following the settings (tests).</summary>
    public void Stop() => _settings.Changed -= OnSettingsChanged;

    [RelayCommand]
    private Task SetThemeAsync(ThemePreference theme) => _settings.UpdateAsync(s => s.Theme = theme);

    [RelayCommand]
    private Task SetLanguageAsync(string? language)
    {
        // An explicit choice ends the command line override.
        _languageOverride = null;
        return _settings.UpdateAsync(s => s.Language = language ?? string.Empty);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _ = _dispatcher.InvokeAsync(Apply);

    private void Apply()
    {
        var language = _languageOverride ?? _settings.Current.Language;
        if (!string.Equals(language, _appliedLanguage, StringComparison.Ordinal))
        {
            _appliedLanguage = language;
            ApplyCulture(ResxLocalizer.ResolveCulture(language));
        }

        var theme = _settings.Current.Theme;
        if (theme != _appliedTheme)
        {
            _appliedTheme = theme;
            _theme.Apply(theme);
        }

        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(Language));
    }

    private void ApplyCulture(CultureInfo culture)
    {
        // UI culture of the thread (WPF built-in texts) and of new threads; formatting stays with the localizer culture,
        // which view models use explicitly.
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        _localizer.SetCulture(culture);
        var language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        if (Application.Current is { } application)
        {
            foreach (Window window in application.Windows)
            {
                window.Language = language;
            }
        }
    }
}
