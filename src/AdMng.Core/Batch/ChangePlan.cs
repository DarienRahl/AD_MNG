using AdMng.Core.Model;

namespace AdMng.Core.Batch;

/// <summary>Kind of a single planned directory change.</summary>
public enum ChangeAction
{
    /// <summary>
    /// Modify attributes of the object (one LDAP modify request for all <see cref="PlannedChange.Attributes"/>). The
    /// planners put replace/clear changes and value additions/removals into separate steps, because the executor sends
    /// them with different semantics (compare-and-swap vs. permissive) and each must report its own outcome.
    /// </summary>
    Modify,

    /// <summary>Administrative password reset (unicodePwd). <see cref="PlannedChange.Password"/> holds the new password.</summary>
    ResetPassword,

    /// <summary>Add the object to the group in <see cref="PlannedChange.TargetDn"/> (modifies the group).</summary>
    AddMember,

    /// <summary>Remove the object from the group in <see cref="PlannedChange.TargetDn"/> (modifies the group).</summary>
    RemoveMember,

    /// <summary>Rename: <see cref="PlannedChange.TargetDn"/> holds the new escaped RDN, e.g. <c>CN=Jan Nowak</c>.</summary>
    Rename,

    /// <summary>Move: <see cref="PlannedChange.TargetDn"/> holds the new parent container DN.</summary>
    Move,

    /// <summary>Delete the object (<see cref="PlannedChange.DeleteSubtree"/> for containers).</summary>
    Delete,

    /// <summary>Create the object at <see cref="ObjectPlan.DistinguishedName"/> with <see cref="PlannedChange.Attributes"/> as initial values.</summary>
    Create,

    /// <summary>
    /// Restore a deleted object from the AD Recycle Bin (or reanimate a tombstone): <see cref="ObjectPlan.DistinguishedName"/>
    /// is the DN of the deleted object in CN=Deleted Objects, <see cref="PlannedChange.TargetDn"/> the full DN it gets back
    /// (its original or a new name under its original or a new parent). See <see cref="RecycleBin.PlanRestoreAsync"/>.
    /// </summary>
    Restore,

    /// <summary>
    /// Change the DACL of the object: <see cref="PlannedChange.Acl"/> holds the explicit ACEs to add and to remove. The
    /// executor reads nTSecurityDescriptor (DACL), applies the delta (<see cref="Security.DaclEditor"/>) and writes the
    /// result with the SD flags control DACL_SECURITY_INFORMATION – owner, group and SACL are never written.
    /// </summary>
    ModifySecurity,
}

/// <summary>How <see cref="AttributeChange.NewValues"/> relate to the current values.</summary>
public enum AttributeChangeKind
{
    /// <summary>The attribute will contain exactly <see cref="AttributeChange.NewValues"/>.</summary>
    Replace,

    /// <summary><see cref="AttributeChange.NewValues"/> are added to the existing values.</summary>
    AddValues,

    /// <summary><see cref="AttributeChange.NewValues"/> are removed from the existing values.</summary>
    RemoveValues,

    /// <summary>All values removed.</summary>
    Clear,
}

