using AdMng.Core.Batch.Planning;

namespace AdMng.Core.Batch.Creation;

/// <summary>The accumulated results of every execution of one wizard window (docs/dev/phase3-bulk-create.md 2.5, 2.7; owner B2).</summary>
public static class BulkCreateResults
{
    /// <summary>
    /// Merges <paramref name="next"/> into <paramref name="accumulated"/>: objects keyed by <see cref="ObjectPlan.SourceRow"/>,
    /// the latest <see cref="ObjectResult"/> per row with the <see cref="ChangeResult"/>s of all executions concatenated;
    /// status <see cref="ObjectResultStatus.Succeeded"/> when the latest execution succeeded, else
    /// <see cref="ObjectResultStatus.PartiallySucceeded"/> when any step of any execution succeeded, else the latest status;
    /// plan = the first plan's header. A null <paramref name="accumulated"/> returns <paramref name="next"/>.
    /// </summary>
    /// <remarks>
    /// Objects without a source row are keyed by their planned DN. The merged objects keep the order of their first
    /// execution (new ones are appended); <see cref="ObjectResult.StartedAt"/> is the first execution's,
    /// <see cref="ObjectResult.FinishedAt"/> and <see cref="ObjectResult.FinalDn"/> the latest's. The merged plan holds the
    /// latest plan of every object, so <see cref="ChangePlan.ContainsPasswords"/> and the export see every password.
    /// </remarks>
    public static BatchResult Merge(BatchResult? accumulated, BatchResult next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (accumulated is null)
        {
            return next;
        }

        var merged = new List<ObjectResult>(accumulated.Objects.Count + next.Objects.Count);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in accumulated.Objects.Concat(next.Objects))
        {
            var key = Key(result.Plan);
            if (!index.TryGetValue(key, out var position))
            {
                index[key] = merged.Count;
                merged.Add(result);
                continue;
            }

            var earlier = merged[position];
            var changes = earlier.Changes.Concat(result.Changes).ToList();
            var status = result.Status == ObjectResultStatus.Succeeded
                ? ObjectResultStatus.Succeeded
                : changes.Any(c => c.Succeeded) ? ObjectResultStatus.PartiallySucceeded : result.Status;
            merged[position] = result with
            {
                Status = status,
                Changes = changes,
                StartedAt = earlier.StartedAt < result.StartedAt ? earlier.StartedAt : result.StartedAt,
            };
        }

        var plan = accumulated.Plan with { Objects = [.. merged.Select(o => o.Plan)] };
        return new BatchResult(
            plan,
            merged,
            accumulated.Server,
            next.Operator,
            accumulated.StartedAt < next.StartedAt ? accumulated.StartedAt : next.StartedAt,
            next.FinishedAt > accumulated.FinishedAt ? next.FinishedAt : accumulated.FinishedAt,
            next.WasCancelled);
    }

    private static string Key(ObjectPlan plan) =>
        plan.SourceRow is { } row ? "#" + row.ToString(System.Globalization.CultureInfo.InvariantCulture) : PlanningContext.Key(plan.DistinguishedName);
}
