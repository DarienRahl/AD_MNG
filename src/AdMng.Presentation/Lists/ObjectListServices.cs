using AdMng.Presentation.Changes;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Lists;

/// <summary>
/// Services shared by every object list (parameter object; registered as a singleton). Lets the shell, the Find
/// dialog and other hosts create <see cref="ObjectListViewModel"/>s with one argument.
/// </summary>
public sealed class ObjectListServices(
    ISessionManager sessions,
    ILocalizer localizer,
    IUiDispatcher dispatcher,
    ISettingsStore settings,
    ErrorTranslator errors,
    IDirectoryChangeNotifier changes,
    TimeProvider time)
{
    public ISessionManager Sessions { get; } = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public ILocalizer Localizer { get; } = localizer ?? throw new ArgumentNullException(nameof(localizer));

    public IUiDispatcher Dispatcher { get; } = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public ISettingsStore Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    public ErrorTranslator Errors { get; } = errors ?? throw new ArgumentNullException(nameof(errors));

    public IDirectoryChangeNotifier Changes { get; } = changes ?? throw new ArgumentNullException(nameof(changes));

    public TimeProvider Time { get; } = time ?? throw new ArgumentNullException(nameof(time));

    /// <summary>Creates a new, empty list (call <see cref="ObjectListViewModel.NavigateAsync"/> to show something).</summary>
    public ObjectListViewModel CreateList() => new(this);
}
