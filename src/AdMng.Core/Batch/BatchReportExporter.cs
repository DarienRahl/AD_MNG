using AdMng.Core.ActiveDirectory;
using AdMng.Core.Csv;

namespace AdMng.Core.Batch;

/// <summary>
/// Writes a preview (<see cref="ChangePlan"/>) or an execution result (<see cref="BatchResult"/>) as CSV:
/// one row per attribute change or step, one row for objects without steps.
/// </summary>
/// <remarks>
/// Columns: Status, Name, DN, Action, Attribute, Old value, New value, Message - plus Password when
/// <c>includePasswords</c> is true (the caller must have warned the operator; the default never writes passwords).
/// Status values are stable English tokens: for a plan <see cref="StatusError"/>, <see cref="StatusNoChange"/>,
/// <see cref="StatusPlanned"/>; for a result the step outcome (<see cref="StatusSucceeded"/>, <see cref="StatusFailed"/>,
/// <see cref="StatusNotExecuted"/>) or the object status (<see cref="ObjectResultStatus"/> name) for objects without steps.
/// Multiple values are joined with "; ". Formula injection protection of <see cref="ICsvCodec"/> is always on.
/// </remarks>
public sealed class BatchReportExporter
{
    public const string StatusPlanned = "Planned";
    public const string StatusError = "Error";
    public const string StatusNoChange = "NoChange";
    public const string StatusSucceeded = "Succeeded";
    public const string StatusFailed = "Failed";
    public const string StatusNotExecuted = "NotExecuted";

    private static readonly string[] Columns = ["Status", "Name", "DN", "Action", "Attribute", "Old value", "New value", "Message"];
    private const string PasswordColumn = "Password";

    private readonly ICsvCodec _csv;

    public BatchReportExporter(ICsvCodec csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        _csv = csv;
    }

    /// <summary>Writes the preview of a plan.</summary>
    public void Write(TextWriter writer, ChangePlan plan, bool includePasswords = false, char delimiter = ';')
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(plan);
        _csv.Write(writer, Headers(includePasswords), PlanRows(plan, includePasswords), delimiter, protectFormulas: true);
    }

    /// <summary>Writes the outcome of an executed batch.</summary>
    public void Write(TextWriter writer, BatchResult result, bool includePasswords = false, char delimiter = ';')
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);
        _csv.Write(writer, Headers(includePasswords), ResultRows(result, includePasswords), delimiter, protectFormulas: true);
    }

    private static string[] Headers(bool includePasswords) => includePasswords ? [.. Columns, PasswordColumn] : Columns;

    private static IEnumerable<IReadOnlyList<string>> PlanRows(ChangePlan plan, bool includePasswords)
    {
        foreach (var objectPlan in plan.Objects)
        {
            var status = objectPlan.HasErrors ? StatusError : objectPlan.IsNoOp ? StatusNoChange : StatusPlanned;
            var objectMessage = Describe(objectPlan.Issues);
            if (objectPlan.Changes.Count == 0)
            {
                yield return Row(includePasswords, status, objectPlan.DisplayName, objectPlan.DistinguishedName, string.Empty, null, null, null, objectMessage, null);
                continue;
            }

            foreach (var change in objectPlan.Changes)
            {
                foreach (var row in StepRows(change, status, objectPlan.DisplayName, objectPlan.DistinguishedName, objectMessage, null, includePasswords))
                {
                    yield return row;
                }
            }
        }
    }

    private static IEnumerable<IReadOnlyList<string>> ResultRows(BatchResult result, bool includePasswords)
    {
        foreach (var objectResult in result.Objects)
        {
            var objectPlan = objectResult.Plan;
            var objectMessage = Describe(objectPlan.Issues);
            if (objectResult.Changes.Count == 0)
            {
                yield return Row(includePasswords, objectResult.Status.ToString(), objectPlan.DisplayName, objectResult.FinalDn,
                    string.Empty, null, null, null, objectMessage, null);
                continue;
            }

            foreach (var change in objectPlan.Changes)
            {
                var executed = objectResult.Changes.FirstOrDefault(r => ReferenceEquals(r.Change, change));
                var status = executed is null ? StatusNotExecuted : executed.Succeeded ? StatusSucceeded : StatusFailed;
                foreach (var row in StepRows(change, status, objectPlan.DisplayName, objectResult.FinalDn, objectMessage, executed?.ErrorMessage, includePasswords))
                {
                    yield return row;
                }
            }
        }
    }

    private static IEnumerable<IReadOnlyList<string>> StepRows(
        PlannedChange change,
        string status,
        string name,
        string dn,
        string objectMessage,
        string? error,
        bool includePasswords)
    {
        var message = Join(objectMessage, Describe(change.Issues), error);
        var action = change.Action.ToString();
        switch (change.Action)
        {
            case ChangeAction.Modify or ChangeAction.Create:
                if (change.Attributes.Count == 0)
                {
                    yield return Row(includePasswords, status, name, dn, action, null, null, null, message, null);
                    yield break;
                }

                foreach (var attribute in change.Attributes)
                {
                    var issues = Describe(change.Issues.Where(i => i.Attribute is null
                        || string.Equals(i.Attribute, attribute.Attribute, StringComparison.OrdinalIgnoreCase)));
                    yield return Row(includePasswords, status, name, dn, action, attribute.Attribute,
                        JoinValues(attribute.OldValues), JoinValues(attribute.ResultValues), Join(objectMessage, issues, error), null);
                }

                break;
            case ChangeAction.AddMember:
                yield return Row(includePasswords, status, name, dn, action, Attr.MemberOf, null, change.TargetDn, message, null);
                break;
            case ChangeAction.RemoveMember:
                yield return Row(includePasswords, status, name, dn, action, Attr.MemberOf, change.TargetDn, null, message, null);
                break;
            case ChangeAction.Rename or ChangeAction.Move:
                yield return Row(includePasswords, status, name, dn, action, null, null, change.TargetDn, message, null);
                break;
            case ChangeAction.ResetPassword:
                yield return Row(includePasswords, status, name, dn, action, null, null, null, message, change.Password);
                break;
            default:
                yield return Row(includePasswords, status, name, dn, action, null, null, null, message, null);
                break;
        }
    }

    private static string[] Row(
        bool includePasswords,
        string status,
        string name,
        string dn,
        string action,
        string? attribute,
        string? oldValue,
        string? newValue,
        string message,
        string? password)
    {
        string[] row = [status, name, dn, action, attribute ?? string.Empty, oldValue ?? string.Empty, newValue ?? string.Empty, message];
        return includePasswords ? [.. row, password ?? string.Empty] : row;
    }

    private static string JoinValues(IReadOnlyList<string> values) => string.Join("; ", values);

    private static string Describe(IEnumerable<ValidationIssue> issues) =>
        string.Join(" ", issues.Select(i => $"[{i.Severity}: {i.Code}] {i.Message}"));

    private static string Join(params string?[] parts) => string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));
}
