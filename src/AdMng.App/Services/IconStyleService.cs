using System.ComponentModel;
using System.Windows;
using AdMng.App.Controls;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Settings;

namespace AdMng.App.Services;

/// <summary>
/// Publishes the effective icon style (track V, stage V1) as the application resource
/// <see cref="ResourceKey"/>: the operator's preference, or monochrome whenever Windows High Contrast is on
/// (<see cref="IconStyleRules.Effective"/>). The icon elements (<see cref="ObjectIconView"/>, <see cref="AreaIconView"/>)
/// read the resource through a resource reference, so setting it re-renders the icons of every open window of every
/// connection at once - no element subscribes to an event. Application-wide, like <see cref="ThemeService"/>; it
/// follows High Contrast switches at run time until it is disposed.
/// </summary>
public sealed class IconStyleService : IDisposable
{
    /// <summary>Key of the application resource with the effective style (a boxed <see cref="IconStylePreference"/>).</summary>
    public const string ResourceKey = IconDrawing.StyleResourceKey;

    // One box per value: the resource is replaced only when the style really changes.
    private static readonly object ColourValue = IconStylePreference.Colour;
    private static readonly object MonochromeValue = IconStylePreference.Monochrome;

    private readonly WpfUiDispatcher _dispatcher;
    private bool _followsSystem;
    private bool _disposed;

    public IconStyleService(WpfUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>The preference applied last (colour until <see cref="Apply"/> is called).</summary>
    public IconStylePreference Preference { get; private set; } = IconStylePreference.Colour;

    /// <summary>The style the icons are drawn in: <see cref="Preference"/>, or monochrome in High Contrast.</summary>
    public IconStylePreference Effective { get; private set; } = IconStylePreference.Colour;

    /// <summary>Whether Windows High Contrast is on (replaced by the UI tests).</summary>
    internal Func<bool> HighContrast { get; set; } = static () => SystemParameters.HighContrast;

    /// <summary>Applies the preference of the settings. May be called from any thread.</summary>
    public void Apply(IconStylePreference preference)
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
            _followsSystem = true;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        }

        Refresh();
    }

    /// <summary>Evaluates the rule again (after a High Contrast switch) and updates the application resource.</summary>
    internal void Refresh()
    {
        var effective = IconStyleRules.Effective(Preference, HighContrast());
        Effective = effective;
        if (_disposed || Application.Current is not { } application)
        {
            return;
        }

        var resources = application.Resources;
        if (resources.Contains(ResourceKey) && resources[ResourceKey] is IconStylePreference current && current == effective)
        {
            return;
        }

        resources[ResourceKey] = effective == IconStylePreference.Monochrome ? MonochromeValue : ColourValue;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_followsSystem)
        {
            _followsSystem = false;
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        }
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast))
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
}
