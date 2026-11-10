using AdMng.Presentation.Settings;

namespace AdMng.App.Services;

/// <summary>
/// The window background setting (track V, stage V1): <see cref="WindowBackgroundPreference.Solid"/> switches off the
/// Mica backdrop the Fluent theme gives every themed window on Windows 11 22H2 and newer, for remote desktop sessions
/// and slower computers.
/// </summary>
/// <remarks>
/// WPF has no API to choose the backdrop, only the application context switch <see cref="DisableSwitchName"/>
/// (the name as it stands in PresentationFramework.dll 10.0.12). The theme reads it once per process - when the first
/// window gets its style and its backdrop - so <see cref="Apply"/> runs once at start, right after the settings were
/// loaded and before any window exists (<see cref="AppHost.InitializeAsync"/>, the same in the application, the smoke
/// test and the UI tests), and a later change of the setting waits for the next start.
/// </remarks>
internal static class WindowBackdrop
{
    /// <summary>The switch of the Fluent theme: true = windows get the solid background of the theme, no backdrop.</summary>
    public const string DisableSwitchName = "Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop";

    /// <summary>The value of the switch a preference stands for: only <see cref="WindowBackgroundPreference.Solid"/> disables the backdrop.</summary>
    public static bool DisablesBackdrop(WindowBackgroundPreference preference) => preference == WindowBackgroundPreference.Solid;

    /// <summary>
    /// Sets the switch for a preference that disables the backdrop and leaves it alone otherwise (the theme's default,
    /// or whatever the host of the process configured). Cannot fail: the name is a constant.
    /// </summary>
    public static void Apply(WindowBackgroundPreference preference)
    {
        if (DisablesBackdrop(preference))
        {
            AppContext.SetSwitch(DisableSwitchName, true);
        }
    }
}
