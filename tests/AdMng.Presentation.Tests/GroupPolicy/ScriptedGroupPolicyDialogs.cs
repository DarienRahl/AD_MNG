using AdMng.Core.Batch;
using AdMng.Presentation.Changes;
using AdMng.Presentation.GroupPolicy;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>What the scripted preview saw.</summary>
/// <param name="Title">The change title.</param>
/// <param name="Plan">The plan shown.</param>
/// <param name="PowerShell">The PowerShell tab.</param>
/// <param name="Rows">The preview rows ("action | item | before | after").</param>
/// <param name="Notes">The notes above the rows (PDC).</param>
/// <param name="Result">The execution result (null when not executed).</param>
internal sealed record PreviewSeen(string Title, ChangePlan Plan, string PowerShell, IReadOnlyList<string> Rows, IReadOnlyList<string> Notes, BatchResult? Result);

/// <summary>
/// <see cref="IGroupPolicyDialogs"/> with scripted answers: the preview executes the plan by default (Execute, as the operator
/// would), the name dialog accepts (optionally with another name), the picker chooses by name, the backup window and the
/// WMI editor run handlers; everything shown is recorded.
/// </summary>
internal sealed class ScriptedGroupPolicyDialogs : IGroupPolicyDialogs
{
    public List<PreviewSeen> Previews { get; } = [];

    /// <summary>Execute the previewed plan (default) or close the preview without executing.</summary>
    public bool ExecutePreviews { get; set; } = true;

    /// <summary>Also play "Cofnij" and execute the rollback after a successful execution.</summary>
    public bool UndoAfterExecute { get; set; }

    /// <summary>The name typed into the name dialog (null: keep the proposal); <see cref="CancelNameDialog"/> cancels.</summary>
    public string? NewGpoName { get; set; }

    public bool CancelNameDialog { get; set; }

    /// <summary>Copy: keep the source's permissions.</summary>
    public bool CopyPermissions { get; set; }

    public List<NewGpoViewModel> NameDialogs { get; } = [];

    /// <summary>The GPO the picker chooses (by name; null: cancel).</summary>
    public string? PickGpo { get; set; }

    public List<GpoPickerViewModel> Pickers { get; } = [];

    /// <summary>Plays the backup window (null: cancel).</summary>
    public Func<GpoBackupViewModel, Task<bool>>? BackupHandler { get; set; }

    public List<GpoBackupViewModel> BackupWindows { get; } = [];

    public List<GpoComparisonViewModel> Comparisons { get; } = [];

    /// <summary>Plays the WMI filter editor (null: cancel).</summary>
    public Func<WmiFilterEditorViewModel, bool>? WmiEditorHandler { get; set; }

    /// <summary>What "Przeglądaj…" returns.</summary>
    public string? Folder { get; set; }

    public async Task<bool> ShowPlanPreviewAsync(ChangePlanPreviewViewModel preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var rows = preview.Rows.Select(r => $"{r.Action} | {r.Attribute} | {r.Before} | {r.After}").ToList();
        BatchResult? result = null;
        if (ExecutePreviews && preview.ExecuteCommand.CanExecute(null))
        {
            await preview.ExecuteCommand.ExecuteAsync(null);
            result = preview.Result;
            if (UndoAfterExecute && preview.UndoCommand.CanExecute(null))
            {
                preview.UndoCommand.Execute(null);
                await preview.ExecuteCommand.ExecuteAsync(null);
            }
        }

        Previews.Add(new PreviewSeen(preview.ChangeTitle, preview.Plan, preview.PowerShellScript, rows, preview.Notes, result));
        return preview.ChangedDirectory;
    }

    public Task<bool> ShowNewGpoAsync(NewGpoViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        NameDialogs.Add(dialog);
        if (CancelNameDialog)
        {
            return Task.FromResult(false);
        }

        if (NewGpoName is { } name)
        {
            dialog.Name = name;
        }

        dialog.CopyPermissions = CopyPermissions;
        if (!dialog.OkCommand.CanExecute(null))
        {
            return Task.FromResult(false);
        }

        dialog.OkCommand.Execute(null);
        return Task.FromResult(true);
    }

    public async Task<bool> ShowGpoPickerAsync(GpoPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        Pickers.Add(picker);
        await picker.InitializeAsync();
        if (PickGpo is not { } name || picker.Items.FirstOrDefault(i => i.Name == name) is not { } item)
        {
            return false;
        }

        if (picker.MultiSelect)
        {
            item.IsChecked = true;
        }
        else
        {
            picker.SelectedItem = item;
        }

        picker.OkCommand.Execute(null);
        return picker.Result.Count > 0;
    }

    public async Task<bool> ShowBackupAsync(GpoBackupViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        BackupWindows.Add(dialog);
        await dialog.InitializeAsync();
        return BackupHandler is { } handler && await handler(dialog);
    }

    public async Task ShowComparisonAsync(GpoComparisonViewModel comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        Comparisons.Add(comparison);
        await comparison.InitializeAsync();
    }

    public Task<bool> ShowWmiFilterEditorAsync(WmiFilterEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return Task.FromResult(WmiEditorHandler?.Invoke(editor) ?? false);
    }

    public Task<string?> PickFolderAsync(string title, string? initialFolder) => Task.FromResult(Folder);
}
