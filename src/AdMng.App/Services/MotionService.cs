using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using AdMng.App.Behaviors;
using AdMng.Presentation.Motion;
using AdMng.Presentation.Settings;

namespace AdMng.App.Services;

/// <summary>
/// Answers "does the interface move" (track V, stage V1; design note 5.4 and 5.5) and publishes the answer to the
/// behaviours through the one switch <see cref="Motion.IsEnabled"/>. The answer is
/// <see cref="MotionRules.Effective"/>: the operator's preference, where "follow Windows" means the Windows setting
/// "Animation effects" together with hardware accelerated drawing - both followed while the application runs - and a
/// forced switch-off that wins over everything. Application-wide, like <see cref="IconStyleService"/>;
/// <see cref="AppearanceService"/> feeds it from the settings.
/// </summary>
/// <remarks>
/// Motion is forced off for the whole run by the environment variable <see cref="TestRunVariable"/> = 1, by
/// <c>--smoke-test</c> and by <see cref="ForceOff"/> (the UI test host), so every test and every screenshot sees final
/// values. Progress indicators are not motion in this sense and are never switched off.
/// </remarks>
public sealed class MotionService : IDisposable
{
    /// <summary>The environment variable CI sets for UI test runs; the value "1" switches motion off.</summary>
    public const string TestRunVariable = "ADMNG_UI_TESTS";

    private readonly WpfUiDispatcher _dispatcher;
    private bool _followsSystem;
    private bool _disposed;

    /// <param name="dispatcher">The UI thread.</param>
    /// <param name="forcedOff">Motion is off for this run whatever the setting says (test run, smoke test).</param>
    public MotionService(WpfUiDispatcher dispatcher, bool forcedOff = false)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        IsForcedOff = forcedOff;
    }

    /// <summary>The preference applied last (follow Windows until <see cref="Apply"/> is called).</summary>
    public MotionPreference Preference { get; private set; } = MotionPreference.FollowWindows;

    /// <summary>Motion is switched off for this run; no preference switches it on again.</summary>
    public bool IsForcedOff { get; private set; }

    /// <summary>Whether the interface moves (false until <see cref="Apply"/> or <see cref="ForceOff"/> ran).</summary>
    public bool IsOn { get; private set; }

    /// <summary>The Windows setting "Animation effects" (replaced by the UI tests).</summary>
    internal Func<bool> WindowsAnimations { get; set; } = static () => SystemParameters.ClientAreaAnimation;

    /// <summary>Whether WPF draws with the graphics adapter: rendering tier 1 or 2 (replaced by the UI tests).</summary>
    internal Func<bool> HardwareAccelerated { get; set; } = static () => (RenderCapability.Tier >> 16) > 0;

    /// <summary>Whether the value of <see cref="TestRunVariable"/> asks for a run without motion.</summary>
    public static bool IsTestRun(string? variable) => string.Equals(variable?.Trim(), "1", StringComparison.Ordinal);

    /// <summary>Applies the preference of the settings. May be called from any thread.</summary>
    public void Apply(MotionPreference preference)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => Apply(preference));
            return;
        }

        if (_disposed)
        {
            return;
        }

        Preference = preference;
        if (!_followsSystem)
        {
            // Both belong to the UI thread: the rendering tier event is the one of this thread's media context.
            _followsSystem = true;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
            RenderCapability.TierChanged += OnTierChanged;
        }

        Refresh();
    }

    /// <summary>
    /// Switches motion off for the rest of the run (UI tests, the smoke test), before the first window. May be called
    /// from any thread.
    /// </summary>
    public void ForceOff()
    {
        IsForcedOff = true;
        if (_dispatcher.CheckAccess())
        {
            Refresh();
        }
        else
        {
            _ = _dispatcher.InvokeAsync(Refresh);
        }
    }

    /// <summary>Evaluates the rule again and publishes the answer. UI thread.</summary>
    internal void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        // Windows is only asked when its answer decides.
        var follows = !IsForcedOff && Preference == MotionPreference.FollowWindows;
        IsOn = MotionRules.Effective(Preference, follows && WindowsAnimations(), follows && HardwareAccelerated(), IsForcedOff);
        Motion.IsEnabled = IsOn;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsOn = false;

        // No service, no motion (the default of the switch).
        Motion.IsEnabled = false;
        if (!_followsSystem)
        {
            return;
        }

        _followsSystem = false;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;

        // The tier event lives in the media context of the UI thread: touching it from the thread that disposes the
        // host would create a media context for that thread.
        if (_dispatcher.CheckAccess())
        {
            RenderCapability.TierChanged -= OnTierChanged;
        }
        else if (!_dispatcher.Dispatcher.HasShutdownStarted)
        {
            _ = _dispatcher.Dispatcher.BeginInvoke(() => RenderCapability.TierChanged -= OnTierChanged);
        }
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation))
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Refresh();
        }
        else
        {
            _ = _dispatcher.InvokeAsync(Refresh);
        }
    }

    private void OnTierChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            Refresh();
        }
        else
        {
            _ = _dispatcher.InvokeAsync(Refresh);
        }
    }
}
