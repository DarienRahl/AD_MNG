using System.ComponentModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Formatting;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.BatchEditing;

/// <summary>Filter of the preview grid.</summary>
public enum PreviewFilter
{
    All,

    /// <summary>Rows that will change something.</summary>
    Changes,

    Warnings,
    Errors,

    /// <summary>Objects that need no change.</summary>
    NoChange,
}

/// <summary>Severity shown in a preview row (icon).</summary>
public enum RowSeverity
{
    None,
    Info,
    Warning,
    Error,
}

/// <summary>Filter of the results grid.</summary>
public enum ResultFilter
{
    All,
    Succeeded,

    /// <summary>Failed, partially succeeded or cancelled objects.</summary>
    Failed,

    Skipped,
}

/// <summary>One object of the preview: include check box shared by all its rows.</summary>
public sealed partial class PreviewObjectViewModel : ObservableObject
{
    internal PreviewObjectViewModel(ObjectPlan plan)
    {
        Plan = plan;
        IsIncluded = CanToggle;
    }

    public ObjectPlan Plan { get; }

    public string Name => Plan.DisplayName;

    public string Dn => Plan.DistinguishedName;

    /// <summary>CSV row (1-based, header excluded) for CSV imports.</summary>
    public int? SourceRow => Plan.SourceRow;

    public bool HasErrors => Plan.HasErrors;

    public bool HasWarnings => Plan.HasWarnings;

    public bool IsNoOp => !Plan.HasErrors && Plan.IsNoOp;

    /// <summary>Only objects that would change something can be excluded (others are skipped anyway).</summary>
    public bool CanToggle => !Plan.HasErrors && !Plan.IsNoOp;

    /// <summary>Execute this object (excluded objects are removed from the plan before execution).</summary>
    [ObservableProperty]
    public partial bool IsIncluded { get; set; }

    public IReadOnlyList<PreviewRowViewModel> Rows { get; internal set; } = [];
}

/// <summary>One flattened preview row: object, action, attribute, before → after, finding.</summary>
public sealed class PreviewRowViewModel : INotifyPropertyChanged
{
    internal PreviewRowViewModel(PreviewObjectViewModel owner, string action, string attribute, string before, string after, RowSeverity severity, string issueText, bool isFirst)
    {
        Object = owner;
        Action = action;
        Attribute = attribute;
        Before = before;
        After = after;
        Severity = severity;
        IssueText = issueText;
        IsFirstOfObject = isFirst;
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewObjectViewModel.IsIncluded))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIncluded)));
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PreviewObjectViewModel Object { get; }

    public string Name => Object.Name;

    public string Dn => Object.Dn;

    public int? SourceRow => Object.SourceRow;

    /// <summary>Localized action ("Modify", "Add to group", "Move"...) or "No change".</summary>
    public string Action { get; }

    /// <summary>Friendly attribute name (or the group / target for membership, rename and move).</summary>
    public string Attribute { get; }

    public string Before { get; }

    public string After { get; }

    public RowSeverity Severity { get; }

    /// <summary>Localized findings of the row.</summary>
    public string IssueText { get; }

    /// <summary>First row of its object (the view shows the name and the include check box only there).</summary>
    public bool IsFirstOfObject { get; }

    public bool CanToggle => Object.CanToggle;

    public bool IsIncluded
    {
        get => Object.IsIncluded;
        set => Object.IsIncluded = value;
    }

    /// <summary>True when the row changes something.</summary>
    public bool IsChange { get; init; }
}

/// <summary>One object of the execution result.</summary>
public sealed class ResultRowViewModel
{
    internal ResultRowViewModel(ObjectResult result, string statusText, string message)
    {
        Result = result;
        StatusText = statusText;
        Message = message;
    }

    public ObjectResult Result { get; }

    public string Name => Result.Plan.DisplayName;

    /// <summary>DN after the batch (after a rename/move: the new DN).</summary>
    public string Dn => Result.FinalDn;

    public int? SourceRow => Result.Plan.SourceRow;

    public ObjectResultStatus Status => Result.Status;

    public string StatusText { get; }

    /// <summary>Localized error or the reason why the object was skipped.</summary>
    public string Message { get; }

    public bool IsFailure => Status is ObjectResultStatus.Failed or ObjectResultStatus.PartiallySucceeded or ObjectResultStatus.Cancelled;

    /// <summary>A new password was set (the export can include it after a confirmation).</summary>
    public bool HasPassword => Result.Changes.Any(c => c.Succeeded && c.Change.Password is not null);
}

/// <summary>Turns plans and results into localized grid rows.</summary>
internal sealed class PreviewBuilder(ILocalizer localizer, ErrorTranslator errors, IAttributeFormatter formatter, ISchemaCatalog schema)
{
    private const string HiddenPassword = "••••••••";

    public List<PreviewObjectViewModel> Build(ChangePlan plan)
    {
        var objects = new List<PreviewObjectViewModel>(plan.Objects.Count);
        foreach (var objectPlan in plan.Objects)
        {
            var owner = new PreviewObjectViewModel(objectPlan);
            owner.Rows = Rows(owner);
            objects.Add(owner);
        }

        return objects;
    }

