using System.Windows;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.Services;

/// <summary>
/// WPF <see cref="IDialogService"/>. Message boxes, the saved view editor and the column chooser are shown through
/// <see cref="IWindowService"/> (view model → registered window). Property sheets, New Object, Reset Password, the
/// pickers and Find host the view models of <see cref="IDialogViewModelFactory"/>; the batch editor, the session log
/// and the settings go through <see cref="ToolWindows"/>.
/// </summary>
/// <remarks>
/// To plug in a dialog: register its window (<c>services.AddWindow&lt;TViewModel, TWindow&gt;()</c> in
/// <see cref="AppHost"/>), create the view model in the matching method below and show it with
/// <see cref="IWindowService.ShowDialog(object)"/> (modal) or <see cref="IWindowService.Show"/> (modeless).
/// Every method may be called from any thread; windows are always created on the UI thread.
/// </remarks>
public sealed class DialogService : IDialogService
{
    private readonly IWindowService _windows;
    private readonly ILocalizer _localizer;
    private readonly WpfUiDispatcher _dispatcher;
    private readonly AppNavigator _navigator;
    private readonly ToolWindows _tools; // C2b

    // ---- C2a: dialog view model factory. Resolved lazily: DialogServices (inside the factory) depends on IDialogService.
    private readonly IServiceProvider _services;
    private IDialogViewModelFactory? _factory;

