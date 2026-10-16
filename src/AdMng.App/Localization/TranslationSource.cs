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
        get => _localizer ??= new ResxLocalizer();
        set => _localizer = value ?? throw new ArgumentNullException(nameof(value));
    }
}
