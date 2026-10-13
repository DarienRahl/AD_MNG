using System.Windows;
using AdMng.App.Views;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.Services;

/// <summary>
/// Opens the batch editor, the CSV import, the session log, the PowerShell history and the settings dialog: creates the
/// view model through <see cref="IDialogViewModelFactory"/> and shows it with <see cref="IWindowService"/> (the windows
/// host it with <see cref="Infrastructure.WindowViewModelLifecycle"/>). Used by <see cref="DialogService"/> and by the
/// main window's Tools menu (PowerShell history has no <see cref="IDialogService"/> entry). UI thread only.
/// </summary>
/// <remarks>
/// The factory is resolved on first use: it depends on <see cref="IDialogService"/> (through DialogServices), which
/// depends on this class.
/// </remarks>
public sealed class ToolWindows(IServiceProvider services, IWindowService windows, AppNavigator navigator)
{
    /// <summary>Singleton key of the session log window (one per application).</summary>
    public const string SessionLogKey = "tools:session-log";

    /// <summary>Singleton key of the PowerShell history window (one per application).</summary>
    public const string PowerShellHistoryKey = "tools:powershell-history";

    private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly IWindowService _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    private readonly AppNavigator _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));

    private IDialogViewModelFactory Factory => _services.GetRequiredService<IDialogViewModelFactory>();

    /// <summary>
    /// An open batch editor holds generated passwords that were not exported. The main window asks before it closes:
    /// owned windows are closed with it and cannot refuse.
    /// </summary>
    public bool HasUnsavedGeneratedPasswords =>
        Application.Current?.Windows.OfType<BatchEditorWindow>().Any(w => w.ViewModel.HasUnsavedGeneratedPasswords) == true;

    /// <summary>A new (modeless) batch editor; an empty target list opens the CSV import.</summary>
    public Window ShowBatchEditor(BatchEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _windows.Show(Factory.CreateBatchEditor(request));
    }

    /// <summary>The session log (modeless; an open one is activated).</summary>
    public Window ShowSessionLog() => ShowSingleton(SessionLogKey, () => Factory.CreateSessionLog());

    /// <summary>The PowerShell history (modeless; an open one is activated).</summary>
    public Window ShowPowerShellHistory() => ShowSingleton(PowerShellHistoryKey, () => Factory.CreatePowerShellHistory());

    /// <summary>
    /// The settings dialog (modal). Advanced Features is applied through the main window's shell, so the tree and the
    /// container tabs reload. Returns true when the operator saved.
    /// </summary>
    public bool ShowSettings()
    {
        var shell = (_navigator.MainWindow ?? Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible))?.Shell;
        var window = _windows.CreateWindow(Factory.CreateSettings(shell?.ToggleAdvancedFeaturesCommand));
        _windows.ShowDialog(window);
        return window is IDialogResultWindow { Result: true };
    }

    /// <summary>Activates the open window with <paramref name="key"/>, or creates the view model and shows a new one.</summary>
    private Window ShowSingleton(string key, Func<IWindowViewModel> create)
    {
        if (_windows.FindOpen(key) is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return open;
        }

        // Created only when needed: a view model that is never shown would never be disposed.
        return _windows.Show(create(), key);
    }
}
