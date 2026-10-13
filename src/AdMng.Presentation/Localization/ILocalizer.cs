using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace AdMng.Presentation.Localization;

/// <summary>
/// Localized UI texts. Keys are the constants of <see cref="L"/> (plus the dynamic key families of
/// <see cref="DisplayNames"/>). A missing key yields <c>[key]</c> so gaps are visible but never crash the UI.
/// </summary>
public interface ILocalizer
{
    /// <summary>Text for <paramref name="key"/> in <see cref="Culture"/>.</summary>
    string this[string key] { get; }

    /// <summary><see cref="string.Format(IFormatProvider, string, object[])"/> over the localized text, using <see cref="Culture"/>.</summary>
    string Format(string key, params object?[] args);

    /// <summary>Current UI culture (also used for number/date formatting and sorting of lists).</summary>
    CultureInfo Culture { get; }

    /// <summary>True when the neutral (English) resources define <paramref name="key"/>.</summary>
    bool Contains(string key);

    /// <summary>Switches the language at runtime (no restart).</summary>
    void SetCulture(CultureInfo culture);

    /// <summary>Raised after <see cref="SetCulture"/> changed the culture (on the calling thread).</summary>
    event EventHandler? CultureChanged;
}

/// <summary>
/// <see cref="ILocalizer"/> over <c>Resources/Strings.resx</c> (English, neutral) and <c>Strings.pl.resx</c>.
/// Raises <see cref="INotifyPropertyChanged.PropertyChanged"/> for <c>Item[]</c> on culture change, so WPF
/// bindings like <c>{Binding [Shell.Refresh], Source={StaticResource Loc}}</c> refresh without a restart.
/// </summary>
public sealed class ResxLocalizer : ILocalizer, INotifyPropertyChanged
{
    /// <summary>Base name of the embedded resources.</summary>
    public const string ResourceBaseName = "AdMng.Presentation.Resources.Strings";

    private static readonly ResourceManager Resources = new(ResourceBaseName, typeof(ResxLocalizer).Assembly);

    private CultureInfo _culture;

    public ResxLocalizer()
        : this(CultureInfo.CurrentUICulture)
    {
    }

    public ResxLocalizer(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        _culture = culture;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? CultureChanged;

    public CultureInfo Culture => _culture;

    public string this[string key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);
            return Resources.GetString(key, _culture) ?? $"[{key}]";
        }
    }

    public string Format(string key, params object?[] args) => string.Format(_culture, this[key], args);

    public bool Contains(string key) => Resources.GetString(key, CultureInfo.InvariantCulture) is not null;

    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (string.Equals(culture.Name, _culture.Name, StringComparison.Ordinal))
        {
            return;
        }

        _culture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resolves the settings language ("" = system, "pl", "en") to a culture. Unknown or non-Polish system
    /// languages fall back to English, the neutral language of the resources.
    /// </summary>
    public static CultureInfo ResolveCulture(string? language, CultureInfo? systemCulture = null)
    {
        var code = string.IsNullOrWhiteSpace(language)
            ? (systemCulture ?? CultureInfo.InstalledUICulture).TwoLetterISOLanguageName
            : language.Trim();
        return string.Equals(code, "pl", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("pl-PL")
            : CultureInfo.GetCultureInfo("en-US");
    }
}