/// <summary>A planned change of one attribute with its value before and after (for the diff preview and rollback).</summary>
/// <param name="Attribute">LDAP display name.</param>
/// <param name="Kind">Semantics of <paramref name="NewValues"/>.</param>
/// <param name="OldValues">Values read from the directory while planning.</param>
/// <param name="NewValues">Values to write (see <paramref name="Kind"/>).</param>
public sealed record AttributeChange(
    string Attribute,
    AttributeChangeKind Kind,
    IReadOnlyList<string> OldValues,
    IReadOnlyList<string> NewValues)
{
    /// <summary>Resulting values after the change (for the preview "after" column).</summary>
    public IReadOnlyList<string> ResultValues => Kind switch
    {
        AttributeChangeKind.Replace => NewValues,
        AttributeChangeKind.Clear => [],
        AttributeChangeKind.AddValues => OldValues
            .Concat(NewValues.Where(n => !OldValues.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .ToArray(),
        AttributeChangeKind.RemoveValues => OldValues
            .Where(o => !NewValues.Contains(o, StringComparer.OrdinalIgnoreCase))
            .ToArray(),
        _ => NewValues,
    };
}

/// <summary>Severity of a validation finding.</summary>
public enum IssueSeverity
{
    Info,
    Warning,

    /// <summary>The object (or change) will not be executed.</summary>
    Error,
}

/// <summary>
/// A validation finding. <see cref="Code"/> is stable and used by the UI to show a localized message;
/// <see cref="Message"/> is an English fallback with details.
/// </summary>
public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Message, string? Attribute = null);

/// <summary>Stable issue codes produced by planners and validators.</summary>
public static class IssueCodes
{
    public const string NotFound = "NotFound";
    public const string Ambiguous = "Ambiguous";
    public const string NotWritable = "NotWritable";
    public const string ReadOnlyAttribute = "ReadOnlyAttribute";
    public const string UnknownAttribute = "UnknownAttribute";
    public const string SingleValued = "SingleValued";
    public const string TooLong = "TooLong";
    public const string TooShort = "TooShort";
    public const string InvalidValue = "InvalidValue";
    public const string MandatoryAttribute = "MandatoryAttribute";
    public const string Duplicate = "Duplicate";
    public const string DuplicateInBatch = "DuplicateInBatch";
    public const string TemplateError = "TemplateError";
    public const string NotApplicable = "NotApplicable";
    public const string ProtectedObject = "ProtectedObject";
    public const string AdminSdHolder = "AdminSdHolder";
    public const string CriticalSystemObject = "CriticalSystemObject";
    public const string NotReversible = "NotReversible";
    public const string Conflict = "Conflict";
    public const string NoChange = "NoChange";
    public const string BinaryNotSupported = "BinaryNotSupported";
    public const string InvalidCombination = "InvalidCombination";
    public const string PasswordNeverExpiresConflict = "PasswordNeverExpiresConflict";
    public const string TargetIsSameContainer = "TargetIsSameContainer";
    public const string CannotCreateChild = "CannotCreateChild";

    /// <summary>Disabling, deleting or resetting the account of a domain controller (use the demotion of the role instead).</summary>
    public const string DomainControllerAccount = "DomainControllerAccount";

    /// <summary>A domain controller is moved out of OU=Domain Controllers (default Group Policy of DCs no longer applies).</summary>
    public const string DomainControllerOutsideOu = "DomainControllerOutsideOu";

    /// <summary>Resetting a computer account breaks the computer's trust relationship until it is re-joined or repaired.</summary>
    public const string ComputerTrustLost = "ComputerTrustLost";

    /// <summary>Deleting a computer deletes its child objects too – BitLocker recovery passwords among them.</summary>
    public const string DeletesBitLockerRecovery = "DeletesBitLockerRecovery";

    /// <summary>The DACL of the object could not be read (no right to read permissions, or no descriptor).</summary>
    public const string SecurityNotReadable = "SecurityNotReadable";

    /// <summary>The object has child objects and the delete does not include the subtree.</summary>
    public const string HasChildren = "HasChildren";

    /// <summary>
    /// Restore: the original parent container of a deleted object is deleted as well (or does not exist any more) and is
    /// not restored with it - select the parent too, or restore to another container.
    /// </summary>
    public const string ParentDeleted = "ParentDeleted";

    /// <summary>Restore: the parent container is restored in the same plan but cannot be restored, so neither can the child.</summary>
    public const string ParentNotRestored = "ParentNotRestored";

    /// <summary>Restore: the chosen target container does not exist (any more) or is not a valid container.</summary>
    public const string TargetNotFound = "TargetNotFound";

    /// <summary>Restore: an object with the same name already exists in the target container.</summary>
    public const string AlreadyExists = "AlreadyExists";

    /// <summary>Restore: the deleted object was recycled (its deleted object lifetime expired) and cannot be restored.</summary>
    public const string Recycled = "Recycled";

    /// <summary>
    /// Restore without the AD Recycle Bin: the object is reanimated from a tombstone and gets back only the few
    /// attributes a tombstone keeps (group memberships and most other attributes are lost).
    /// </summary>
    public const string TombstoneReanimation = "TombstoneReanimation";
}

/// <summary>One step for one object. Steps of an object run sequentially in list order.</summary>
public sealed record PlannedChange
{
    public required ChangeAction Action { get; init; }

    /// <summary>Attribute changes for <see cref="ChangeAction.Modify"/> / <see cref="ChangeAction.Create"/>.</summary>
    public IReadOnlyList<AttributeChange> Attributes { get; init; } = [];

    /// <summary>Group DN (membership), new parent DN (move), new RDN (rename) or the full new DN (restore).</summary>
    public string? TargetDn { get; init; }

    /// <summary>New password for <see cref="ChangeAction.ResetPassword"/>. Sensitive: never log, never persist.</summary>
    public string? Password { get; init; }

    public bool DeleteSubtree { get; init; }

    /// <summary>ACEs added and removed by <see cref="ChangeAction.ModifySecurity"/> (a delta on the DACL read at execution).</summary>
    public Security.AclChange? Acl { get; init; }

    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);

    /// <summary>Never print the password.</summary>
    public override string ToString() =>
        $"{Action} target={TargetDn} attributes=[{string.Join(", ", Attributes.Select(a => a.Attribute))}]";
}

