using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Batch;

/// <summary>
/// Rollback of SYSVOL steps (design note docs/dev/k-computers-and-gpo.md 3.3): a written file gets its old content back (a
/// created file is deleted), a deleted file is written again, a folder DACL gets the descriptor read while planning, the
/// version goes UP again on every half that changed (never back to the old number - clients must reprocess the restored
/// settings), a created folder is deleted only while it holds nothing but what the plan wrote, and a deleted folder is
/// not reversible (restore it from the backup taken before the delete).
/// </summary>
public sealed partial class RollbackPlanner
{
    private static void InvertSysvol(PlannedChange change, IReadOnlyList<PlannedChange> succeeded, List<PlannedChange> steps, List<ValidationIssue> issues)
    {
        var sysvol = change.Sysvol;
        switch (change.Action)
        {
            case ChangeAction.WriteGpoFile when sysvol is { NewContent: { } written }:
                steps.Add(sysvol.OldContent is null
                    ? new PlannedChange { Action = ChangeAction.DeleteGpoFile, Sysvol = sysvol with { OldContent = written, NewContent = null } }
                    : new PlannedChange { Action = ChangeAction.WriteGpoFile, Sysvol = sysvol with { OldContent = written, NewContent = sysvol.OldContent } });
                break;
            case ChangeAction.DeleteGpoFile when sysvol is { OldContent: { } deleted }:
                steps.Add(new PlannedChange { Action = ChangeAction.WriteGpoFile, Sysvol = sysvol with { OldContent = null, NewContent = deleted } });
                break;
            case ChangeAction.SyncGpoFolderSecurity or ChangeAction.SetGpoFolderSecurity when sysvol?.OldFolderSecurity is { } before:
                steps.Add(new PlannedChange
                {
                    Action = ChangeAction.SetGpoFolderSecurity,
                    Sysvol = sysvol with { OldFolderSecurity = sysvol.NewFolderSecurity, NewFolderSecurity = before },
                });
                break;
            case ChangeAction.SetGpoVersion when change.GpoVersion is { } version:
                var old = version.Old;
                try
                {
                    var next = version.New.Bump(user: old.User != version.New.User, computer: old.Computer != version.New.Computer);
                    steps.Add(new PlannedChange
                    {
                        Action = ChangeAction.SetGpoVersion,
                        Sysvol = sysvol,
                        GpoVersion = new GpoVersionChange(version.New, version.New, next),
                    });
                }
                catch (GpoVersionOverflowException)
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.GpoVersionOverflow,
                        "The GPO version reached its maximum; the restored settings cannot be published."));
                }

                break;
            case ChangeAction.CreateGpoFolder when sysvol is not null:
                steps.Add(GuardedFolderDelete(sysvol.GpoId, succeeded));
                break;
            case ChangeAction.DeleteGpoFolder:
                issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.NotReversible,
                    "A deleted GPO folder cannot be restored by a rollback; restore the GPO from the backup taken before the delete."));
                break;
            default:
                issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.NotReversible,
                    "This SYSVOL step cannot be undone: the plan does not hold the previous state."));
                break;
        }
    }

    /// <summary>The folder delete of a created GPO (when the plan created its folder), guarded by what the plan wrote.</summary>
    private static IEnumerable<PlannedChange> RollbackOfCreatedGpoFolder(IReadOnlyList<PlannedChange> succeeded)
    {
        if (succeeded.FirstOrDefault(c => c.Action == ChangeAction.CreateGpoFolder)?.Sysvol is { } created)
        {
            yield return GuardedFolderDelete(created.GpoId, succeeded);
        }
    }

    private static PlannedChange GuardedFolderDelete(Guid gpoId, IReadOnlyList<PlannedChange> succeeded)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in succeeded)
        {
            if (change.Sysvol is { RelativePath: { } path } sysvol && sysvol.GpoId == gpoId)
            {
                if (change.Action == ChangeAction.WriteGpoFile && sysvol.NewContent is { } content)
                {
                    files[path] = content;
                }
                else if (change.Action == ChangeAction.DeleteGpoFile)
                {
                    files.Remove(path);
                }
            }
        }

        return new PlannedChange
        {
            Action = ChangeAction.DeleteGpoFolder,
            Sysvol = new SysvolChange
            {
                GpoId = gpoId,
                OnlyIfPristine = true,
                PristineFiles = files,
                PristineVersion = succeeded.LastOrDefault(c => c.Action == ChangeAction.SetGpoVersion)?.GpoVersion?.New,
            },
        };
    }
}
