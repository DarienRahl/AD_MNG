using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;

namespace AdMng.App.Services;

/// <summary>
/// WPF <see cref="IDialogService"/>. Message boxes, the saved view editor and the column chooser are shown through
/// <see cref="IWindowService"/> (view model → registered window). The property sheet, batch editor, reset password,
/// new object, Find, pickers, session log and settings dialogs belong to the next UI wave: until their windows are
/// registered they show a localized "not available yet" message (pickers then return nothing).
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

    public DialogService(IWindowService windows, ILocalizer localizer, WpfUiDispatcher dispatcher, AppNavigator navigator, ToolWindows tools)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools)); // C2b
    }

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

    public async Task<string?> PickContainerAsync(IDirectorySession session, string? startDn)
    {
        ArgumentNullException.ThrowIfNull(session);
        await NotAvailableAsync(L.Ui.PickContainer).ConfigureAwait(true);
        return null;
    }

    public async Task<IReadOnlyList<string>> PickObjectsAsync(IDirectorySession session, IReadOnlyList<ObjectKind> kinds, bool multiSelect)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(kinds);
        await NotAvailableAsync(L.Ui.PickObjects).ConfigureAwait(true);
        return [];
    }

    public Task ShowPropertiesAsync(string distinguishedName)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        return NotAvailableAsync(L.Commands.Properties);
    }

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
        return NotAvailableAsync(L.Commands.ResetPassword);
    }

    public async Task<string?> ShowNewObjectAsync(ObjectKind kind, string parentDn)
    {
        ArgumentNullException.ThrowIfNull(parentDn);
        await NotAvailableAsync(kind switch
        {
            ObjectKind.User or ObjectKind.InetOrgPerson => L.Commands.NewUser,
            ObjectKind.Group => L.Commands.NewGroup,
            ObjectKind.OrganizationalUnit => L.Commands.NewOrganizationalUnit,
            ObjectKind.Computer => L.Commands.NewComputer,
            ObjectKind.Contact => L.Commands.NewContact,
            _ => L.Shell.MenuNew,
        }).ConfigureAwait(true);
        return null;
    }

    public Task ShowFindAsync(string? startDn) => NotAvailableAsync(L.Commands.Find);

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