/// <summary>All planned steps for one directory object.</summary>
public sealed record ObjectPlan
{
    /// <summary>DN of the object when the plan was made (for Create: the DN to create).</summary>
    public required string DistinguishedName { get; init; }

    /// <summary>Human readable name for the preview grid.</summary>
    public required string DisplayName { get; init; }

    public ObjectKind Kind { get; init; }

    /// <summary>objectClass values (needed by validators and executors), may be empty.</summary>
    public IReadOnlyList<string> ObjectClasses { get; init; } = [];

    /// <summary>
    /// objectGUID when the planner read it. Restore plans set it: a deleted object is best identified by its GUID (the
    /// PowerShell export uses it), its DN in CN=Deleted Objects is mangled.
    /// </summary>
    public Guid? ObjectGuid { get; init; }

    /// <summary>Source row number when planned from CSV (1-based, header excluded).</summary>
    public int? SourceRow { get; init; }

    public IReadOnlyList<PlannedChange> Changes { get; init; } = [];

    /// <summary>Object level findings (not found, protected object...).</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    /// <summary>True when the object or any of its changes has an error - it will be skipped.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error) || Changes.Any(c => c.HasErrors);

    public bool HasWarnings => Issues.Any(i => i.Severity == IssueSeverity.Warning)
        || Changes.Any(c => c.Issues.Any(i => i.Severity == IssueSeverity.Warning));

    /// <summary>Nothing to do (all values already as requested).</summary>
    public bool IsNoOp => Changes.Count == 0 || Changes.All(c => c.Action == ChangeAction.Modify && c.Attributes.Count == 0);
}

/// <summary>
/// A complete, previewable set of changes. Created by a planner, shown to the operator as a diff,
/// then executed by <see cref="IBatchExecutor"/>. Plans are in-memory only.
/// </summary>
public sealed record ChangePlan
{
    public required IReadOnlyList<ObjectPlan> Objects { get; init; }

    /// <summary>Short operator-facing description ("Set department on 120 users").</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>When the current values were read from the directory.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>DC the current values were read from (execution must use the same DC).</summary>
    public string? Server { get; init; }

    public int ExecutableCount => Objects.Count(o => !o.HasErrors && !o.IsNoOp);

    public int ErrorCount => Objects.Count(o => o.HasErrors);

    public int NoOpCount => Objects.Count(o => !o.HasErrors && o.IsNoOp);

    /// <summary>True when the plan contains a password (results export must warn / mask).</summary>
    public bool ContainsPasswords => Objects.Any(o => o.Changes.Any(c => c.Password is not null));
}
