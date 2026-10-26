using System.Windows;
using AdMng.App.Connections;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using AdMng.Presentation.Views;
using AdMng.Presentation.Watchdog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// One per connection window (scoped): message dialogs carry the connection's header ("Domena: contoso.test …"), and after
/// the window closed (<see cref="MarkClosed"/>) leftover background work of the connection can no longer show anything:
/// messages are dropped and confirmations answer "no" (logged by type only).
/// </remarks>
public sealed class DialogService : IDialogService, IUserMessageLauncher
{
    private readonly IWindowService _windows;
    private readonly ILocalizer _localizer;
    private readonly WpfUiDispatcher _dispatcher;
    private readonly ConnectionNavigator? _navigator;
    private readonly ToolWindows _tools; // C2b
    private readonly ConnectionContext? _connection;
    private readonly ILogger<DialogService> _logger;
    private volatile bool _closed;

    // ---- C2a: dialog view model factory. Resolved lazily: DialogServices (inside the factory) depends on IDialogService.
    private readonly IServiceProvider _services;
    private IDialogViewModelFactory? _factory;

    /// <remarks>
    /// <c>navigator</c> and <c>connection</c> are those of the connection window (null in hosts without connection windows).
    /// </remarks>
    public DialogService(
        IWindowService windows,
        ILocalizer localizer,
        WpfUiDispatcher dispatcher,
        ToolWindows tools,
        IServiceProvider services,
        ConnectionNavigator? navigator = null,
        ConnectionContext? connection = null,
        ILogger<DialogService>? logger = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _navigator = navigator;
        _connection = connection;
        _logger = logger ?? NullLogger<DialogService>.Instance;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools)); // C2b
    }

    /// <summary>The connection window closed: nothing is shown any more (see the remarks of the class).</summary>
    public bool IsClosed => _closed;

    /// <summary>The connection window closed: from now on no dialog of this connection is shown.</summary>
    public void MarkClosed() => _closed = true;

    /// <summary>
    /// The error dialog of an unhandled exception (<see cref="IAppErrorPresenter"/>): owned by this connection's window but
    /// WITHOUT the connection header - the exception may come from another window's work.
    /// </summary>
    public Task ShowApplicationErrorAsync(string title, string message) =>
        ShowMessageAsync(MessageDialogViewModel.Error(title, message, _localizer), withHeader: false);

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

    public Task<bool> ShowLogonHoursAsync(LogonHoursViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return OnUiThreadAsync(() => ShowModal(editor));
    }

    public Task<bool> ShowWatchdogCertificateTrustAsync(WatchdogCertificateTrustViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        return OnUiThreadAsync(() => ShowModal(dialog));
    }

    public Task ShowConnectAsync() => OnUiThreadAsync(() =>
    {
        _navigator?.ShowConnect();
        return true;
    });

    /// <summary>"Change Domain Controller" (modal). True when the session moved to another DC.</summary>
    public Task<bool> ShowChangeDomainControllerAsync() =>
        OnUiThreadAsync(() => ShowWindowModal(Factory.CreateChangeDomainController()));

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

    /// <summary>
    /// One property sheet per object: a second request activates the open window. Sheets are found by their CURRENT DN
    /// (a sheet follows renames and moves of its object), not by the DN they were opened with, so the renamed object
    /// does not get a second sheet and a new object at the old DN does not reuse the stale one.
    /// </summary>
    public Task ShowPropertiesAsync(string distinguishedName)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        return OnUiThreadAsync(() =>
        {
            if (FindPropertySheet(distinguishedName) is { } open)
            {
                if (open.WindowState == WindowState.Minimized)
                {
                    open.WindowState = WindowState.Normal;
                }

                open.Activate();
                return true;
            }

            ShowWindowModeless(Factory.CreatePropertySheet(distinguishedName), singletonKey: null);
            return true;
        });
    }

    /// <summary>The open property sheet showing <paramref name="distinguishedName"/> (escaping and case ignored), or null.</summary>
    internal Window? FindPropertySheet(string distinguishedName) =>
        _windows.FindOpen(viewModel => viewModel is PropertySheetViewModel sheet && sheet.Shows(distinguishedName));

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

    // ---- AD Recycle Bin (modeless, one window) ----
    public Task ShowRecycleBinAsync(Guid? select = null) => OnUiThreadAsync(() =>
    {
        _tools.ShowRecycleBin(select);
        return true;
    });

    // ---- Account review, README U2 (modeless, one window) ----
    public Task ShowAccountReviewAsync() => OnUiThreadAsync(() =>
    {
        _tools.ShowAccountReview();
        return true;
    });

    // ---- Messages to users, README U1 (modeless, one window per launch) ----

    /// <summary>
    /// <see cref="IUserMessageLauncher"/>: the message window for the accounts, with the template matching the account
    /// review check (and its N days from the remembered review parameters).
    /// </summary>
    public Task ShowComposerAsync(IReadOnlyList<string> distinguishedNames, AccountCheck? reason)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        return OnUiThreadAsync(() =>
        {
            var parameters = _services.GetRequiredService<ISettingsStore>().Current.AccountReview.Parameters;
            _windows.Show(Factory.CreateUserMessageComposer(UserMessageRequest.For(distinguishedNames, reason, parameters)));
            return true;
        });
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    /// <summary>"«Feature» is not available in this version yet."</summary>
    private Task NotAvailableAsync(string featureTitleKey)
    {
        var feature = _localizer[featureTitleKey].TrimEnd('.', '…');
        return ShowInfoAsync(feature, _localizer.Format(L.Ui.NotAvailableYet, feature));
    }

    private Task<bool> ShowMessageAsync(MessageDialogViewModel dialog, bool withHeader = true) => OnUiThreadAsync(() =>
    {
        if (withHeader && _connection?.Identity is { } identity)
        {
            // Every information, error and confirmation dialog names the domain it is about.
            dialog.ConnectionHeader = ConnectionChrome.HeaderText(identity, _localizer);
            dialog.ConnectionColor = identity.Color;
        }

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
        if (_closed)
        {
            // Leftover work of a closed connection window: no window may appear (unowned, after the window is gone).
            _logger.LogInformation("A dialog of a closed connection window was not shown.");
            return Task.FromResult(false);
        }

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