    public DialogService(IWindowService windows, ILocalizer localizer, WpfUiDispatcher dispatcher, AppNavigator navigator, ToolWindows tools, IServiceProvider services)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools)); // C2b
    }

    private IDialogViewModelFactory Factory => _factory ??= _services.GetRequiredService<IDialogViewModelFactory>();

    // ---- Message boxes ----------------------------------------------------------------------------------------

    public Task ShowInfoAsync(string title, string message) =>
        ShowMessageAsync(MessageDialogViewModel.Information(title, message, _localizer));

    public Task ShowErrorAsync(string title, string message) =>
        ShowMessageAsync(MessageDialogViewModel.Error(title, message, _localizer));

    public Task<bool> ConfirmAsync(string title, string message, bool destructive) =>
        ShowMessageAsync(MessageDialogViewModel.Confirmation(title, message, destructive, _localizer));

    public Task<bool> ConfirmTypedAsync(string title, string message, string expectedText) =>
        ShowMessageAsync(MessageDialogViewModel.TypedConfirmation(title, message, expectedText, _localizer));

    // ---- Dialogs of this wave ---------------------------------------------------------------------------------

    public Task<bool> ShowSavedViewEditorAsync(SavedViewEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return OnUiThreadAsync(() => ShowModal(editor) && editor.Result is not null);
    }

    public Task<bool> ShowColumnChooserAsync(ColumnChooserViewModel chooser)
    {
        ArgumentNullException.ThrowIfNull(chooser);
        return OnUiThreadAsync(() => ShowModal(chooser) && chooser.Result.Count > 0);
    }

    public Task ShowConnectAsync() => OnUiThreadAsync(() =>
    {
        _navigator.ShowConnect();
        return true;
    });

    // ---- Dialogs of the next wave (property sheets, wizards, pickers) ----------------------------------------

    // ---- C2a: object dialogs (property sheets, New Object, Reset Password, pickers, Find) ----------------------
    // The windows call InitializeAsync when they are loaded and dispose their view model when they are closed.

    public async Task<string?> PickContainerAsync(IDirectorySession session, string? startDn)
    {
        ArgumentNullException.ThrowIfNull(session);
        string? result = null;
        await OnUiThreadAsync(() =>
        {
            var picker = Factory.CreateContainerPicker(session, startDn);
            result = ShowWindowModal(picker) ? picker.Result : null;
            return result is not null;
        }).ConfigureAwait(true);
        return result;
    }

    public async Task<IReadOnlyList<string>> PickObjectsAsync(IDirectorySession session, IReadOnlyList<ObjectKind> kinds, bool multiSelect)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(kinds);
        IReadOnlyList<string> result = [];
        await OnUiThreadAsync(() =>
        {
            var picker = Factory.CreateObjectPicker(session, kinds, multiSelect);
            result = ShowWindowModal(picker) ? picker.Result : [];
            return result.Count > 0;
        }).ConfigureAwait(true);
        return result;
    }

    /// <summary>One property sheet per object: a second request activates the open window.</summary>
    public Task ShowPropertiesAsync(string distinguishedName)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        return OnUiThreadAsync(() =>
        {
            var key = PropertiesWindowKey(distinguishedName);
            if (_windows.FindOpen(key) is { } open)
            {
                if (open.WindowState == WindowState.Minimized)
                {
                    open.WindowState = WindowState.Normal;
                }

                open.Activate();
                return true;
            }

            ShowWindowModeless(Factory.CreatePropertySheet(distinguishedName), key);
            return true;
        });
    }

    /// <summary>Singleton key of the property sheet of <paramref name="distinguishedName"/> (escaping and case ignored).</summary>
    internal static string PropertiesWindowKey(string distinguishedName) =>
        "properties:" + (DistinguishedName.TryParse(distinguishedName, out var dn) ? dn.ToString() : distinguishedName).ToUpperInvariant();

    // ---- C2b: batch editor / CSV import (modeless) ----
    public Task ShowBatchEditorAsync(BatchEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OnUiThreadAsync(() =>
        {
            _tools.ShowBatchEditor(request);
            return true;
        });
    }

    public Task ShowResetPasswordAsync(IReadOnlyList<string> distinguishedNames)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        return OnUiThreadAsync(() => ShowWindowModal(Factory.CreateResetPassword(distinguishedNames)));
    }

    public async Task<string?> ShowNewObjectAsync(ObjectKind kind, string parentDn)
    {
        ArgumentNullException.ThrowIfNull(parentDn);
        string? created = null;
        await OnUiThreadAsync(() =>
        {
            var dialog = Factory.CreateNewObject(kind, parentDn);
            ShowWindowModal(dialog);

            // Set as soon as the object exists (also when not all of its settings could be applied).
            created = dialog.CreatedDn;
            return created is not null;
        }).ConfigureAwait(true);
        return created;
    }

    public Task ShowFindAsync(string? startDn) => OnUiThreadAsync(() =>
    {
        ShowWindowModeless(Factory.CreateFind(startDn), singletonKey: null);
        return true;
    });

    // ---- C2b: session log (modeless, one window) and settings (modal) ----
    public Task ShowSessionLogAsync() => OnUiThreadAsync(() =>
    {
        _tools.ShowSessionLog();
        return true;
    });

    public Task ShowSettingsAsync() => OnUiThreadAsync(_tools.ShowSettings);

    // ---- Helpers ----------------------------------------------------------------------------------------------

    /// <summary>"«Feature» is not available in this version yet."</summary>
    private Task NotAvailableAsync(string featureTitleKey)
    {
        var feature = _localizer[featureTitleKey].TrimEnd('.', '…');
        return ShowInfoAsync(feature, _localizer.Format(L.Ui.NotAvailableYet, feature));
    }

    private Task<bool> ShowMessageAsync(MessageDialogViewModel dialog) => OnUiThreadAsync(() =>
    {
        var window = _windows.CreateWindow(dialog);
        _windows.ShowDialog(window);
        return window is IDialogResultWindow { Result: true };
    });

    /// <summary>Shows a modal dialog for <paramref name="viewModel"/>; true when it reported a positive result.</summary>
    private bool ShowModal(object viewModel)
    {
        var window = _windows.CreateWindow(viewModel);
        var dialogResult = _windows.ShowDialog(window);
        return window is IDialogResultWindow resultWindow ? resultWindow.Result == true : dialogResult == true;
    }

    // ---- C2a: hosting window view models -------------------------------------------------------------------

    /// <summary>
    /// Shows a window view model modally (the window initializes and disposes it). True when the window reported a
    /// positive result.
    /// </summary>
    private bool ShowWindowModal(IWindowViewModel viewModel)
    {
        Window window;
        try
        {
            window = _windows.CreateWindow(viewModel);
        }
        catch
        {
            viewModel.Dispose();
            throw;
        }

        bool? dialogResult;
        try
        {
            dialogResult = _windows.ShowDialog(window);
        }
        catch
        {
            // The window could not be shown, so it never closes and never disposes the view model (disposing twice is safe).
            viewModel.Dispose();
            throw;
        }

        return window is IDialogResultWindow resultWindow ? resultWindow.Result == true : dialogResult == true;
    }

    /// <summary>Shows a window view model modelessly (the window initializes and disposes it).</summary>
    private void ShowWindowModeless(IWindowViewModel viewModel, string? singletonKey)
    {
        try
        {
            _windows.Show(viewModel, singletonKey);
        }
        catch
        {
            viewModel.Dispose();
            throw;
        }
    }

    private Task<bool> OnUiThreadAsync(Func<bool> show)
    {
        if (_dispatcher.CheckAccess())
        {
            return Task.FromResult(show());
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _dispatcher.InvokeAsync(() =>
        {
            try
            {
                completion.SetResult(show());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }
}
