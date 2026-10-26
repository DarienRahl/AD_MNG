using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AdMng.App.Connections;
using AdMng.App.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
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

    /// <summary>
    /// Finds an open window whose view model (DataContext) matches <paramref name="matchesViewModel"/>, for windows
    /// whose identity can change while they are open (a property sheet follows renames and moves of its object). Like
    /// <see cref="FindOpen(string)"/>, a window whose directory session ended is not reused.
    /// </summary>
    Window? FindOpen(Func<object?, bool> matchesViewModel);

    /// <summary>
    /// The window new dialogs of this connection should be owned by: its active window, else its main window, else its
    /// connect window (null when none is shown). Never a window of another connection.
    /// </summary>
    Window? GetOwner();

    /// <summary>Shows <paramref name="window"/> modally over <see cref="GetOwner"/>.</summary>
    bool? ShowDialog(Window window);
}

/// <summary>
/// Default <see cref="IWindowService"/>, one per connection window (scoped; design note docs/dev/multi-window.md 2.4).
/// Windows are created from the connection's scope, so a dialog opened in window B uses B's services all the way down.
/// Every window it prepares carries the connection id and (except the settings and message dialogs, which have their own
/// header) the connection's identity chrome; owners and "one per connection" lookups only consider windows of the
/// same connection.
/// </summary>
public sealed class WindowService : IWindowService
{
    private static readonly DependencyProperty SingletonKeyProperty =
        DependencyProperty.RegisterAttached("SingletonKey", typeof(string), typeof(WindowService), new PropertyMetadata(null));

    private readonly IServiceProvider _services;
    private readonly Dictionary<Type, WindowRegistration> _registrations;
    private readonly ConnectionContext? _connection;
    private readonly ILocalizer? _localizer;
    private readonly IConnectionAttention? _attention;

    /// <remarks>
    /// <c>connection</c>, <c>localizer</c> and <c>attention</c> are those of the connection window (null in hosts without
    /// connection windows: then every window counts as one connection).
    /// </remarks>
    public WindowService(
        IServiceProvider services,
        IEnumerable<WindowRegistration> registrations,
        ConnectionContext? connection = null,
        ILocalizer? localizer = null,
        IConnectionAttention? attention = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _connection = connection;
        _localizer = localizer;
        _attention = attention;
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
        var owner = GetOwner();
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (_connection is not null && IsAnotherConnectionActive())
        {
            // Raised by this connection's background work while the operator works in another window: the dialog must not
            // take the keyboard (Enter meant for that window would answer it). It waits without focus, the owner flashes in
            // the taskbar and the active window shows a toast; thread modality keeps the keystrokes away meanwhile.
            window.ShowActivated = false;
            if (owner is not null)
            {
                Flash(owner);
            }

            _attention?.DialogWaiting(_connection.Id);
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

        // Owned by this connection's main window: closing window A never closes B's batch editor.
        if (MainWindowOfConnection() is { } main && !ReferenceEquals(main, window))
        {
            window.Owner = main;
        }

        window.Show();
        return window;
    }

    public Window? FindOpen(string singletonKey)
    {
        ArgumentNullException.ThrowIfNull(singletonKey);
        return WindowsOfConnection()
            .FirstOrDefault(w => string.Equals((string?)w.GetValue(SingletonKeyProperty), singletonKey, StringComparison.Ordinal)
                && w.DataContext is not WindowViewModelBase { IsSessionEnded: true });
    }

    public Window? FindOpen(Func<object?, bool> matchesViewModel)
    {
        ArgumentNullException.ThrowIfNull(matchesViewModel);
        return WindowsOfConnection()
            .FirstOrDefault(w => w.DataContext is not WindowViewModelBase { IsSessionEnded: true } && matchesViewModel(w.DataContext));
    }

    public Window? GetOwner()
    {
        if (Application.Current is null)
        {
            return null;
        }

        var windows = WindowsOfConnection().Where(w => w.IsVisible).ToList();
        return windows.FirstOrDefault(w => w.IsActive)
            ?? MainWindowOfConnection()
            ?? windows.OfType<ConnectWindow>().FirstOrDefault()
            ?? (_connection is null ? windows.LastOrDefault() : null);
    }

    /// <summary>The windows of this connection (all windows in hosts without connection windows).</summary>
    private IEnumerable<Window> WindowsOfConnection()
    {
        var windows = Application.Current?.Windows.OfType<Window>() ?? [];
        return _connection is { } connection ? windows.Where(w => ConnectionChrome.BelongsTo(w, connection.Id)) : windows;
    }

    /// <summary>This connection's visible main window (in hosts without connection windows: the application's main window).</summary>
    private Window? MainWindowOfConnection() => _connection is null
        ? Application.Current?.MainWindow is { IsVisible: true } main ? main : Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible)
        : WindowsOfConnection().OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);

    private bool IsAnotherConnectionActive() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) is { } active
        && ConnectionChrome.GetConnectionId(active) is { } activeConnection
        && activeConnection != _connection!.Id;

    /// <summary>
    /// Gives every window the UI language (number/date formatting of bindings, spell checking), the connection id and -
    /// except the main, connect, settings and message windows - the connection's identity chrome (a snapshot).
    /// </summary>
    private void Prepare(Window window)
    {
        UiLanguage.ApplyTo(window);
        if (_connection is null)
        {
            return;
        }

        ConnectionChrome.SetConnectionId(window, _connection.Id);
        if (_localizer is not null && window is not (MainWindow or ConnectWindow or SettingsWindow or MessageDialogWindow))
        {
            ConnectionChrome.Apply(window, _connection.Identity, _localizer);
        }
    }

    /// <summary>Flashes the taskbar button of <paramref name="window"/> until it comes to the foreground (best effort).</summary>
    private static void Flash(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = handle,
            Flags = FlashAll | FlashUntilForeground,
            Count = uint.MaxValue,
            Timeout = 0,
        };
        _ = FlashWindowEx(ref info);
    }

    private const uint FlashAll = 0x3;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);
}
