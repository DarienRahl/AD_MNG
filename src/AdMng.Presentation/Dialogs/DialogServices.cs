using AdMng.Presentation.Changes;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Dialogs;

/// <summary>
/// Services shared by the dialog and secondary-window view models (parameter object, registered as a singleton
/// like <see cref="ObjectListServices"/>). Lets <see cref="IDialogViewModelFactory"/> and tests create view models
/// with one argument instead of a dozen.
/// </summary>
public sealed class DialogServices(
    ISessionManager sessions,
    ILocalizer localizer,
    IUiDispatcher dispatcher,
    ISettingsStore settings,
    ErrorTranslator errors,
    IDirectoryChangeNotifier changes,
    TimeProvider time,
    IDialogService dialogs,
    IFileDialogService files,
    IClipboardService clipboard,
    INotificationService notifications,
    IChangeCoordinator coordinator,
    ActivityCenterViewModel activities,
    ObjectListServices lists)
{
    public ISessionManager Sessions { get; } = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public ILocalizer Localizer { get; } = localizer ?? throw new ArgumentNullException(nameof(localizer));

    public IUiDispatcher Dispatcher { get; } = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public ISettingsStore Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    public ErrorTranslator Errors { get; } = errors ?? throw new ArgumentNullException(nameof(errors));

    /// <summary>Changes made through the pipeline (open property sheets refresh or flag themselves as outdated).</summary>
    public IDirectoryChangeNotifier Changes { get; } = changes ?? throw new ArgumentNullException(nameof(changes));

    public TimeProvider Time { get; } = time ?? throw new ArgumentNullException(nameof(time));

    public IDialogService Dialogs { get; } = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

    public IFileDialogService Files { get; } = files ?? throw new ArgumentNullException(nameof(files));

    public IClipboardService Clipboard { get; } = clipboard ?? throw new ArgumentNullException(nameof(clipboard));

    public INotificationService Notifications { get; } = notifications ?? throw new ArgumentNullException(nameof(notifications));

    /// <summary>Executes reviewed plans as background activities (CLAUDE.md rule 5).</summary>
    public IChangeCoordinator Coordinator { get; } = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <summary>Background operations (the batch editor shows the progress of its own execution).</summary>
    public ActivityCenterViewModel Activities { get; } = activities ?? throw new ArgumentNullException(nameof(activities));

    /// <summary>Creates object lists (Find results, picker results).</summary>
    public ObjectListServices Lists { get; } = lists ?? throw new ArgumentNullException(nameof(lists));
}