    public ResultRowViewModel BuildResult(ObjectResult result)
    {
        var status = localizer[DisplayNames.ObjectResultStatusKey(result.Status)];
        string message;
        var failed = result.Changes.FirstOrDefault(c => !c.Succeeded);
        if (failed is not null)
        {
            message = errors.TranslateFailure(failed.ErrorKind, failed.ErrorMessage);
        }
        else if (result.Status == ObjectResultStatus.Skipped)
        {
            var reasons = result.Plan.Issues.Concat(result.Plan.Changes.SelectMany(c => c.Issues))
                .Where(i => i.Severity != IssueSeverity.Info || result.Plan.IsNoOp)
                .Select(i => errors.TranslateIssue(i, schema))
                .Distinct(StringComparer.Ordinal);
            message = string.Join(" ", reasons);
        }
        else
        {
            message = string.Empty;
        }

        return new ResultRowViewModel(result, status, message);
    }

    private List<PreviewRowViewModel> Rows(PreviewObjectViewModel owner)
    {
        var plan = owner.Plan;
        var rows = new List<PreviewRowViewModel>();
        var objectIssues = plan.Issues.ToList();

        void Add(string action, string attribute, string before, string after, IEnumerable<ValidationIssue> issues, bool isChange)
        {
            // Object level findings are shown on the object's first row.
            IReadOnlyList<ValidationIssue> all = [.. objectIssues, .. issues];
            objectIssues.Clear();
            var severity = all.Count == 0 ? RowSeverity.None : all.Max(i => i.Severity) switch
            {
                IssueSeverity.Error => RowSeverity.Error,
                IssueSeverity.Warning => RowSeverity.Warning,
                _ => RowSeverity.Info,
            };
            var text = string.Join(" ", all.Select(i => errors.TranslateIssue(i, schema)).Distinct(StringComparer.Ordinal));
            rows.Add(new PreviewRowViewModel(owner, action, attribute, before, after, severity, text, rows.Count == 0) { IsChange = isChange });
        }

        if (plan.Changes.Count == 0)
        {
            Add(plan.HasErrors ? localizer[L.Batch.NotExecuted] : localizer[L.Batch.NoChange], string.Empty, string.Empty, string.Empty, [], isChange: false);
            return rows;
        }

        foreach (var change in plan.Changes)
        {
            var action = localizer[DisplayNames.ChangeActionKey(change.Action)];
            switch (change.Action)
            {
                case ChangeAction.Modify or ChangeAction.Create:
                    if (change.Attributes.Count == 0)
                    {
                        Add(action, string.Empty, string.Empty, string.Empty, change.Issues, isChange: false);
                        break;
                    }

                    foreach (var attribute in change.Attributes)
                    {
                        var issues = change.Issues.Where(i => i.Attribute is null || string.Equals(i.Attribute, attribute.Attribute, StringComparison.OrdinalIgnoreCase));
                        Add(action, localizer.AttributeName(attribute.Attribute, schema),
                            Format(attribute.Attribute, attribute.OldValues), Format(attribute.Attribute, attribute.ResultValues), issues, isChange: true);
                    }

                    // Findings about attributes that produced no change (e.g. an unknown attribute).
                    var orphaned = change.Issues.Where(i => i.Attribute is not null
                        && !change.Attributes.Any(a => string.Equals(a.Attribute, i.Attribute, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (orphaned.Count > 0)
                    {
                        Add(action, string.Empty, string.Empty, string.Empty, orphaned, isChange: false);
                    }

                    break;
                case ChangeAction.AddMember:
                    Add(action, localizer.AttributeName(Attr.MemberOf, schema), string.Empty, NameOf(change.TargetDn), change.Issues, isChange: true);
                    break;
                case ChangeAction.RemoveMember:
                    Add(action, localizer.AttributeName(Attr.MemberOf, schema), NameOf(change.TargetDn), string.Empty, change.Issues, isChange: true);
                    break;
                case ChangeAction.Rename:
                    Add(action, localizer.AttributeName(Attr.Name, schema), NameOf(plan.DistinguishedName), RdnValue(change.TargetDn), change.Issues, isChange: true);
                    break;
                case ChangeAction.Move:
                    Add(action, localizer[L.Batch.Container], ParentPath(plan.DistinguishedName), PathOf(change.TargetDn), change.Issues, isChange: true);
                    break;
                case ChangeAction.ResetPassword:
                    Add(action, localizer[L.Batch.Password], string.Empty, change.Password is null ? string.Empty : HiddenPassword, change.Issues, isChange: true);
                    break;
                default:
                    Add(action, string.Empty, string.Empty, string.Empty, change.Issues, isChange: true);
                    break;
            }
        }

        return rows;
    }

    private string Format(string attribute, IReadOnlyList<string> values) =>
        string.Join("; ", values.Select(v => formatter.FormatValue(attribute, v, localizer.Culture)));

    private static string NameOf(string? dn) =>
        dn is not null && DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot ? parsed.Name : dn ?? string.Empty;

    private static string RdnValue(string? rdn)
    {
        if (string.IsNullOrEmpty(rdn))
        {
            return string.Empty;
        }

        return DistinguishedName.TryParse(rdn, out var parsed) && !parsed.IsRoot ? parsed.Name : rdn;
    }

    private static string PathOf(string? dn) => dn is null ? string.Empty : AddressResolver.ToDisplayPath(dn);

    private static string ParentPath(string dn) =>
        DistinguishedName.TryParse(dn, out var parsed) && parsed.Parent is { IsRoot: false } parent ? AddressResolver.ToDisplayPath(parent.ToString()) : string.Empty;
}
