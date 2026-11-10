using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Motion;

/// <summary>The rule that decides whether the interface moves (track V, stage V1; design note 5.4).</summary>
public static class MotionRules
{
    /// <summary>
    /// Whether motion is on. A forced switch-off (UI tests, the smoke test) wins over everything; then the operator's
    /// choice; "follow Windows" is on only while Windows has "Animation effects" on and the display is hardware
    /// accelerated - without acceleration (a remote desktop session drawn in software, a virtual machine without a
    /// graphics adapter) every frame of an animation costs processor time the lists need.
    /// </summary>
    /// <param name="preference">The setting (<see cref="AppSettings.Motion"/>).</param>
    /// <param name="windowsAnimations">The Windows setting "Animation effects" (client area animations).</param>
    /// <param name="hardwareAccelerated">Whether WPF draws with the graphics adapter (rendering tier above 0).</param>
    /// <param name="forcedOff">Motion is switched off for this run, whatever the setting says.</param>
    public static bool Effective(MotionPreference preference, bool windowsAnimations, bool hardwareAccelerated, bool forcedOff)
    {
        if (forcedOff)
        {
            return false;
        }

        return preference switch
        {
            MotionPreference.On => true,
            MotionPreference.Off => false,
            _ => windowsAnimations && hardwareAccelerated,
        };
    }
}
