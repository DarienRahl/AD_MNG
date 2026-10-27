using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.Batch;

/// <summary>
/// Default <see cref="IRollbackPlanner"/>. For every object with at least one successful step it emits the inverse
/// steps in reverse order, addressed to the object's final DN. Old/new values are swapped so that the executor's
/// optimistic concurrency detects values changed by somebody else after the batch.
/// </summary>
/// <remarks>
/// Not reversible (reported as <see cref="IssueCodes.NotReversible"/> warnings, the step is omitted): password resets,
/// deletions, restores from the AD Recycle Bin (deleting the object again is not an undo to offer automatically) and unlocks. Restoring "must change password" uses pwdLastSet=-1 (the password age restarts now), which
/// is reported as a warning too. After a successful password reset pwdLastSet is never restored: the administrator-set
/// password stays (it cannot be undone), so "must change password at next logon" must stay as well.
/// A created object is rolled back by deleting it.
/// </remarks>
public sealed partial class RollbackPlanner : IRollbackPlanner
{
    /// <inheritdoc />
    public ChangePlan CreateRollbackPlan(BatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var objects = new List<ObjectPlan>();
        var deleted = DeletedReferents(result);
        foreach (var objectResult in result.Objects)
        {
            var succeeded = objectResult.Changes.Where(c => c.Succeeded).Select(Executed).ToList();
            if (succeeded.Count > 0)
            {
                objects.Add(WithoutReferencesTo(CreateObjectRollback(objectResult, succeeded), deleted));
            }
        }

        // A sequential plan (GPO plans) is undone in reverse object order: links before the container, children before it.
        if (result.Plan.Sequential)
        {
            objects.Reverse();
        }

        var title = string.IsNullOrEmpty(result.Plan.Title) ? "Rollback" : $"Rollback: {result.Plan.Title}";
        return new ChangePlan
        {
            Objects = objects,
            Title = title,
            CreatedAt = result.FinishedAt,
            Server = result.Server,
            Sequential = result.Plan.Sequential,
        };
    }

    /// <summary>
    /// The step as it was executed: a security step's delta is the part that changed the DACL, so the rollback does not
    /// remove an ACE another administrator added between the plan and its execution.
    /// </summary>
    private static PlannedChange Executed(ChangeResult result) =>
        result.AppliedAcl is { } applied && result.Change.Acl is { } planned
            ? result.Change with { Acl = planned with { Added = applied.Added, Removed = applied.Removed } }
            : result.Change;

