using AdMng.Core.Audit;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Views;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>
/// Creates the view models of the windows opened by <see cref="IDialogService"/>. The WPF dialog service calls the
/// method matching the request, hosts the result as described by <see cref="IWindowViewModel"/> and reads the
/// result property after the window closed.
/// </summary>
public interface IDialogViewModelFactory
{
    /// <summary>For <see cref="IDialogService.ShowPropertiesAsync"/> (modeless).</summary>
    PropertySheetViewModel CreatePropertySheet(string distinguishedName);

    /// <summary>For <see cref="IDialogService.ShowBatchEditorAsync"/> (modeless). Empty targets = CSV import.</summary>
    BatchEditorViewModel CreateBatchEditor(BatchEditorRequest request);

    /// <summary>For <see cref="IDialogService.ShowResetPasswordAsync"/> (modal).</summary>
    ResetPasswordViewModel CreateResetPassword(IReadOnlyList<string> distinguishedNames);

    /// <summary>For <see cref="IDialogService.ShowNewObjectAsync"/> (modal); the result is <see cref="NewObjectViewModel.CreatedDn"/>.</summary>
    NewObjectViewModel CreateNewObject(ObjectKind kind, string parentDn);

    /// <summary>For <see cref="IDialogService.ShowFindAsync"/> (modeless).</summary>
    FindViewModel CreateFind(string? startDn);

    /// <summary>For <see cref="IDialogService.PickObjectsAsync"/> (modal); the result is <see cref="ObjectPickerViewModel.Result"/>.</summary>
    ObjectPickerViewModel CreateObjectPicker(IDirectorySession session, IReadOnlyList<ObjectKind> kinds, bool multiSelect);

    /// <summary>For <see cref="IDialogService.PickContainerAsync"/> (modal); the result is <see cref="ContainerPickerViewModel.Result"/>.</summary>
    ContainerPickerViewModel CreateContainerPicker(IDirectorySession session, string? startDn);

    /// <summary>For <see cref="IDialogService.ShowSessionLogAsync"/> (modeless).</summary>
    SessionLogViewModel CreateSessionLog();

    /// <summary>PowerShell of the changes executed in this session (Tools menu; modeless).</summary>
    PowerShellHistoryViewModel CreatePowerShellHistory();

    /// <summary>
    /// For <see cref="IDialogService.ShowSettingsAsync"/> (modal). Pass the main window's
    /// <c>ShellViewModel.ToggleAdvancedFeaturesCommand</c> so that Advanced Features reloads the tree and lists.
    /// </summary>
    SettingsViewModel CreateSettings(IAsyncRelayCommand? toggleAdvancedFeatures = null);
}

/// <summary>Default <see cref="IDialogViewModelFactory"/> (singleton).</summary>
public sealed class DialogViewModelFactory(
    DialogServices services,
    BatchEngines engines,
    ViewLibrary views,
    IAuditLog auditLog,
    AuditLogExporter auditExporter,
    PowerShellHistory powerShellHistory,
    IThemeService theme) : IDialogViewModelFactory
{
    public PropertySheetViewModel CreatePropertySheet(string distinguishedName) => new(services, engines.Planner, distinguishedName);

    public BatchEditorViewModel CreateBatchEditor(BatchEditorRequest request) => new(services, engines, request);

    public ResetPasswordViewModel CreateResetPassword(IReadOnlyList<string> distinguishedNames) =>
        new(services, engines.Planner, engines.Reports, distinguishedNames);

    public NewObjectViewModel CreateNewObject(ObjectKind kind, string parentDn) => new(services, engines.Templates, kind, parentDn);

    public FindViewModel CreateFind(string? startDn) => new(services, startDn);

    public ObjectPickerViewModel CreateObjectPicker(IDirectorySession session, IReadOnlyList<ObjectKind> kinds, bool multiSelect) =>
        new(services, session, kinds, multiSelect);

    public ContainerPickerViewModel CreateContainerPicker(IDirectorySession session, string? startDn) =>
        new(services, CreateTree(), session, startDn);

    public SessionLogViewModel CreateSessionLog() => new(services, auditLog, auditExporter);

    public PowerShellHistoryViewModel CreatePowerShellHistory() => new(services, powerShellHistory);

    public SettingsViewModel CreateSettings(IAsyncRelayCommand? toggleAdvancedFeatures = null) =>
        new(services, theme) { ShellToggleAdvancedFeatures = toggleAdvancedFeatures };

    private NavigationTreeViewModel CreateTree() =>
        new(services.Sessions, services.Settings, services.Localizer, services.Dispatcher, services.Errors, views, services.Changes);
}
