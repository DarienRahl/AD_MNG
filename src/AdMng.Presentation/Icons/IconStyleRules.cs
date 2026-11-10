using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Icons;

/// <summary>The rule that decides which icon style the application draws (track V, stage V1).</summary>
public static class IconStyleRules
{
    /// <summary>
    /// The style to draw: monochrome when the operator chose it or when Windows High Contrast is on (the glyphs take the
    /// system colours; the colour pictures have fixed colours), otherwise colour.
    /// </summary>
    /// <param name="preference">The setting (<see cref="AppSettings.IconStyle"/>).</param>
    /// <param name="highContrast">Whether Windows High Contrast is on.</param>
    public static IconStylePreference Effective(IconStylePreference preference, bool highContrast) =>
        highContrast || preference == IconStylePreference.Monochrome
            ? IconStylePreference.Monochrome
            : IconStylePreference.Colour;
}
