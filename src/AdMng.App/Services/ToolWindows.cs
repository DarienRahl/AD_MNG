using System.Windows;
using AdMng.App.Views;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.Services;

/// <summary>
/// Opens the batch editor, the CSV import, the session log, the PowerShell history, the AD Recycle Bin, the account review, the saved filters and the settings dialog: creates the
/// view model through <see cref="IDialogViewModelFactory"/> and shows it with <see cref="IWindowService"/> (the windows
/// host it with <see cref="Infrastructure.WindowViewModelLifecycle"/>). Used by <see cref="DialogService"/> and by the
/// main window's Tools menu (PowerShell history has no <see cref="IDialogService"/> entry). UI thread only.
/// </summary>
/// <remarks>
/// The factory is resolved on first use: it depends on <see cref="IDialogService"/> (through DialogServices), which
/// depends on this class. One per connection window (scoped): the "one window" tool keys mean one per connection.
/// </remarks>
public sealed class ToolWindows(IServiceProvider services, IWindowService windows, Presentation.Connections.ConnectionContext? connection = null)
{
    /// <summary>Singleton key of the session log window (one per connection window).</summary>
    public const string SessionLogKey = "tools:session-log";

    /// <summary>Singleton key of the PowerShell history window (one per connection window).</summary>
    public const string PowerShellHistoryKey = "tools:powershell-history";

    /// <summary>Singleton key of the AD Recycle Bin window (one per connection window).</summary>
    public const string RecycleBinKey = "tools:recycle-bin";

    /// <summary>Singleton key of the account review window (one per connection window).</summary>
    public const string AccountReviewKey = "tools:account-review";

    /// <summary>
    /// Singleton key of the saved filters window (one per connection window: the filters are shared settings, but the
    /// builder picks attributes and groups and tests a filter through this connection's session).
    /// </summary>
    public const string SavedFiltersKey = "tools:saved-filters";

    private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly IWindowService _windows = windows ?? throw new ArgumentNullException(nameof(windows));

    private IDialogViewModelFactory Factory => _services.GetRequiredService<IDialogViewModelFactory>();

    /// <summary>
    /// An open batch editor of this connection holds generated passwords that were not exported. The main window asks
    /// before it closes: owned windows are closed with it and cannot refuse.
    /// </summary>
    public bool HasUnsavedGeneratedPasswords =>
        Application.Current?.Windows.OfType<BatchEditorWindow>()
            .Any(w => (connection is null || Connections.ConnectionChrome.BelongsTo(w, connection.Id)) && w.ViewModel.HasUnsavedGeneratedPasswords) == true;

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
    /// The AD Recycle Bin (modeless; an open one is activated). <paramref name="select"/>: the deleted object to select once
    /// the list is loaded.
    /// </summary>
    public Window ShowRecycleBin(Guid? select = null)
    {
        var window = ShowSingleton(RecycleBinKey, () =>
        {
            var bin = Factory.CreateRecycleBin();
            if (select is { } guid)
            {
                bin.SelectObject(guid);
            }

            return bin;
        });
        if (select is { } selected && window is RecycleBinWindow { ViewModel: { } open } && !open.IsDisposed)
        {
            open.SelectObject(selected);
        }

        return window;
    }

    /// <summary>The account review (modeless; an open one is activated).</summary>
    public Window ShowAccountReview() => ShowSingleton(AccountReviewKey, () => Factory.CreateAccountReview());

    /// <summary>The saved filters of the filter builder (modeless; an open one is activated).</summary>
    public Window ShowSavedFilters() => ShowSingleton(SavedFiltersKey, () => Factory.CreateSavedFilters());

    /// <summary>
    /// The settings dialog (modal). The settings are shared by all connection windows; every window's shell applies a
    /// changed Advanced Features setting itself. Returns true when the operator saved.
    /// </summary>
    public bool ShowSettings()
    {
        var window = _windows.CreateWindow(Factory.CreateSettings());
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
