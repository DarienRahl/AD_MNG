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
using AdMng.Presentation.Watchdog;

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

    /// <summary>For <see cref="IDialogService.ShowChangeDomainControllerAsync"/> (modal): switch the session to another DC.</summary>
    ChangeDomainControllerViewModel CreateChangeDomainController();

    /// <summary>For <see cref="IDialogService.ShowSessionLogAsync"/> (modeless).</summary>
    SessionLogViewModel CreateSessionLog();

    /// <summary>PowerShell of the changes executed in this session (Tools menu; modeless).</summary>
    PowerShellHistoryViewModel CreatePowerShellHistory();

    /// <summary>For <see cref="IDialogService.ShowRecycleBinAsync"/> (modeless): deleted objects of the current session's domain.</summary>
    RecycleBinViewModel CreateRecycleBin();

    /// <summary>For <see cref="IDialogService.ShowAccountReviewAsync"/> (modeless, one window): the account review (U2).</summary>
    AccountReview.AccountReviewViewModel CreateAccountReview();

    /// <summary>
    /// The message window of README U1 (modeless, one per launch), opened through <see cref="AccountReview.IUserMessageLauncher"/>.
    /// Throws <see cref="InvalidOperationException"/> when the host registered no <see cref="UserMessages.UserMessageServices"/>.
    /// </summary>
    UserMessages.UserMessageComposerViewModel CreateUserMessageComposer(UserMessages.UserMessageRequest request);

    /// <summary>
    /// For <see cref="IDialogService.ShowSettingsAsync"/> (modal). The settings are shared by all connection windows; every
    /// window's shell applies a changed Advanced Features setting itself.
    /// </summary>
    SettingsViewModel CreateSettings();

    /// <summary>
    /// "Porównaj z obiektem w innym połączeniu…" (modeless, one per launch): the object of this window against the matching
    /// object of another open connection, read-only.
    /// </summary>
    Comparison.ObjectComparisonViewModel CreateObjectComparison(string distinguishedName);
}

/// <summary>Default <see cref="IDialogViewModelFactory"/> (one per connection window: it creates the window's dialogs).</summary>
/// <remarks>
/// With a <see cref="WatchdogObjectHistoryLauncher"/> the property sheets offer "Historia zmian (Watchdog)"; with the
/// <see cref="IDirectoryConnector"/> "Change domain controller" also lists what the DC locator knows (client site), and
/// the account review and the Account tab read other domain controllers. With an
/// <see cref="AccountReview.IUserMessageLauncher"/> (path U1) "Wyślij wiadomość…" is shown; with
/// <see cref="UserMessages.UserMessageServices"/> the message window and the settings tab "Wiadomości e-mail" exist.
/// </remarks>
public sealed class DialogViewModelFactory(
    DialogServices services,
    BatchEngines engines,
    ViewLibrary views,
    IAuditLog auditLog,
    AuditLogExporter auditExporter,
    PowerShellHistory powerShellHistory,
    IThemeService theme,
    WatchdogObjectHistoryLauncher? historyLauncher = null,
    IDirectoryConnector? connector = null,
    AccountReview.IUserMessageLauncher? messages = null,
    UserMessages.UserMessageServices? userMessages = null,
    Connections.ConnectionRegistry? connections = null,
    Comparison.IObjectComparisonLauncher? comparison = null) : IDialogViewModelFactory
{
    private readonly AccountReview.DomainControllerReader _domainControllers = new(connector, services.Errors);

    public PropertySheetViewModel CreatePropertySheet(string distinguishedName) =>
        new(services, engines.Planner, distinguishedName)
        {
            HistoryLauncher = historyLauncher,
            DomainControllers = _domainControllers,
            MessageLauncher = messages,
            ComparisonLauncher = comparison,
        };

    public BatchEditorViewModel CreateBatchEditor(BatchEditorRequest request) => new(services, engines, request);

    public ResetPasswordViewModel CreateResetPassword(IReadOnlyList<string> distinguishedNames) =>
        new(services, engines.Planner, engines.Reports, distinguishedNames);

    public NewObjectViewModel CreateNewObject(ObjectKind kind, string parentDn) => new(services, engines.Templates, kind, parentDn);

    public FindViewModel CreateFind(string? startDn) => new(services, startDn);

    public ObjectPickerViewModel CreateObjectPicker(IDirectorySession session, IReadOnlyList<ObjectKind> kinds, bool multiSelect) =>
        new(services, session, kinds, multiSelect);

    public ContainerPickerViewModel CreateContainerPicker(IDirectorySession session, string? startDn) =>
        new(services, CreateTree(), session, startDn);

    public ChangeDomainControllerViewModel CreateChangeDomainController() => new(services, auditLog, connector);

    public SessionLogViewModel CreateSessionLog() => new(services, auditLog, auditExporter);

    public PowerShellHistoryViewModel CreatePowerShellHistory() => new(services, powerShellHistory);

    public RecycleBinViewModel CreateRecycleBin() => new(services);

    public AccountReview.AccountReviewViewModel CreateAccountReview() => new(services, views, engines.Csv, _domainControllers, messages);

    public UserMessages.UserMessageComposerViewModel CreateUserMessageComposer(UserMessages.UserMessageRequest request) =>
        new(services, userMessages ?? throw new InvalidOperationException("Messages to users are not registered."), request);

    public SettingsViewModel CreateSettings() => new(services, theme, userMessages);

    public Comparison.ObjectComparisonViewModel CreateObjectComparison(string distinguishedName) =>
        new(services, engines.Csv, distinguishedName, connections);

    private NavigationTreeViewModel CreateTree() =>
        new(services.Sessions, services.Settings, services.Localizer, services.Dispatcher, services.Errors, views, services.Changes);
}
