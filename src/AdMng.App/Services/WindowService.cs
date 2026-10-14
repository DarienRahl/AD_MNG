using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.Presentation.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.Services;

/// <summary>Maps a view model type to the window that shows it (see <see cref="WindowRegistrationExtensions.AddWindow{TViewModel, TWindow}"/>).</summary>
/// <param name="ViewModelType">The view model type (exact type of the object passed to <see cref="IWindowService"/>).</param>
/// <param name="WindowType">The window type; created with <see cref="ActivatorUtilities"/>, so its constructor may take
/// the view model and any registered service.</param>
public sealed record WindowRegistration(Type ViewModelType, Type WindowType);

/// <summary>DI helpers for <see cref="WindowRegistration"/>.</summary>
public static class WindowRegistrationExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TWindow"/> as the view of <typeparamref name="TViewModel"/>. The window is created
    /// per call; its constructor may take the view model (passed explicitly) and services. If the constructor does not
    /// set a DataContext, the view model becomes the DataContext.
    /// </summary>
    public static IServiceCollection AddWindow<TViewModel, TWindow>(this IServiceCollection services)
        where TViewModel : class
        where TWindow : Window
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new WindowRegistration(typeof(TViewModel), typeof(TWindow)));
        return services;
    }
}

/// <summary>
/// Creates and shows windows for view models (the extension point for property sheets, the batch editor and other
/// dialogs: register the window with <see cref="WindowRegistrationExtensions.AddWindow{TViewModel, TWindow}"/>, then
/// show the view model with <see cref="ShowDialog(object)"/> or <see cref="Show"/>). All members are UI-thread members.
/// </summary>
public interface IWindowService
{
    /// <summary>True when a window is registered for <paramref name="viewModelType"/>.</summary>
    bool IsRegistered(Type viewModelType);

    /// <summary>Creates the registered window for <paramref name="viewModel"/> (not shown yet).</summary>
    /// <exception cref="InvalidOperationException">No window is registered for the view model type.</exception>
    Window CreateWindow(object viewModel);

    /// <summary>Shows the view model's window modally over the active window. Returns the window's DialogResult.</summary>
    bool? ShowDialog(object viewModel);

    /// <summary>
    /// Shows the view model's window modelessly (owned by the main window). With <paramref name="singletonKey"/> an
    /// already open window with the same key is activated instead (e.g. one property sheet per object).
    /// </summary>
    Window Show(object viewModel, string? singletonKey = null);

    /// <summary>
    /// Finds an open window shown with <paramref name="singletonKey"/>. A window whose directory session ended
    /// (<see cref="WindowViewModelBase.IsSessionEnded"/>) is not reused: reopening it opens a new window in the current session.
    /// </summary>
    Window? FindOpen(string singletonKey);

    /// <summary>The window new dialogs should be owned by: the active window, else the main window (null when none is shown).</summary>
    Window? GetOwner();

    /// <summary>Shows <paramref name="window"/> modally over <see cref="GetOwner"/>.</summary>
    bool? ShowDialog(Window window);
}

/// <summary>Default <see cref="IWindowService"/>.</summary>
public sealed class WindowService : IWindowService
{
    private static readonly DependencyProperty SingletonKeyProperty =
        DependencyProperty.RegisterAttached("SingletonKey", typeof(string), typeof(WindowService), new PropertyMetadata(null));

    private readonly IServiceProvider _services;
    private readonly Dictionary<Type, WindowRegistration> _registrations;

    public WindowService(IServiceProvider services, IEnumerable<WindowRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _registrations = [];
        foreach (var registration in registrations)
        {
            // Last registration wins, so a later module can replace a default window.
            _registrations[registration.ViewModelType] = registration;
        }
    }

    public bool IsRegistered(Type viewModelType)
    {
        ArgumentNullException.ThrowIfNull(viewModelType);
        return _registrations.ContainsKey(viewModelType);
    }

    public Window CreateWindow(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (!_registrations.TryGetValue(viewModel.GetType(), out var registration))
        {
            throw new InvalidOperationException($"No window is registered for view model type {viewModel.GetType().FullName}.");
        }

        // ActivatorUtilities only accepts explicit arguments that a constructor consumes.
        var takesViewModel = registration.WindowType.GetConstructors()
            .Any(c => c.GetParameters().Any(p => p.ParameterType.IsInstanceOfType(viewModel)));
        var window = (Window)(takesViewModel
            ? ActivatorUtilities.CreateInstance(_services, registration.WindowType, viewModel)
            : ActivatorUtilities.CreateInstance(_services, registration.WindowType));
        if (window.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue)
        {
            window.DataContext = viewModel;
        }

        Prepare(window);
        return window;
    }

    public bool? ShowDialog(object viewModel) => ShowDialog(CreateWindow(viewModel));

    public bool? ShowDialog(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Prepare(window);
        if (GetOwner() is { } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return window.ShowDialog();
    }

    public Window Show(object viewModel, string? singletonKey = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (singletonKey is not null && FindOpen(singletonKey) is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return existing;
        }

        var window = CreateWindow(viewModel);
        window.SetValue(SingletonKeyProperty, singletonKey);
        if (Application.Current?.MainWindow is { IsVisible: true } main && !ReferenceEquals(main, window))
        {
            window.Owner = main;
        }

        window.Show();
        return window;
    }

    public Window? FindOpen(string singletonKey)
    {
        ArgumentNullException.ThrowIfNull(singletonKey);
        return Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => string.Equals((string?)w.GetValue(SingletonKeyProperty), singletonKey, StringComparison.Ordinal)
                && w.DataContext is not WindowViewModelBase { IsSessionEnded: true });
    }

    public Window? GetOwner()
    {
        if (Application.Current is not { } application)
        {
            return null;
        }

        var windows = application.Windows.OfType<Window>().Where(w => w.IsVisible).ToList();
        return windows.FirstOrDefault(w => w.IsActive)
            ?? (application.MainWindow is { IsVisible: true } main ? main : null)
            ?? windows.LastOrDefault();
    }

    /// <summary>Gives every window the UI language (number/date formatting of bindings, spell checking).</summary>
    private static void Prepare(Window window) => UiLanguage.ApplyTo(window);
}
