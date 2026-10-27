using AdMng.Presentation.Changes;
using AdMng.Presentation.GroupPolicy;
using Microsoft.Win32;

namespace AdMng.App.Services;

/// <summary>
/// The Group Policy windows of the connection window (K2, design note docs/dev/k-computers-and-gpo.md 5.2): the change
/// plan preview, the name dialog, the GPO picker, backup / restore / import, the WMI filter editor (modal) and the
/// comparison (modeless). Every window is owned by the connection's windows and disposes its view model when it closes.
/// </summary>
public sealed partial class DialogService : IGroupPolicyDialogs
{
    public Task<bool> ShowPlanPreviewAsync(ChangePlanPreviewViewModel preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return OnUiThreadAsync(() =>
        {
            ShowWindowModal(preview);
            return preview.ChangedDirectory;
        });
    }

    public Task<bool> ShowNewGpoAsync(NewGpoViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        return OnUiThreadAsync(() => ShowWindowModal(dialog) && dialog.Result is not null);
    }

    public Task<bool> ShowGpoPickerAsync(GpoPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        return OnUiThreadAsync(() => ShowWindowModal(picker) && picker.Result.Count > 0);
    }

    public Task<bool> ShowBackupAsync(GpoBackupViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        return OnUiThreadAsync(() => ShowWindowModal(dialog) | dialog.ChangedDirectory);
    }

    public Task ShowComparisonAsync(GpoComparisonViewModel comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return OnUiThreadAsync(() =>
        {
            _tools.ShowGpoComparison(comparison);
            return true;
        });
    }

    public Task<bool> ShowWmiFilterEditorAsync(WmiFilterEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return OnUiThreadAsync(() => ShowWindowModal(editor) && editor.Result is not null);
    }

    /// <summary>The system folder picker (the only place a backup may go: a folder the operator chooses, rule 1).</summary>
    public async Task<string?> PickFolderAsync(string title, string? initialFolder)
    {
        string? folder = null;
        await OnUiThreadAsync(() =>
        {
            var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
            if (!string.IsNullOrWhiteSpace(initialFolder) && System.IO.Directory.Exists(initialFolder))
            {
                dialog.InitialDirectory = initialFolder;
            }

            var owner = _windows.GetOwner();
            if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true)
            {
                folder = dialog.FolderName;
            }

            return folder is not null;
        }).ConfigureAwait(true);
        return folder;
    }
}
