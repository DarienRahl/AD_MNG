using AdMng.Core.Directory;
using AdMng.Core.Model;

namespace AdMng.Core.Batch.Creation;

// BuildRetryPlan (docs/dev/phase3-bulk-create.md 2.5): pure – the failed, partially succeeded and cancelled objects of all
// executions, each without the steps that succeeded in any of them; a password that was never set gets a new one.
public sealed partial class BulkCreatePlanner
{
    public ChangePlan BuildRetryPlan(BulkCreateComputation computation, BatchResult accumulated, string? newSharedPassword, string title)
    {
        ArgumentNullException.ThrowIfNull(computation);
        ArgumentNullException.ThrowIfNull(accumulated);
        ArgumentNullException.ThrowIfNull(title);
        var rows = computation.Rows.ToDictionary(r => r.Number);
        var objects = new List<ObjectPlan>();
        foreach (var result in accumulated.Objects)
        {
            if (result.Status is not (ObjectResultStatus.Failed or ObjectResultStatus.PartiallySucceeded or ObjectResultStatus.Cancelled))
            {
                continue;
            }

            var row = result.Plan.SourceRow is { } number && rows.TryGetValue(number, out var found) ? found : null;
            objects.Add(RetryObject(computation.Input, result, row, newSharedPassword));
        }

        return new ChangePlan
        {
            Objects = objects,
            Title = title,
            CreatedAt = _time.GetUtcNow(),
            Server = computation.Server,
            Sequential = false,
        };
    }

    private ObjectPlan RetryObject(BulkCreateInput input, ObjectResult result, BulkCreateRow? row, string? newSharedPassword)
    {
        var plan = result.Plan;
        var done = result.Changes.Where(c => c.Succeeded).SelectMany(c => StepKeys(c.Change)).ToHashSet(StringComparer.Ordinal);
        var created = done.Contains(nameof(ChangeAction.Create));
        var createFailure = result.Changes.LastOrDefault(c => c.Change.Action == ChangeAction.Create && !c.Succeeded);
        var issues = plan.Issues.ToList();
        if (!created && createFailure?.ErrorKind is DirectoryErrorKind.AlreadyExists or DirectoryErrorKind.ConstraintViolation)
        {
            // The value is taken since the check: the Create would fail again (2.5) – shown, not executed.
            issues.Add(Error(BulkCreateIssueCodes.ValueTakenSinceCheck, "A value was taken since the check; go back to the check step."));
        }

        var changes = new List<PlannedChange>();
        foreach (var change in plan.Changes)
        {
            if (change.Action == ChangeAction.Modify)
            {
                var remaining = change.Attributes.Where(a => !done.Contains(AttributeStepKey(a.Attribute))).ToList();
                if (remaining.Count > 0)
                {
                    changes.Add(remaining.Count == change.Attributes.Count ? change : change with { Attributes = remaining });
                }

                continue;
            }

            if (StepKeys(change).All(done.Contains))
            {
                continue;
            }

            changes.Add(change.Action == ChangeAction.ResetPassword ? RetryPassword(input, plan, result, change, row, newSharedPassword) : change);
        }

        return plan with
        {
            DistinguishedName = created ? result.FinalDn : plan.DistinguishedName,
            Changes = changes,
            Issues = issues,
            PowerShell = created ? null : plan.PowerShell,
        };
    }

    /// <summary>
    /// A password step that never succeeded (2.5): generated passwords and machine passwords are generated again (the failed
    /// one was never applied nor handed out); a shared password is replaced by <paramref name="newSharedPassword"/> when the
    /// last attempt failed with the password policy, else it is the step's previous password.
    /// </summary>
    private PlannedChange RetryPassword(BulkCreateInput input, ObjectPlan plan, ObjectResult result, PlannedChange change, BulkCreateRow? row, string? newSharedPassword)
    {
        var tokens = row is null ? [] : ForbiddenTokens(row);
        if (plan.Kind == ObjectKind.Computer)
        {
            return change with { Password = _passwords.Generate(ResetComputerAccountOperation.GeneratedLength, tokens) };
        }

        if (input.Options.PasswordMode == InitialPasswordMode.Generate)
        {
            return change with { Password = _passwords.Generate(input.Options.GeneratedLength, tokens) };
        }

        var last = result.Changes.LastOrDefault(c => c.Change.Action == ChangeAction.ResetPassword && !c.Succeeded);
        if (last?.ErrorKind == DirectoryErrorKind.PasswordPolicy && !string.IsNullOrEmpty(newSharedPassword))
        {
            var issues = row is not null && ContainsName(newSharedPassword, row)
                ? [Warning(BulkCreateIssueCodes.PasswordContainsName, "The password contains the account name or a part of the display name.")]
                : Array.Empty<ValidationIssue>();
            return change with { Password = newSharedPassword, Issues = issues };
        }

        return change;
    }

    /// <summary>What a step did (the batch editor's <c>StepKeys</c>): one key per attribute of a modify, the group of a membership, the action otherwise.</summary>
    private static IEnumerable<string> StepKeys(PlannedChange change) => change.Action switch
    {
        ChangeAction.Modify => change.Attributes.Select(a => AttributeStepKey(a.Attribute)),
        ChangeAction.AddMember or ChangeAction.RemoveMember => [$"{change.Action}:{Planning.PlanningContext.Key(change.TargetDn ?? string.Empty).ToUpperInvariant()}"],
        _ => [change.Action.ToString()],
    };

    private static string AttributeStepKey(string attribute) => "Modify:" + attribute.ToUpperInvariant();
}
