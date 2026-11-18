using System.Globalization;
using AdMng.App.Localization;
using AdMng.Presentation.Localization;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// Gives a part of a test a localizer of its own in a known language, as a host would
/// (<see cref="TranslationSource.Localizer"/>), and puts the previous one back whatever happens. For tests of controls
/// that run without a <see cref="UiTestContext"/>: without it they would speak the language of whatever test ran before.
/// </summary>
internal sealed class LanguageScope : IDisposable
{
    private readonly ILocalizer _previous;

    private LanguageScope(ResxLocalizer localizer)
    {
        _previous = TranslationSource.Localizer;
        Localizer = localizer;
        TranslationSource.Localizer = localizer;
    }

    /// <summary>The localizer of the scope (what a view model of the test is created with).</summary>
    public ResxLocalizer Localizer { get; }

    /// <param name="culture">"en-US" or "pl-PL".</param>
    public static LanguageScope Use(string culture) => new(new ResxLocalizer(CultureInfo.GetCultureInfo(culture)));

    /// <summary>A language switch while the application runs (the settings window).</summary>
    public void Switch(string culture) => Localizer.SetCulture(CultureInfo.GetCultureInfo(culture));

    public void Dispose() => TranslationSource.Localizer = _previous;
}
