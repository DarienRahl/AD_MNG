using AdMng.Core.ActiveDirectory;
using AdMng.Core.Csv;
using AdMng.Core.Schema;

namespace AdMng.Core.Batch;

/// <summary>
/// Writes a preview (<see cref="ChangePlan"/>) or an execution result (<see cref="BatchResult"/>) as CSV:
/// one row per attribute change or step, one row for objects without steps.
/// </summary>
/// <remarks>
/// Columns: Status, Name, DN, Action, Attribute, Old value, New value, Message - plus Password when
/// <c>includePasswords</c> is true (the caller must have warned the operator; the default never writes passwords).
/// The old and new values of secret attributes (<see cref="SecretAttributes"/>, with the schema's confidential attributes
/// when a schema is passed) are written as <see cref="SecretAttributes.Mask"/> unless <c>includePasswords</c> is true.
/// A result contains the password of a reset only when the reset succeeded (a failed or not executed reset set nothing).
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
    /// <param name="writer">Target.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="includePasswords">Write passwords and the values of secret attributes in plain text.</param>
    /// <param name="delimiter">CSV delimiter.</param>
    /// <param name="schema">Schema of the session: its confidential attributes are masked as well.</param>
    public void Write(TextWriter writer, ChangePlan plan, bool includePasswords = false, char delimiter = ';', ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(plan);
        _csv.Write(writer, Headers(includePasswords), PlanRows(plan, new Secrets(includePasswords, schema)), delimiter, protectFormulas: true);
    }

    /// <summary>Writes the outcome of an executed batch.</summary>
    /// <param name="writer">Target.</param>
    /// <param name="result">The result.</param>
    /// <param name="includePasswords">
    /// Write the passwords of the resets that succeeded and the values of secret attributes in plain text.
    /// </param>
    /// <param name="delimiter">CSV delimiter.</param>
    /// <param name="schema">Schema of the session: its confidential attributes are masked as well.</param>
    public void Write(TextWriter writer, BatchResult result, bool includePasswords = false, char delimiter = ';', ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);
        _csv.Write(writer, Headers(includePasswords), ResultRows(result, new Secrets(includePasswords, schema)), delimiter, protectFormulas: true);
    }

    /// <summary>
    /// True when exporting the plan with <c>includePasswords</c> would write something the default export hides: a password
    /// or a value of a secret attribute. The caller asks the operator before including them.
    /// </summary>
    public static bool ContainsSecrets(ChangePlan plan, ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.ContainsPasswords || plan.Objects.SelectMany(o => o.Changes).Any(c => HasSecretValues(c, schema));
    }

    /// <summary>
    /// True when exporting the result with <c>includePasswords</c> would write something the default export hides: the
    /// password of a reset that succeeded or a value of a secret attribute.
    /// </summary>
    public static bool ContainsSecrets(BatchResult result, ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Objects.Any(o => o.Plan.Kind != Model.ObjectKind.Computer
                && o.Changes.Any(c => c.Succeeded && c.Change.Action == ChangeAction.ResetPassword && c.Change.Password is not null))
            || result.Plan.Objects.SelectMany(o => o.Changes).Any(c => HasSecretValues(c, schema));
    }

    private static bool HasSecretValues(PlannedChange change, ISchemaCatalog? schema) =>
        change.Action is ChangeAction.Modify or ChangeAction.Create
        && change.Attributes.Any(a => (a.OldValues.Count > 0 || a.NewValues.Count > 0) && SecretAttributes.IsSecret(a.Attribute, schema));

    private static string[] Headers(bool includePasswords) => includePasswords ? [.. Columns, PasswordColumn] : Columns;

    private static IEnumerable<IReadOnlyList<string>> PlanRows(ChangePlan plan, Secrets secrets)
    {
        var includePasswords = secrets.Include;
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
                foreach (var row in StepRows(change, status, objectPlan, objectPlan.DistinguishedName, objectMessage, null, secrets, passwordSet: true))
                {
                    yield return row;
                }
            }
        }
    }

    private static IEnumerable<IReadOnlyList<string>> ResultRows(BatchResult result, Secrets secrets)
    {
        var includePasswords = secrets.Include;
        foreach (var objectResult in result.Objects)
        {
            var objectPlan = objectResult.Plan;
            var objectMessage = Describe(objectPlan.Issues);
            if (objectResult.Changes.Count == 0)
            {
                // Nothing ran (skipped / cancelled): the reason may be an issue of a step, e.g. a value that is too long.
                var reasons = Describe(objectPlan.Issues.Concat(objectPlan.Changes.SelectMany(c => c.Issues)));
                yield return Row(includePasswords, objectResult.Status.ToString(), objectPlan.DisplayName, objectResult.FinalDn,
                    string.Empty, null, null, null, reasons, null);
                continue;
            }

            foreach (var change in objectPlan.Changes)
            {
                var executed = objectResult.Changes.FirstOrDefault(r => ReferenceEquals(r.Change, change));
                var status = executed is null ? StatusNotExecuted : executed.Succeeded ? StatusSucceeded : StatusFailed;
                // Only a reset that succeeded set its password; the password of a failed or not executed reset is useless and
                // must not end up in a file.
                var passwordSet = executed is { Succeeded: true };
                foreach (var row in StepRows(change, status, objectPlan, objectResult.FinalDn, objectMessage, executed?.ErrorMessage, secrets, passwordSet))
                {
                    yield return row;
                }
            }
        }
    }

    private static IEnumerable<IReadOnlyList<string>> StepRows(
        PlannedChange change,
        string status,
        ObjectPlan objectPlan,
        string dn,
        string objectMessage,
        string? error,
        Secrets secrets,
        bool passwordSet)
    {
        var includePasswords = secrets.Include;
        var name = objectPlan.DisplayName;
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
                        secrets.Values(attribute.Attribute, attribute.OldValues), secrets.Values(attribute.Attribute, attribute.ResultValues),
                        Join(objectMessage, issues, error), null);
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
            case ChangeAction.Restore:
                // Old value: where the object was (CN=Deleted Objects); new value: the DN it is restored to.
                yield return Row(includePasswords, status, name, dn, action, null, objectPlan.DistinguishedName, change.TargetDn, message, null);
                break;
            case ChangeAction.ResetPassword:
                // K1: the machine password of a reset computer account is never written to a report.
                yield return Row(includePasswords, status, name, dn, action, null, null, null, message,
                    passwordSet && objectPlan.Kind != Model.ObjectKind.Computer ? change.Password : null);
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

    /// <summary>Whether secrets are written in plain text, and which attributes are secret.</summary>
    private readonly record struct Secrets(bool Include, ISchemaCatalog? Schema)
    {
        /// <summary>The joined values, or the mask for (non-empty) values of a secret attribute that are not included.</summary>
        public string Values(string attribute, IReadOnlyList<string> values) =>
            values.Count == 0 ? string.Empty
            : !Include && SecretAttributes.IsSecret(attribute, Schema) ? SecretAttributes.Mask
            : JoinValues(values);
    }

    private static string Describe(IEnumerable<ValidationIssue> issues) =>
        string.Join(" ", issues.Select(i => $"[{i.Severity}: {i.Code}] {i.Message}"));

    private static string Join(params string?[] parts) => string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));
}
