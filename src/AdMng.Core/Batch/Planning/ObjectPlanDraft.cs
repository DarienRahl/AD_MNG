using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Batch.Planning;

/// <summary>Mutable builder of one non-modify step (password reset, membership, rename, move, delete).</summary>
internal sealed class StepDraft
{
    public StepDraft(ChangeAction action)
    {
        Action = action;
    }

    public ChangeAction Action { get; }

    public string? TargetDn { get; init; }

    /// <summary>Sensitive - only copied into the <see cref="PlannedChange"/>.</summary>
    public string? Password { get; init; }

    public bool DeleteSubtree { get; init; }

    public List<ValidationIssue> Issues { get; } = [];

    public PlannedChange Build() => new()
    {
        Action = Action,
        TargetDn = TargetDn,
        Password = Password,
        DeleteSubtree = DeleteSubtree,
        Issues = [.. Issues],
    };

    /// <summary>Never print the password.</summary>
    public override string ToString() => $"{Action} target={TargetDn}";
}

/// <summary>Mutable builder of one <see cref="ObjectPlan"/> shared by the change planner and the CSV import planner.</summary>
internal sealed class ObjectPlanDraft
{
    private readonly Dictionary<string, AttributeEdit> _edits = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AttributeEdit> _editOrder = [];

    public ObjectPlanDraft(string distinguishedName, string displayName, DirectoryObject? current, int? sourceRow = null)
    {
        DistinguishedName = distinguishedName;
        DisplayName = displayName;
        Current = current;
        SourceRow = sourceRow;
    }

    /// <summary>DN as returned by the server (or as requested when the object was not found).</summary>
    public string DistinguishedName { get; }

    public string DisplayName { get; }

    /// <summary>The object as read while planning; null when not found / not resolved.</summary>
    /// <remarks>Replaced only before the first edit, when ranged values were completed (same DN).</remarks>
    public DirectoryObject? Current { get; set; }

    public int? SourceRow { get; }

    public ObjectKind Kind => Current?.Kind ?? ObjectKind.Unknown;

    public IReadOnlyList<string> ObjectClasses => Current?.GetStrings(Attr.ObjectClass) ?? [];

    /// <summary>Object level findings.</summary>
    public List<ValidationIssue> Issues { get; } = [];

    /// <summary>Findings about attribute changes; attached to the Modify step (or the object when there is none).</summary>
    public List<ValidationIssue> AttributeIssues { get; } = [];

    public StepDraft? PasswordReset { get; set; }

    public List<StepDraft> Memberships { get; } = [];

    public StepDraft? Rename { get; set; }

    public StepDraft? Move { get; set; }

    public StepDraft? Delete { get; set; }

    /// <summary>Attribute changes computed by <see cref="FinalizeAttributes"/>.</summary>
    public IReadOnlyList<AttributeChange> AttributeChanges { get; private set; } = [];

    /// <summary>DN after rename and/or move; null when the object keeps its DN.</summary>
    public string? FinalDn { get; set; }

    /// <summary>The step that determines <see cref="FinalDn"/> (the move when present, otherwise the rename).</summary>
    public StepDraft? NamingStep => Move ?? Rename;

    public bool HasErrors => Issues.Any(IsError) || AttributeIssues.Any(IsError)
        || new[] { PasswordReset, Rename, Move, Delete }.Concat(Memberships).Any(s => s is not null && s.Issues.Any(IsError));

    /// <summary>Display name for the preview: displayName, otherwise the RDN value.</summary>
    public static string DisplayNameOf(DirectoryObject obj) =>
        obj.GetString(Attr.DisplayName) is { Length: > 0 } displayName ? displayName : obj.Name;

    /// <summary>Returns the working state of an attribute, creating it from the current values.</summary>
    public AttributeEdit GetEdit(string attribute, ISchemaCatalog schema)
    {
        if (_edits.TryGetValue(attribute, out var edit))
        {
            return edit;
        }

        var definition = schema.FindAttribute(attribute);
        var name = definition?.LdapName ?? attribute;
        edit = new AttributeEdit(name, Current is null ? [] : PipelineValues.Read(Current, name), definition);
        _edits[attribute] = edit;
        _edits[name] = edit;
        _editOrder.Add(edit);
        return edit;
    }

    public AttributeEdit? FindEdit(string attribute) => _edits.GetValueOrDefault(attribute);

    /// <summary>Values of an attribute after the operations applied so far (what templates see).</summary>
    public IReadOnlyList<string> CurrentValues(string attribute) =>
        FindEdit(attribute)?.Current ?? (Current is null ? [] : PipelineValues.Read(Current, attribute));

    public void FinalizeAttributes() =>
        AttributeChanges = _editOrder.Select(e => e.ToChange()).OfType<AttributeChange>().ToArray();

    public ObjectPlan Build()
    {
        var changes = new List<PlannedChange>();
        var objectIssues = new List<ValidationIssue>(Issues);
        if (Delete is not null)
        {
            changes.Add(Delete.Build());
        }
        else
        {
            if (PasswordReset is not null)
            {
                changes.Add(PasswordReset.Build());
            }

            if (AttributeChanges.Count > 0)
            {
                changes.AddRange(ModifySteps());
            }
            else
            {
                objectIssues.AddRange(AttributeIssues);
            }

            changes.AddRange(Memberships.Select(m => m.Build()));
            if (Rename is not null)
            {
                changes.Add(Rename.Build());
            }

            if (Move is not null)
            {
                changes.Add(Move.Build());
            }
        }

        if (changes.Count == 0 && Current is not null && !objectIssues.Any(IsError))
        {
            objectIssues.Add(new ValidationIssue(IssueSeverity.Info, IssueCodes.NoChange, "Nothing to change: all values are already as requested."));
        }

        return new ObjectPlan
        {
            DistinguishedName = DistinguishedName,
            DisplayName = DisplayName,
            Kind = Kind,
            ObjectClasses = ObjectClasses,
            SourceRow = SourceRow,
            Changes = changes,
            Issues = objectIssues,
        };
    }

    /// <summary>
    /// The Modify steps of the attribute changes. Replace/clear changes and value additions/removals are separate
    /// steps because the executor sends them as separate requests (compare-and-swap vs. the permissive modify
    /// control): when the second request fails after the first was applied, the applied changes are still reported,
    /// audited and rolled back as succeeded. Findings go with the step that writes their attribute (otherwise the first).
    /// </summary>
    private IEnumerable<PlannedChange> ModifySteps()
    {
        var strict = AttributeChanges.Where(c => c.Kind is AttributeChangeKind.Replace or AttributeChangeKind.Clear).ToArray();
        var permissive = AttributeChanges.Where(c => c.Kind is AttributeChangeKind.AddValues or AttributeChangeKind.RemoveValues).ToArray();
        if (strict.Length == 0 || permissive.Length == 0)
        {
            yield return new PlannedChange { Action = ChangeAction.Modify, Attributes = AttributeChanges, Issues = [.. AttributeIssues] };
            yield break;
        }

        bool IsPermissive(ValidationIssue issue) =>
            issue.Attribute is not null && permissive.Any(c => string.Equals(c.Attribute, issue.Attribute, StringComparison.OrdinalIgnoreCase));

        yield return new PlannedChange { Action = ChangeAction.Modify, Attributes = strict, Issues = [.. AttributeIssues.Where(i => !IsPermissive(i))] };
        yield return new PlannedChange { Action = ChangeAction.Modify, Attributes = permissive, Issues = [.. AttributeIssues.Where(IsPermissive)] };
    }

    private static bool IsError(ValidationIssue issue) => issue.Severity == IssueSeverity.Error;
}
