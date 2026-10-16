using System.Windows;
using System.Windows.Markup;
using AdMng.App.Localization;

namespace AdMng.App.Infrastructure;

/// <summary>
/// The WPF <see cref="FrameworkElement.Language"/> (culture of bindings with StringFormat, number/date conversions and
/// spell checking) follows the UI language of the localizer. Windows call <see cref="ApplyTo"/> when created; a
/// language switch updates the open windows.
/// </summary>
public static class UiLanguage
{
    /// <summary>The XML language of the current UI culture.</summary>
    public static XmlLanguage Current => XmlLanguage.GetLanguage(TranslationSource.Localizer.Culture.IetfLanguageTag);

    public static void ApplyTo(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.Language = Current;
    }
}
