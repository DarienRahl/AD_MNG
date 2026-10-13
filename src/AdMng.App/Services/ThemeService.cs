using System.Windows;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;

namespace AdMng.App.Services;

/// <summary>
/// Switches the built-in Fluent theme (light / dark / follow Windows). The only place in the code base that uses
/// the experimental <see cref="Application.ThemeMode"/> API (WPF0001), so a future API change touches one file.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private readonly WpfUiDispatcher _dispatcher;

    public ThemeService(WpfUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>The preference applied last (System until <see cref="Apply"/> is called).</summary>
    public ThemePreference Current { get; private set; } = ThemePreference.System;

    public void Apply(ThemePreference preference)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => Apply(preference));
            return;
        }

        if (Application.Current is not { } application)
        {
            return;
        }

#pragma warning disable WPF0001 // ThemeMode is experimental in .NET 10; isolated in this service by design.
        var mode = preference switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (application.ThemeMode != mode)
        {
            application.ThemeMode = mode;
        }
#pragma warning restore WPF0001

        Current = preference;
    }
}