    private static ObjectPlan CreateObjectRollback(ObjectResult objectResult, IReadOnlyList<PlannedChange> succeeded)
    {
        var plan = objectResult.Plan;
        var issues = new List<ValidationIssue>();
        var steps = new List<PlannedChange>();

        if (succeeded.Any(c => c.Action == ChangeAction.Create))
        {
            // A new GPO: its folder goes first (only while it holds nothing but what the plan wrote).
            steps.AddRange(RollbackOfCreatedGpoFolder(succeeded));
            steps.Add(new PlannedChange { Action = ChangeAction.Delete });
            return Build(objectResult, steps, issues);
        }

        // The new password cannot be taken back; removing "must change at next logon" would leave the account with a
        // password the administrator (and the exported results) know, without forcing the user to replace it.
        var passwordWasReset = succeeded.Any(c => c.Action == ChangeAction.ResetPassword);

        // DN before each step, so a rename/move can be undone to the exact original name/parent.
        var dnBefore = new List<string>(succeeded.Count);
        var currentDn = plan.DistinguishedName;
        foreach (var change in succeeded)
        {
            dnBefore.Add(currentDn);
            currentDn = DnAfter(currentDn, change);
        }

        for (var i = succeeded.Count - 1; i >= 0; i--)
        {
            var change = succeeded[i];
            switch (change.Action)
            {
                case ChangeAction.CreateGpoFolder or ChangeAction.WriteGpoFile or ChangeAction.DeleteGpoFile or ChangeAction.SyncGpoFolderSecurity
                    or ChangeAction.SetGpoFolderSecurity or ChangeAction.SetGpoVersion or ChangeAction.DeleteGpoFolder:
                    InvertSysvol(change, succeeded, steps, issues);
                    break;
                case ChangeAction.Modify:
                    var attributes = new List<AttributeChange>();
                    foreach (var attribute in change.Attributes)
                    {
                        if (Invert(attribute, passwordWasReset, issues) is { } inverse)
                        {
                            attributes.Add(inverse);
                        }
                    }

                    if (attributes.Count > 0)
                    {
                        steps.Add(new PlannedChange { Action = ChangeAction.Modify, Attributes = attributes });
                    }

                    break;
                case ChangeAction.AddMember:
                    steps.Add(new PlannedChange { Action = ChangeAction.RemoveMember, TargetDn = change.TargetDn });
                    break;
                case ChangeAction.RemoveMember:
                    steps.Add(new PlannedChange { Action = ChangeAction.AddMember, TargetDn = change.TargetDn });
                    break;
                case ChangeAction.Rename:
                    if (DistinguishedName.TryParse(dnBefore[i], out var beforeRename) && !beforeRename.IsRoot)
                    {
                        steps.Add(new PlannedChange { Action = ChangeAction.Rename, TargetDn = beforeRename.Leaf.ToString() });
                    }

                    break;
                case ChangeAction.Move:
                    if (DistinguishedName.TryParse(dnBefore[i], out var beforeMove) && beforeMove.Parent is { } parent)
                    {
                        steps.Add(new PlannedChange { Action = ChangeAction.Move, TargetDn = parent.ToString() });
                    }

                    break;
                case ChangeAction.ResetPassword:
                    issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.NotReversible,
                        "The previous password cannot be restored."));
                    break;
                case ChangeAction.Delete:
                    issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.NotReversible,
                        "A deleted object cannot be restored by a rollback; use the AD Recycle Bin."));
                    break;
                case ChangeAction.Restore:
                    issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.NotReversible,
                        "A restored object is not deleted again by a rollback; delete it explicitly if it must go."));
                    break;
                case ChangeAction.ModifySecurity when change.Acl is { IsEmpty: false } acl:
                    // The inverse of what changed the DACL at execution (Executed).
                    steps.Add(new PlannedChange { Action = ChangeAction.ModifySecurity, Acl = acl.Inverse() });
                    break;
            }
        }

        return Build(objectResult, steps, issues);
    }

    /// <summary>
    /// The <c>{GUID}</c> names of the GPOs and WMI filters the batch deleted (their Delete step succeeded): a rollback must
    /// not put back links (<c>gPLink</c>) or filter links (<c>gPCWQLFilter</c>) to them, which would point to nothing.
    /// </summary>
    private static HashSet<string> DeletedReferents(BatchResult result)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var objectResult in result.Objects)
        {
            if (objectResult.Plan.Kind is ObjectKind.GroupPolicyContainer or ObjectKind.WmiFilter
                && objectResult.Changes.Any(c => c.Succeeded && c.Change.Action == ChangeAction.Delete)
                && DistinguishedName.TryParse(objectResult.Plan.DistinguishedName, out var dn) && !dn.IsRoot)
            {
                names.Add(dn.Leaf.Value);
            }
        }

        return names;
    }

    /// <summary>The rollback without the inverse values that name a deleted GPO or WMI filter (a not-reversible warning each).</summary>
    private static ObjectPlan WithoutReferencesTo(ObjectPlan rollback, HashSet<string> deleted)
    {
        if (deleted.Count == 0)
        {
            return rollback;
        }

        var issues = new List<ValidationIssue>(rollback.Issues);
        var steps = new List<PlannedChange>();
        foreach (var step in rollback.Changes)
        {
            if (step.Action != ChangeAction.Modify)
            {
                steps.Add(step);
                continue;
            }

            var kept = new List<AttributeChange>();
            foreach (var attribute in step.Attributes)
            {
                if ((string.Equals(attribute.Attribute, Attr.GpLink, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(attribute.Attribute, GroupPolicy.GroupPolicyAttributes.WqlFilter, StringComparison.OrdinalIgnoreCase))
                    && attribute.NewValues.Any(v => deleted.Any(d => v.Contains(d, StringComparison.OrdinalIgnoreCase))))
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.NotReversible,
                        "The value names a GPO or WMI filter the batch deleted; it is not put back.", attribute.Attribute));
                    continue;
                }

                kept.Add(attribute);
            }

            if (kept.Count > 0)
            {
                steps.Add(step with { Attributes = kept });
            }
        }

        return rollback with { Changes = steps, Issues = issues };
    }

    private static ObjectPlan Build(ObjectResult objectResult, List<PlannedChange> steps, List<ValidationIssue> issues) => new()
    {
        DistinguishedName = objectResult.FinalDn,
        DisplayName = objectResult.Plan.DisplayName,
        Kind = objectResult.Plan.Kind,
        ObjectClasses = objectResult.Plan.ObjectClasses,
        SourceRow = objectResult.Plan.SourceRow,
        Changes = steps,
        Issues = issues,
    };

    private static string DnAfter(string dn, PlannedChange change)
    {
        if (!DistinguishedName.TryParse(dn, out var parsed) || parsed.IsRoot || string.IsNullOrEmpty(change.TargetDn))
        {
            return dn;
        }

        switch (change.Action)
        {
            case ChangeAction.Rename when DistinguishedName.TryParse(change.TargetDn, out var rdn) && !rdn.IsRoot && parsed.Parent is { } parent:
                return parent.Child(rdn.Leaf.Type, rdn.Leaf.Value).ToString();
            case ChangeAction.Move when DistinguishedName.TryParse(change.TargetDn, out var target):
                return parsed.WithParent(target).ToString();
            case ChangeAction.Restore when DistinguishedName.TryParse(change.TargetDn, out var restored) && !restored.IsRoot:
                return restored.ToString();
            default:
                return dn;
        }
    }

    private static AttributeChange? Invert(AttributeChange change, bool passwordWasReset, List<ValidationIssue> issues)
    {
        var written = change.ResultValues;
        if (string.Equals(change.Attribute, Attr.LockoutTime, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.NotReversible,
                "An unlocked account cannot be locked again.", change.Attribute));
            return null;
        }

        if (string.Equals(change.Attribute, Attr.PwdLastSet, StringComparison.OrdinalIgnoreCase))
        {
            if (passwordWasReset)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.NotReversible,
                    "The password reset cannot be undone, so the password-change setting of the reset is kept.", change.Attribute));
                return null;
            }

            // pwdLastSet only accepts 0 ("must change") and -1 ("now").
            if (change.OldValues is ["0"])
            {
                return new AttributeChange(change.Attribute, AttributeChangeKind.Replace, written, ["0"]);
            }

            if (written is ["0"])
            {
                issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.NotReversible,
                    "The original password age cannot be restored; the password will count as changed now.", change.Attribute));
                return new AttributeChange(change.Attribute, AttributeChangeKind.Replace, written, ["-1"]);
            }

            return null;
        }

        return change.Kind switch
        {
            AttributeChangeKind.AddValues => new AttributeChange(change.Attribute, AttributeChangeKind.RemoveValues, written, change.NewValues),
            AttributeChangeKind.RemoveValues => new AttributeChange(change.Attribute, AttributeChangeKind.AddValues, written, change.NewValues),
            _ when change.OldValues.Count == 0 => new AttributeChange(change.Attribute, AttributeChangeKind.Clear, written, []),
            _ => new AttributeChange(change.Attribute, AttributeChangeKind.Replace, written, change.OldValues),
        };
    }
}
