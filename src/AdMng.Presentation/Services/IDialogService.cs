using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Views;

namespace AdMng.Presentation.Services;

/// <summary>
/// Opens dialogs and secondary windows. Implemented by the WPF app; every method is asynchronous so the
/// implementation may use modal windows, Fluent content dialogs or modeless windows as appropriate.
/// All texts passed in are already localized.
/// </summary>
public interface IDialogService
{
    Task ShowInfoAsync(string title, string message);

    Task ShowErrorAsync(string title, string message);

    /// <summary>Yes/No confirmation. <paramref name="destructive"/> styles the confirm button as dangerous.</summary>
    Task<bool> ConfirmAsync(string title, string message, bool destructive);

    /// <summary>
    /// Confirmation that requires the operator to type <paramref name="expectedText"/> (e.g. the number of objects)
    /// before the confirm button is enabled. Used for large or risky operations.
    /// </summary>
    Task<bool> ConfirmTypedAsync(string title, string message, string expectedText);

    /// <summary>ADUC-style container browser. Returns the chosen container DN or null when cancelled.</summary>
    Task<string?> PickContainerAsync(IDirectorySession session, string? startDn);

    /// <summary>
    /// "Select Users, Computers, or Groups" dialog. Returns the chosen DNs (empty when cancelled).
    /// </summary>
    Task<IReadOnlyList<string>> PickObjectsAsync(IDirectorySession session, IReadOnlyList<ObjectKind> kinds, bool multiSelect);

    /// <summary>Opens the (modeless) property sheet of an object. Completes when the window is shown.</summary>
    Task ShowPropertiesAsync(string distinguishedName);

    /// <summary>Opens the batch editor for the given targets, optionally pre-filled and positioned on the preview step.</summary>
    Task ShowBatchEditorAsync(BatchEditorRequest request);

    Task ShowResetPasswordAsync(IReadOnlyList<string> distinguishedNames);

    /// <summary>Opens the "New object" wizard. Returns the DN of the created object, or null when cancelled.</summary>
    Task<string?> ShowNewObjectAsync(ObjectKind kind, string parentDn);

    /// <summary>Opens the Find dialog (modeless) starting in <paramref name="startDn"/> (null = entire domain).</summary>
    Task ShowFindAsync(string? startDn);

    Task ShowSessionLogAsync();

    /// <summary>
    /// Opens the AD Recycle Bin window (modeless, one per application; an open one is activated). With
    /// <paramref name="select"/> the deleted object with that objectGUID is selected once the list is loaded (the
    /// Watchdog tab opens a deleted object this way).
    /// </summary>
    Task ShowRecycleBinAsync(Guid? select = null);

    Task ShowSettingsAsync();

    /// <summary>Shows the connection window (used after Disconnect).</summary>
    Task ShowConnectAsync();

    /// <summary>Shows the saved view editor. Returns true when the operator saved.</summary>
    Task<bool> ShowSavedViewEditorAsync(SavedViewEditorViewModel editor);

    /// <summary>Shows the column chooser of a list. Returns true when the operator applied the selection.</summary>
    Task<bool> ShowColumnChooserAsync(ColumnChooserViewModel chooser);

    /// <summary>Shows the Logon Hours dialog (modal). Returns true when the operator pressed OK.</summary>
    Task<bool> ShowLogonHoursAsync(Dialogs.LogonHoursViewModel editor);
}

/// <summary>What the batch editor should open with.</summary>
/// <param name="TargetDns">Objects the operations apply to.</param>
public sealed record BatchEditorRequest(IReadOnlyList<string> TargetDns)
{
    /// <summary>Operations to pre-fill (e.g. a simple action on more objects than the confirmation threshold).</summary>
    public IReadOnlyList<BatchOperation> Operations { get; init; } = [];

    /// <summary>Plan immediately and open on the preview step (only meaningful with <see cref="Operations"/>).</summary>
    public bool OpenPreview { get; init; }

    /// <summary>Localized title of the change ("Disable 120 accounts").</summary>
    public string Title { get; init; } = string.Empty;
}
