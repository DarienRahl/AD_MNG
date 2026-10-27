using AdMng.Presentation.Changes;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>
/// The Group Policy windows (design note docs/dev/k-computers-and-gpo.md 5.2), implemented by the App's dialog service of
/// the connection window (scoped). Every method may be called from any thread; the windows are owned by the connection's
/// main window and disposed when they close.
/// </summary>
public interface IGroupPolicyDialogs
{
    /// <summary>The change plan preview (modal). True when something was written to the directory or SYSVOL.</summary>
    Task<bool> ShowPlanPreviewAsync(ChangePlanPreviewViewModel preview);

    /// <summary>New GPO, copy or rename (modal). True when the operator confirmed (the result is on the view model).</summary>
    Task<bool> ShowNewGpoAsync(NewGpoViewModel dialog);

    /// <summary>Choose GPOs (link an existing GPO, compare with…) (modal). True when the operator chose.</summary>
    Task<bool> ShowGpoPickerAsync(GpoPickerViewModel picker);

    /// <summary>Backup, restore, import, backup before delete (modal). True when the window finished its task.</summary>
    Task<bool> ShowBackupAsync(GpoBackupViewModel dialog);

    /// <summary>The comparison of two GPOs (modeless).</summary>
    Task ShowComparisonAsync(GpoComparisonViewModel comparison);

    /// <summary>New or edited WMI filter (modal). True when the operator confirmed (the result is on the view model).</summary>
    Task<bool> ShowWmiFilterEditorAsync(WmiFilterEditorViewModel editor);

    /// <summary>The system folder picker: a folder the operator chooses for backups (null: cancelled).</summary>
    Task<string?> PickFolderAsync(string title, string? initialFolder);
}
