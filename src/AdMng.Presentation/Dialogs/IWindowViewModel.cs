using System.ComponentModel;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Dialogs;

/// <summary>
/// Contract of a view model hosted in its own window (property sheet, batch editor, pickers, Find, session log,
/// settings...). The WPF host treats all of them the same way:
/// <list type="number">
/// <item>create the view model through <see cref="IDialogViewModelFactory"/>, set it as the window's DataContext and
/// bind the title to <see cref="WindowTitle"/>;</item>
/// <item>subscribe to <see cref="CloseRequested"/>: close the window (modal windows set DialogResult to the argument);</item>
/// <item>when the operator closes the window with the title bar button / Alt+F4, cancel the close, await
/// <see cref="CanCloseAsync"/> and close only when it returns true;</item>
/// <item>after the window is shown call <see cref="InitializeAsync"/> (it never throws; failures are shown by the view model);</item>
/// <item>dispose the view model when the window has closed (unsubscribes from application-wide events).</item>
/// </list>
/// </summary>
public interface IWindowViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Localized window title (raises PropertyChanged when the language changes).</summary>
    string WindowTitle { get; }

    /// <summary>The view model asks the host to close the window; the argument is the dialog result.</summary>
    event EventHandler<bool?>? CloseRequested;

    /// <summary>Loads what the window shows. Never throws; safe to call fire-and-forget.</summary>
    Task InitializeAsync();

    /// <summary>
    /// Called before the operator closes the window (title bar / Alt+F4). Returns false to keep it open, e.g. when
    /// the operator declined to discard pending changes.
    /// </summary>
    Task<bool> CanCloseAsync();
}

/// <summary>Base class of window view models: title refresh on language change, close request, disposal.</summary>
public abstract class WindowViewModelBase : ObservableObject, IWindowViewModel
{
    protected WindowViewModelBase(DialogServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Services.Localizer.CultureChanged += OnLocalizerCultureChanged;
    }

    public event EventHandler<bool?>? CloseRequested;

    public abstract string WindowTitle { get; }

    /// <summary>True after <see cref="Dispose()"/> (the window closed).</summary>
    public bool IsDisposed { get; private set; }

    protected DialogServices Services { get; }

    protected ILocalizer Localizer => Services.Localizer;

    protected ErrorTranslator Errors => Services.Errors;

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual Task<bool> CanCloseAsync() => Task.FromResult(true);

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Services.Localizer.CultureChanged -= OnLocalizerCultureChanged;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases subscriptions of the derived view model.</summary>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>Raises <see cref="CloseRequested"/>.</summary>
    protected void RequestClose(bool? dialogResult) => CloseRequested?.Invoke(this, dialogResult);

    /// <summary>Called after the UI language changed (on the thread that changed it). Refreshes the title by default.</summary>
    protected virtual void OnCultureChanged() => OnPropertyChanged(nameof(WindowTitle));

    private void OnLocalizerCultureChanged(object? sender, EventArgs e) => OnCultureChanged();
}
