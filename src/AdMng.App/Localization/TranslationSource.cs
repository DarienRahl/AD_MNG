using System.ComponentModel;
using System.Globalization;
using AdMng.Presentation.Localization;

namespace AdMng.App.Localization;

/// <summary>
/// The <see cref="ILocalizer"/> that XAML bindings (<see cref="TrExtension"/>, <see cref="TrBinding"/>) read from.
/// The host sets it to the DI singleton before the first window is created, so a language switch
/// (<see cref="ILocalizer.SetCulture"/>) updates every bound text live.
/// </summary>
public static class TranslationSource
{
    private static ILocalizer? _localizer;

    /// <summary>The localizer of the running application (a standalone one until the host sets it).</summary>
    public static ILocalizer Localizer
    {
        get
        {
            if (_localizer is null)
            {
                Attach(new ResxLocalizer());
            }

            return _localizer!;
        }
        set => Attach(value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>
    /// What bindings read: one object for the whole process that forwards to <see cref="Localizer"/>. Styles and
    /// templates of the application resources are created once and keep their bindings; bound to this object (not to the
    /// localizer of the moment) they follow a language switch and a host that replaces the localizer (each UI test
    /// builds its own host).
    /// </summary>
    public static LocalizedTexts Texts { get; } = new();

    private static void Attach(ILocalizer localizer)
    {
        if (ReferenceEquals(localizer, _localizer))
        {
            return;
        }

        if (_localizer is not null)
        {
            _localizer.CultureChanged -= OnCultureChanged;
        }

        _localizer = localizer;
        localizer.CultureChanged += OnCultureChanged;
        Texts.Refresh();
    }

    private static void OnCultureChanged(object? sender, EventArgs e) => Texts.Refresh();
}

/// <summary>The localized texts of <see cref="TranslationSource.Localizer"/> for bindings (<c>[key]</c>).</summary>
public sealed class LocalizedTexts : INotifyPropertyChanged
{
    internal LocalizedTexts()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => TranslationSource.Localizer[key];

    public CultureInfo Culture => TranslationSource.Localizer.Culture;

    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
    }
}
