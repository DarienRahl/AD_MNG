using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Core.Templates;

namespace AdMng.Core.Batch;

/// <summary>
/// Default <see cref="IChangePlanner"/>: reads every target live from the session's DC, applies the operations in
/// request order to an in-memory working copy (templates see the values produced by earlier operations), drops
/// no-ops and validates the result (schema, permissions, uniqueness, protected objects).
/// </summary>
public sealed class ChangePlanner : IChangePlanner
{
    /// <summary>Attributes always read for every target.</summary>
    public static IReadOnlyList<string> BaseAttributes { get; } =
    [
        Attr.ObjectClass, Attr.Name, Attr.DisplayName, Attr.DistinguishedName, Attr.UserAccountControl, Attr.LockoutTime,
        Attr.PwdLastSet, Attr.AccountExpires, Attr.AdminCount, Attr.IsCriticalSystemObject, Attr.ObjectSid,
    ];

    private const string AccountExpiresNever = "9223372036854775807";

    /// <summary>userAccountControl bits that may be changed with <see cref="SetAccountFlagOperation"/>.</summary>
    private const UserAccountControl SettableFlags = UserAccountControl.Script | UserAccountControl.HomeDirRequired
        | UserAccountControl.PasswordNotRequired | UserAccountControl.PasswordCantChange
        | UserAccountControl.EncryptedTextPasswordAllowed | UserAccountControl.DontExpirePassword
        | UserAccountControl.MnsLogonAccount | UserAccountControl.SmartcardRequired | UserAccountControl.TrustedForDelegation
        | UserAccountControl.NotDelegated | UserAccountControl.UseDesKeyOnly | UserAccountControl.DontRequirePreauth
        | UserAccountControl.TrustedToAuthForDelegation;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>FILETIME 0; earlier dates cannot be converted.</summary>
    private static readonly DateTimeOffset FileTimeEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ITemplateEngine _templates;
    private readonly PasswordGenerator _passwords;
    private readonly TimeProvider _timeProvider;

    public ChangePlanner(ITemplateEngine templates, PasswordGenerator passwords, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(passwords);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _templates = templates;
        _passwords = passwords;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks><paramref name="progress"/> receives the number of targets planned so far.</remarks>
    public async Task<ChangePlan> PlanAsync(
        IDirectorySession session,
        BatchRequest request,
        PlanningOptions? options,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.TargetDns);
        ArgumentNullException.ThrowIfNull(request.Operations);

        var context = new PlanningContext(session, options ?? new PlanningOptions(), cancellationToken);
        var createdAt = _timeProvider.GetUtcNow();
        var analysis = RequestAnalysis.Create(request, _templates);
        await analysis.ResolveReferencesAsync(context).ConfigureAwait(false);

        var targets = DistinctTargets(request.TargetDns);
        var drafts = new ObjectPlanDraft[targets.Count];
        var completed = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, targets.Count),
            new ParallelOptions { MaxDegreeOfParallelism = context.MaxDegreeOfParallelism, CancellationToken = cancellationToken },
            async (index, _) =>
            {
                drafts[index] = await PlanObjectAsync(context, analysis, targets[index], index + 1).ConfigureAwait(false);
                progress?.Report(Interlocked.Increment(ref completed));
            }).ConfigureAwait(false);

        PlanValidation.ValidateFinalDnsInBatch(drafts);
        if (context.Options.CheckUniqueness)
        {
            await PlanValidation.ValidateUniquenessAsync(context, drafts).ConfigureAwait(false);
        }

        return new ChangePlan
        {
            Objects = drafts.Select(d => d.Build()).ToArray(),
            Title = request.Title,
            CreatedAt = createdAt,
            Server = session.Info.Server,
        };
    }

    private static List<string> DistinctTargets(IReadOnlyList<string> targets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(targets.Count);
        foreach (var target in targets)
        {
            ArgumentNullException.ThrowIfNull(target, nameof(targets));
            if (seen.Add(PlanningContext.Key(target)))
            {
                result.Add(target);
            }
        }

        return result;
    }

    private async Task<ObjectPlanDraft> PlanObjectAsync(PlanningContext context, RequestAnalysis analysis, string target, int counter)
    {
        if (!DistinguishedName.TryParse(target, out var parsedTarget) || parsedTarget.IsRoot)
        {
            var invalid = new ObjectPlanDraft(target, target, null);
            invalid.Issues.Add(PlanValidation.Error(IssueCodes.InvalidValue, $"'{target}' is not a valid distinguished name."));
            return invalid;
        }

        var current = await context.Session.GetAsync(target, analysis.Attributes, context.CancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            var missing = new ObjectPlanDraft(target, parsedTarget.Name, null);
            missing.Issues.Add(PlanValidation.Error(IssueCodes.NotFound, $"The object '{target}' does not exist (any more)."));
            return missing;
        }

        current = await context.CompleteRangedValuesAsync(current).ConfigureAwait(false);

        var draft = new ObjectPlanDraft(current.DistinguishedName, ObjectPlanDraft.DisplayNameOf(current), current);
        if (analysis.RequestErrors.Count > 0)
        {
            draft.Issues.AddRange(analysis.RequestErrors);
            return draft;
        }

        CheckProtection(draft, analysis);
        if (analysis.Delete is { } delete)
        {
            await PlanDeleteAsync(context, draft, delete).ConfigureAwait(false);
            return draft;
        }

        var permissions = await context.GetPermissionsAsync(current.DistinguishedName).ConfigureAwait(false);
        var planner = new ObjectOperationPlanner(this, context, analysis, draft, permissions, counter);
        planner.ApplyAll();
        draft.FinalizeAttributes();
        await PlanValidation.ValidateAttributeChangesAsync(context, draft, permissions).ConfigureAwait(false);
        await CheckFinalDnAsync(context, draft).ConfigureAwait(false);
        return draft;
    }

    private static void CheckProtection(ObjectPlanDraft draft, RequestAnalysis analysis)
    {
        var current = draft.Current!;
        if (current.IsProtectedByAdminSdHolder && analysis.Operations.Count > 0)
        {
            draft.Issues.Add(PlanValidation.Warning(IssueCodes.AdminSdHolder,
                "The object is protected by AdminSDHolder (adminCount=1): it is or was a member of a privileged group and " +
                "its permissions are reset hourly; changes may require Domain Admin rights."));
        }

        if (current.GetBoolean(Attr.IsCriticalSystemObject) == true && analysis.MovesDeletesOrRenames)
        {
            draft.Issues.Add(PlanValidation.Error(IssueCodes.CriticalSystemObject,
                "The object is a critical system object (isCriticalSystemObject=TRUE) and must not be moved, renamed or deleted."));
        }

        if (analysis.DeletesOrDisables && IsProtectedAccount(current))
        {
            draft.Issues.Add(PlanValidation.Error(IssueCodes.ProtectedObject,
                "Built-in Administrator, krbtgt and domain controller accounts cannot be deleted or disabled in a batch."));
        }
    }

    private static bool IsProtectedAccount(DirectoryObject current)
    {
        var rid = AdConvert.GetRid(current.ObjectSid);
        if (rid is WellKnownRids.Administrator or WellKnownRids.Krbtgt)
        {
            return true;
        }

        var uac = (UserAccountControl)(current.GetInt32(Attr.UserAccountControl) ?? 0);
        return (uac & (UserAccountControl.ServerTrustAccount | UserAccountControl.PartialSecretsAccount)) != 0;
    }

    private static async Task PlanDeleteAsync(PlanningContext context, ObjectPlanDraft draft, DeleteOperation delete)
    {
        var step = new StepDraft(ChangeAction.Delete) { DeleteSubtree = delete.IncludeSubtree };
        draft.Delete = step;
        if (delete.IncludeSubtree)
        {
            return;
        }

        var search = new DirectorySearch
        {
            BaseDn = draft.DistinguishedName,
            Scope = SearchScope.OneLevel,
            Filter = "(objectClass=*)",
            SizeLimit = 1,
            PageSize = 1,
        };
        await foreach (var _ in context.Session.SearchAsync(search, context.CancellationToken).ConfigureAwait(false))
        {
            step.Issues.Add(PlanValidation.Error(IssueCodes.HasChildren,
                "The object contains other objects; delete it including the subtree or remove the child objects first."));
            break;
        }
    }

    /// <summary>A rename/move must not collide with an existing object (checked with <see cref="PlanningOptions.CheckUniqueness"/>).</summary>
    private static async Task CheckFinalDnAsync(PlanningContext context, ObjectPlanDraft draft)
    {
        if (!context.Options.CheckUniqueness || draft.FinalDn is null || draft.NamingStep is not { } step
            || step.Issues.Any(i => i.Severity == IssueSeverity.Error)
            || DistinguishedName.AreEqual(draft.FinalDn, draft.DistinguishedName))
        {
            return;
        }

        var existing = await context.Session.GetAsync(draft.FinalDn, [Attr.DistinguishedName], context.CancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            step.Issues.Add(PlanValidation.Error(IssueCodes.Duplicate, $"An object named '{existing.DistinguishedName}' already exists."));
        }
    }

    /// <summary>Request-level preparation shared by all targets: parsed templates, regexes, referenced groups and containers.</summary>
    private sealed class RequestAnalysis
    {
        private readonly Dictionary<string, GroupReference> _groups = new(StringComparer.OrdinalIgnoreCase);

        private RequestAnalysis(IReadOnlyList<BatchOperation> operations)
        {
            Operations = operations;
            OperationErrors = new ValidationIssue?[operations.Count];
            Regexes = new Regex?[operations.Count];
        }

        public IReadOnlyList<BatchOperation> Operations { get; }

        /// <summary>Object-level errors applied to every target (invalid combinations).</summary>
        public List<ValidationIssue> RequestErrors { get; } = [];

        /// <summary>Per operation (by index): an error that prevents applying it (invalid template, regex...).</summary>
        public ValidationIssue?[] OperationErrors { get; }

        public Regex?[] Regexes { get; }

        public List<string> Attributes { get; } = [.. BaseAttributes];

        public DeleteOperation? Delete { get; private set; }

        public bool MovesDeletesOrRenames { get; private set; }

        public bool DeletesOrDisables { get; private set; }

        public MoveTarget? Move { get; private set; }

        public static RequestAnalysis Create(BatchRequest request, ITemplateEngine templates)
        {
            var analysis = new RequestAnalysis(request.Operations);
            var operations = request.Operations;
            if (operations.Any(o => o is DeleteOperation) && operations.Count > 1)
            {
                analysis.RequestErrors.Add(PlanValidation.Error(IssueCodes.InvalidCombination, "Delete cannot be combined with other operations."));
            }

            foreach (var (type, name) in new[] { (typeof(MoveOperation), "move"), (typeof(RenameOperation), "rename"), (typeof(ResetPasswordOperation), "password reset") })
            {
                if (operations.Count(o => o?.GetType() == type) > 1)
                {
                    analysis.RequestErrors.Add(PlanValidation.Error(IssueCodes.InvalidCombination, $"Only one {name} operation is allowed per batch."));
                }
            }

            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i] ?? throw new ArgumentException("Operations cannot contain null.", nameof(request));
                analysis.Analyze(i, operation, templates);
            }

            return analysis;
        }

        public GroupReference GetGroup(string dn) => _groups[PlanningContext.Key(dn)];

        public async Task ResolveReferencesAsync(PlanningContext context)
        {
            foreach (var group in _groups.Values)
            {
                if (!group.IsValidDn)
                {
                    continue;
                }

                group.Object = await context.GetReferenceAsync(group.RequestedDn).ConfigureAwait(false);
                if (group.Object is not null)
                {
                    group.Permissions = await context.GetPermissionsAsync(group.Object.DistinguishedName).ConfigureAwait(false);
                }
            }

            if (Move is { Issue: null } move)
            {
                move.Object = await context.GetReferenceAsync(move.RequestedDn).ConfigureAwait(false);
                if (move.Object is null)
                {
                    move.Issue = PlanValidation.Error(IssueCodes.NotFound, $"The target container '{move.RequestedDn}' does not exist.");
                }
                else
                {
                    move.Permissions = await context.GetPermissionsAsync(move.Object.DistinguishedName).ConfigureAwait(false);
                }
            }
        }

        private void Analyze(int index, BatchOperation operation, ITemplateEngine templates)
        {
            switch (operation)
            {
                case SetAttributeOperation set:
                    CheckAttributeName(index, set.Attribute);
                    CheckTemplate(index, set.ValueTemplate, templates);
                    break;
                case ClearAttributeOperation clear:
                    CheckAttributeName(index, clear.Attribute);
                    break;
                case AddValuesOperation add:
                    CheckAttributeName(index, add.Attribute);
                    foreach (var template in add.ValueTemplates)
                    {
                        CheckTemplate(index, template, templates);
                    }

                    break;
                case RemoveValuesOperation remove:
                    CheckAttributeName(index, remove.Attribute);
                    break;
                case FindReplaceOperation findReplace:
                    CheckAttributeName(index, findReplace.Attribute);
                    PrepareFindReplace(index, findReplace);
                    break;
                case SetAccountEnabledOperation { Enabled: false }:
                    DeletesOrDisables = true;
                    break;
                case SetAccountFlagOperation { Flag: UserAccountControl.AccountDisable, Value: true }:
                    DeletesOrDisables = true;
                    break;
                case ResetPasswordOperation:
                    AddAttributes(Attr.SamAccountName, Attr.GivenName, Attr.Sn);
                    break;
                case RenameOperation rename:
                    MovesDeletesOrRenames = true;
                    CheckTemplate(index, rename.NameTemplate, templates);
                    break;
                case MoveOperation move:
                    MovesDeletesOrRenames = true;
                    Move = new MoveTarget(move.TargetContainerDn);
                    break;
                case AddToGroupsOperation addToGroups:
                    RegisterGroups(addToGroups.GroupDns);
                    break;
                case RemoveFromGroupsOperation removeFromGroups:
                    RegisterGroups(removeFromGroups.GroupDns);
                    break;
                case DeleteOperation delete:
                    MovesDeletesOrRenames = true;
                    DeletesOrDisables = true;
                    Delete = delete;
                    break;
            }
        }

        private void CheckAttributeName(int index, string attribute)
        {
            if (string.IsNullOrWhiteSpace(attribute) || !char.IsAsciiLetter(attribute[0])
                || !attribute.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                OperationErrors[index] ??= PlanValidation.Error(IssueCodes.InvalidValue, $"'{attribute}' is not a valid attribute name.");
                return;
            }

            AddAttributes(attribute);
        }

        private void CheckTemplate(int index, string template, ITemplateEngine templates)
        {
            var parsed = templates.Parse(template ?? string.Empty);
            if (!parsed.IsValid)
            {
                OperationErrors[index] ??= PlanValidation.Error(IssueCodes.TemplateError,
                    $"Invalid template '{template}': {string.Join(" ", parsed.Errors)}");
            }

            AddAttributes([.. parsed.ReferencedAttributes]);
        }

        private void PrepareFindReplace(int index, FindReplaceOperation operation)
        {
            if (string.IsNullOrEmpty(operation.Find))
            {
                OperationErrors[index] ??= PlanValidation.Error(IssueCodes.InvalidValue, "The text to find is empty.", operation.Attribute);
                return;
            }

            if (!operation.UseRegex)
            {
                return;
            }

            try
            {
                var options = RegexOptions.CultureInvariant | (operation.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                Regexes[index] = new Regex(operation.Find, options, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                OperationErrors[index] ??= PlanValidation.Error(IssueCodes.InvalidValue,
                    $"Invalid regular expression '{operation.Find}': {ex.Message}", operation.Attribute);
            }
        }

        private void RegisterGroups(IReadOnlyList<string> groupDns)
        {
            AddAttributes(Attr.MemberOf, Attr.PrimaryGroupId);
            foreach (var dn in groupDns)
            {
                var key = PlanningContext.Key(dn);
                if (!_groups.ContainsKey(key))
                {
                    _groups[key] = new GroupReference(dn);
                }
            }
        }

        private void AddAttributes(params string[] attributes)
        {
            foreach (var attribute in attributes)
            {
                if (!Attributes.Contains(attribute, StringComparer.OrdinalIgnoreCase))
                {
                    Attributes.Add(attribute);
                }
            }
        }
    }

    /// <summary>A group referenced by membership operations (read once per plan).</summary>
    private sealed class GroupReference(string requestedDn)
    {
        public string RequestedDn { get; } = requestedDn;

        public bool IsValidDn { get; } = DistinguishedName.TryParse(requestedDn, out var dn) && !dn.IsRoot;

        public DirectoryObject? Object { get; set; }

        public EffectivePermissions Permissions { get; set; } = EffectivePermissions.Unknown;

        public bool IsGroup => Object?.Kind == ObjectKind.Group;

        public uint? Rid => AdConvert.GetRid(Object?.ObjectSid);
    }

    /// <summary>The target container of a move (read once per plan).</summary>
    private sealed class MoveTarget
    {
        public MoveTarget(string requestedDn)
        {
            RequestedDn = requestedDn;
            if (!DistinguishedName.TryParse(requestedDn, out var dn) || dn.IsRoot)
            {
                Issue = PlanValidation.Error(IssueCodes.InvalidValue, $"'{requestedDn}' is not a valid container DN.");
            }
        }

        public string RequestedDn { get; }

        public ValidationIssue? Issue { get; set; }

        public DirectoryObject? Object { get; set; }

        public EffectivePermissions Permissions { get; set; } = EffectivePermissions.Unknown;
    }

    /// <summary>Applies the operations of the request to one object.</summary>
    private sealed class ObjectOperationPlanner
    {
        private readonly ChangePlanner _owner;
        private readonly PlanningContext _context;
        private readonly RequestAnalysis _analysis;
        private readonly ObjectPlanDraft _draft;
        private readonly DirectoryObject _current;
        private readonly EffectivePermissions _permissions;
        private readonly int _counter;
        private readonly Dictionary<string, MembershipState> _memberships = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MembershipState> _membershipOrder = [];
        private readonly HashSet<string> _reportedNotApplicable = new(StringComparer.Ordinal);
        private string? _newName;
        private bool _uacErrorReported;

        public ObjectOperationPlanner(
            ChangePlanner owner,
            PlanningContext context,
            RequestAnalysis analysis,
            ObjectPlanDraft draft,
            EffectivePermissions permissions,
            int counter)
        {
            _owner = owner;
            _context = context;
            _analysis = analysis;
            _draft = draft;
            _current = draft.Current!;
            _permissions = permissions;
            _counter = counter;
        }

        private ObjectKind Kind => _draft.Kind;

        public void ApplyAll()
        {
            for (var i = 0; i < _analysis.Operations.Count; i++)
            {
                var operation = _analysis.Operations[i];
                if (_analysis.OperationErrors[i] is { } error)
                {
                    _draft.AttributeIssues.Add(error);
                    continue;
                }

                Apply(operation, _analysis.Regexes[i]);
            }

            CompleteMemberships();
            CompleteNaming();
            CheckPasswordNeverExpires();
        }

        private void Apply(BatchOperation operation, Regex? regex)
        {
            switch (operation)
            {
                case SetAttributeOperation set:
                    ApplySet(set);
                    break;
                case ClearAttributeOperation clear:
                    Edit(clear.Attribute).Replace([]);
                    break;
                case AddValuesOperation add:
                    ApplyAddValues(add);
                    break;
                case RemoveValuesOperation remove:
                    Edit(remove.Attribute).Remove(remove.Values.Where(v => !string.IsNullOrEmpty(v)));
                    break;
                case FindReplaceOperation findReplace:
                    ApplyFindReplace(findReplace, regex);
                    break;
                case SetAccountEnabledOperation enabled:
                    ApplyFlag(UserAccountControl.AccountDisable, !enabled.Enabled, "Enable/disable");
                    break;
                case SetAccountFlagOperation flag:
                    ApplyAccountFlag(flag);
                    break;
                case UnlockAccountOperation:
                    ApplyUnlock();
                    break;
                case RequirePasswordChangeOperation requirePasswordChange:
                    ApplyRequirePasswordChange(requirePasswordChange.Required);
                    break;
                case SetAccountExpirationOperation expiration:
                    ApplyExpiration(expiration);
                    break;
                case ResetPasswordOperation reset:
                    ApplyResetPassword(reset);
                    break;
                case RenameOperation rename:
                    ApplyRename(rename);
                    break;
                case MoveOperation:
                    ApplyMove();
                    break;
                case AddToGroupsOperation addToGroups:
                    ApplyMembership(addToGroups.GroupDns, add: true);
                    break;
                case RemoveFromGroupsOperation removeFromGroups:
                    ApplyMembership(removeFromGroups.GroupDns, add: false);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported batch operation {operation.GetType().Name}.");
            }
        }

        private AttributeEdit Edit(string attribute) => _draft.GetEdit(attribute, _context.Schema);

        private string? Render(string template, string? attribute)
        {
            try
            {
                return _owner._templates.Render(template, new TemplateContext
                {
                    Attribute = _draft.CurrentValues,
                    DistinguishedName = _draft.DistinguishedName,
                    Domain = _context.Session.Info.DomainDnsName,
                    Counter = _counter,
                    Now = _owner._timeProvider.GetLocalNow(),
                });
            }
            catch (FormatException ex)
            {
                _draft.AttributeIssues.Add(PlanValidation.Error(IssueCodes.TemplateError, ex.Message, attribute));
                return null;
            }
        }

        private void ApplySet(SetAttributeOperation set)
        {
            if (Render(set.ValueTemplate, set.Attribute) is not { } value)
            {
                return;
            }

            if (value.Length == 0)
            {
                if (set.EmptyClears)
                {
                    Edit(set.Attribute).Replace([]);
                }
                else
                {
                    _draft.AttributeIssues.Add(PlanValidation.Info(IssueCodes.NoChange,
                        $"The template for '{set.Attribute}' produced an empty value; the attribute is left unchanged.", set.Attribute));
                }

                return;
            }

            Edit(set.Attribute).Replace([PlanValidation.NormalizeValue(_context.Schema, set.Attribute, value)]);
        }

        private void ApplyAddValues(AddValuesOperation add)
        {
            var values = new List<string>();
            foreach (var template in add.ValueTemplates)
            {
                if (Render(template, add.Attribute) is not { } value)
                {
                    return;
                }

                if (value.Length > 0)
                {
                    values.Add(PlanValidation.NormalizeValue(_context.Schema, add.Attribute, value));
                }
            }

            Edit(add.Attribute).Add(values);
        }

        private void ApplyFindReplace(FindReplaceOperation operation, Regex? regex)
        {
            var edit = Edit(operation.Attribute);
            var comparison = operation.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var result = new List<string>();
            foreach (var value in edit.Current)
            {
                string replaced;
                try
                {
                    replaced = regex is not null
                        ? regex.Replace(value, operation.Replace)
                        : value.Replace(operation.Find, operation.Replace, comparison);
                }
                catch (RegexMatchTimeoutException)
                {
                    _draft.AttributeIssues.Add(PlanValidation.Error(IssueCodes.InvalidValue,
                        $"The regular expression '{operation.Find}' took too long to evaluate.", operation.Attribute));
                    return;
                }

                if (replaced.Length > 0)
                {
                    result.Add(replaced);
                }
            }

            if (!result.SequenceEqual(edit.Current, StringComparer.Ordinal))
            {
                edit.Replace(result);
            }
        }

        private bool RequireKind(Func<ObjectKind, bool> applicable, string operationName)
        {
            if (applicable(Kind))
            {
                return true;
            }

            if (_reportedNotApplicable.Add(operationName))
            {
                _draft.Issues.Add(PlanValidation.Warning(IssueCodes.NotApplicable,
                    $"{operationName} does not apply to objects of type {Kind}; the object is skipped for this operation."));
            }

            return false;
        }

        private static bool IsPasswordAccount(ObjectKind kind) => kind is ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Computer;

        private static bool CanBeMember(ObjectKind kind) => kind.IsSecurityPrincipal() || kind == ObjectKind.Contact;

        private int? CurrentUserAccountControl()
        {
            var values = _draft.CurrentValues(Attr.UserAccountControl);
            if (values.Count > 0 && int.TryParse(values[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var uac))
            {
                return uac;
            }

            if (!_uacErrorReported)
            {
                _uacErrorReported = true;
                _draft.AttributeIssues.Add(PlanValidation.Error(IssueCodes.InvalidValue,
                    "The current userAccountControl value could not be read, so account flags cannot be changed safely.", Attr.UserAccountControl));
            }

            return null;
        }

        private long CurrentInt64(string attribute)
        {
            var values = _draft.CurrentValues(attribute);
            return values.Count > 0 && long.TryParse(values[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }

        private void ApplyFlag(UserAccountControl flag, bool set, string operationName)
        {
            if (!RequireKind(k => k.IsAccount(), operationName) || CurrentUserAccountControl() is not { } uac)
            {
                return;
            }

            var updated = set ? uac | (int)flag : uac & ~(int)flag;
            if (updated != uac)
            {
                Edit(Attr.UserAccountControl).Replace([updated.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        private void ApplyAccountFlag(SetAccountFlagOperation operation)
        {
            switch (operation.Flag)
            {
                case UserAccountControl.AccountDisable:
                    ApplyFlag(UserAccountControl.AccountDisable, operation.Value, "Enable/disable");
                    return;
                case UserAccountControl.Lockout when !operation.Value:
                    ApplyUnlock();
                    return;
                case UserAccountControl.None:
                    return;
            }

            if ((operation.Flag & ~SettableFlags) != 0)
            {
                _draft.AttributeIssues.Add(PlanValidation.Error(IssueCodes.InvalidValue,
                    $"The userAccountControl flag {operation.Flag} cannot be changed in a batch (account type and computed flags are maintained by the system).",
                    Attr.UserAccountControl));
                return;
            }

            if (operation.Flag.HasFlag(UserAccountControl.PasswordCantChange) && operation.Value && Kind.IsAccount())
            {
                _draft.AttributeIssues.Add(PlanValidation.Warning(IssueCodes.NotApplicable,
                    "Domain controllers ignore the PASSWD_CANT_CHANGE flag; 'User cannot change password' is enforced by permissions.",
                    Attr.UserAccountControl));
            }

            ApplyFlag(operation.Flag, operation.Value, $"Account flag {operation.Flag}");
        }

        private void ApplyUnlock()
        {
            if (RequireKind(k => k.IsAccount(), "Unlock") && CurrentInt64(Attr.LockoutTime) > 0)
            {
                Edit(Attr.LockoutTime).Replace(["0"]);
            }
        }

        private void ApplyRequirePasswordChange(bool required)
        {
            if (!RequireKind(IsPasswordAccount, "Password change at next logon"))
            {
                return;
            }

            var current = _draft.CurrentValues(Attr.PwdLastSet);
            var mustChangeNow = current.Count > 0 && current[0] == "0";
            if (required && !mustChangeNow)
            {
                Edit(Attr.PwdLastSet).Replace(["0"]);
            }
            else if (!required && mustChangeNow)
            {
                // -1 makes the DC set pwdLastSet to the current time; only meaningful when the flag is currently set.
                Edit(Attr.PwdLastSet).Replace(["-1"]);
            }
        }

        private void ApplyExpiration(SetAccountExpirationOperation operation)
        {
            if (!RequireKind(k => k.IsAccount(), "Account expiration"))
            {
                return;
            }

            var current = _draft.CurrentValues(Attr.AccountExpires);
            var currentlyNever = current.Count == 0 || current[0] is "0" or AccountExpiresNever;
            if (operation.ExpiresAt is null)
            {
                if (!currentlyNever)
                {
                    Edit(Attr.AccountExpires).Replace([AccountExpiresNever]);
                }

                return;
            }

            if (operation.ExpiresAt.Value < FileTimeEpoch)
            {
                _draft.AttributeIssues.Add(PlanValidation.Error(IssueCodes.InvalidValue,
                    "The expiration date is before 1601-01-01 (UTC), which accountExpires cannot hold.", Attr.AccountExpires));
                return;
            }

            Edit(Attr.AccountExpires).Replace([AdConvert.ToFileTime(operation.ExpiresAt.Value).ToString(CultureInfo.InvariantCulture)]);
        }

        private void ApplyResetPassword(ResetPasswordOperation operation)
        {
            if (!RequireKind(IsPasswordAccount, "Password reset"))
            {
                return;
            }

            string? password = null;
            var issues = new List<ValidationIssue>();
            if (operation.Mode == PasswordMode.Fixed)
            {
                if (string.IsNullOrEmpty(operation.FixedPassword))
                {
                    issues.Add(PlanValidation.Error(IssueCodes.InvalidValue, "No password was entered."));
                }
                else
                {
                    password = operation.FixedPassword;
                }
            }
            else if (operation.GeneratedLength is < PasswordGenerator.MinimumLength or > PasswordGenerator.MaximumLength)
            {
                issues.Add(PlanValidation.Error(IssueCodes.InvalidValue,
                    $"Generated passwords must be {PasswordGenerator.MinimumLength} to {PasswordGenerator.MaximumLength} characters long."));
            }
            else
            {
                password = _owner._passwords.Generate(operation.GeneratedLength, ForbiddenPasswordTokens());
            }

            var step = new StepDraft(ChangeAction.ResetPassword) { Password = password };
            step.Issues.AddRange(issues);
            _draft.PasswordReset = step;

            if (operation.MustChangeAtNextLogon)
            {
                var edit = Edit(Attr.PwdLastSet);
                edit.Replace(["0"]);

                // The reset itself sets pwdLastSet to "now", so 0 must be written even when it is 0 already.
                edit.ForceWrite = true;
            }

            if (operation.UnlockAccount && CurrentInt64(Attr.LockoutTime) > 0)
            {
                Edit(Attr.LockoutTime).Replace(["0"]);
            }
        }

        /// <summary>AD complexity rejects passwords containing the account name or parts (3+ chars) of the display name.</summary>
        private IEnumerable<string> ForbiddenPasswordTokens()
        {
            char[] separators = [',', '.', '-', '_', ' ', '#', '\t'];
            var tokens = new List<string>();
            tokens.AddRange(_current.GetStrings(Attr.SamAccountName));
            foreach (var attribute in new[] { Attr.DisplayName, Attr.GivenName, Attr.Sn })
            {
                foreach (var value in _current.GetStrings(attribute))
                {
                    tokens.AddRange(value.Split(separators, StringSplitOptions.RemoveEmptyEntries));
                }
            }

            return tokens;
        }

        private void ApplyRename(RenameOperation operation)
        {
            if (Render(operation.NameTemplate, null) is not { } rendered)
            {
                return;
            }

            var value = rendered.Trim();
            if (value.Length == 0)
            {
                var step = new StepDraft(ChangeAction.Rename) { TargetDn = string.Empty };
                step.Issues.Add(PlanValidation.Error(IssueCodes.InvalidValue, "The name template produced an empty name."));
                _draft.Rename = step;
                return;
            }

            if (operation.AlsoSetDisplayName)
            {
                Edit(Attr.DisplayName).Replace([value]);
            }

            _newName = string.Equals(value, _current.Dn.Name, StringComparison.Ordinal) ? null : value;
        }

        private void ApplyMove()
        {
            var target = _analysis.Move!;
            if (target.Issue is not null)
            {
                var invalid = new StepDraft(ChangeAction.Move) { TargetDn = target.RequestedDn };
                invalid.Issues.Add(target.Issue);
                _draft.Move = invalid;
                return;
            }

            var targetDn = target.Object!.Dn;
            var objectDn = _current.Dn;
            if (objectDn.Parent == targetDn)
            {
                _draft.Issues.Add(PlanValidation.Info(IssueCodes.TargetIsSameContainer, "The object is already in the target container."));
                return;
            }

            var step = new StepDraft(ChangeAction.Move) { TargetDn = target.Object.DistinguishedName };
            if (targetDn == objectDn || targetDn.IsDescendantOf(objectDn))
            {
                step.Issues.Add(PlanValidation.Error(IssueCodes.InvalidValue, "An object cannot be moved into itself or into its own subtree."));
            }
            else if (PlanValidation.GetStructuralClass(_draft.ObjectClasses, _context.Schema) is { } structuralClass
                     && !target.Permissions.CanCreate(structuralClass))
            {
                step.Issues.Add(PlanValidation.Error(IssueCodes.CannotCreateChild,
                    $"You do not have permission to create '{structuralClass}' objects in '{target.Object.DistinguishedName}'."));
            }

            _draft.Move = step;
        }

        private void ApplyMembership(IReadOnlyList<string> groupDns, bool add)
        {
            if (!RequireKind(CanBeMember, add ? "Add to group" : "Remove from group"))
            {
                return;
            }

            foreach (var groupDn in groupDns)
            {
                var group = _analysis.GetGroup(groupDn);
                var key = PlanningContext.Key(groupDn);
                if (!_memberships.TryGetValue(key, out var state))
                {
                    state = CreateMembershipState(group, add);
                    _memberships[key] = state;
                    _membershipOrder.Add(state);
                }

                if (state.Error is not null)
                {
                    continue;
                }

                if (add)
                {
                    state.IsMember = true;
                }
                else if (state.IsPrimary)
                {
                    _draft.Issues.Add(PlanValidation.Warning(IssueCodes.NotApplicable,
                        $"'{group.Object!.Name}' is the primary group of the object; change the primary group before removing the membership."));
                }
                else
                {
                    state.IsMember = false;
                }
            }
        }

        private MembershipState CreateMembershipState(GroupReference group, bool add)
        {
            ValidationIssue? error = null;
            if (!group.IsValidDn)
            {
                error = PlanValidation.Error(IssueCodes.InvalidValue, $"'{group.RequestedDn}' is not a valid group DN.");
            }
            else if (group.Object is null)
            {
                error = PlanValidation.Error(IssueCodes.NotFound, $"The group '{group.RequestedDn}' does not exist.");
            }
            else if (!group.IsGroup)
            {
                error = PlanValidation.Error(IssueCodes.InvalidValue, $"'{group.Object.DistinguishedName}' is not a group.");
            }
            else if (group.Object.Dn == _current.Dn)
            {
                error = PlanValidation.Error(IssueCodes.InvalidValue, "A group cannot be a member of itself.");
            }

            if (error is not null)
            {
                return new MembershipState(group, add) { Error = error };
            }

            var isPrimary = group.Rid is { } rid && _current.GetInt64(Attr.PrimaryGroupId) == rid;
            var isMember = isPrimary || _current.GetStrings(Attr.MemberOf).Any(m => DistinguishedName.AreEqual(m, group.Object!.DistinguishedName));
            return new MembershipState(group, add) { IsPrimary = isPrimary, Initially = isMember, IsMember = isMember };
        }

        private void CompleteMemberships()
        {
            foreach (var state in _membershipOrder)
            {
                if (state.Error is not null)
                {
                    var failed = new StepDraft(state.FirstActionWasAdd ? ChangeAction.AddMember : ChangeAction.RemoveMember)
                    {
                        TargetDn = state.Group.Object?.DistinguishedName ?? state.Group.RequestedDn,
                    };
                    failed.Issues.Add(state.Error);
                    _draft.Memberships.Add(failed);
                    continue;
                }

                if (state.IsMember == state.Initially)
                {
                    continue;
                }

                var step = new StepDraft(state.IsMember ? ChangeAction.AddMember : ChangeAction.RemoveMember)
                {
                    TargetDn = state.Group.Object!.DistinguishedName,
                };
                if (!state.Group.Permissions.CanWrite(Attr.Member))
                {
                    step.Issues.Add(PlanValidation.Error(IssueCodes.NotWritable,
                        $"You do not have permission to change the members of '{state.Group.Object.Name}'."));
                }

                _draft.Memberships.Add(step);
            }
        }

        private void CompleteNaming()
        {
            var objectDn = _current.Dn;
            if (_newName is not null)
            {
                var leaf = objectDn.Leaf;
                var step = new StepDraft(ChangeAction.Rename) { TargetDn = $"{leaf.Type}={DistinguishedName.EscapeValue(_newName)}" };
                if (_context.HasSchema && _context.Schema.FindAttribute(leaf.Type) is { RangeUpper: { } upper } && _newName.Length > upper)
                {
                    step.Issues.Add(PlanValidation.Error(IssueCodes.TooLong,
                        $"The new name is {_newName.Length} characters long; the maximum is {upper}.", leaf.Type));
                }

                if (!_permissions.CanWrite(Attr.Name))
                {
                    step.Issues.Add(PlanValidation.Error(IssueCodes.NotWritable, "You do not have permission to rename this object."));
                }

                _draft.Rename = step;
            }

            var renamed = _newName is not null;
            var moved = _draft.Move is { } move && move.Issues.All(i => i.Severity != IssueSeverity.Error);
            if (!renamed && !moved)
            {
                return;
            }

            var parent = moved ? _analysis.Move!.Object!.Dn : objectDn.Parent!;
            _draft.FinalDn = parent.Child(objectDn.Leaf.Type, _newName ?? objectDn.Name).ToString();
        }

        private void CheckPasswordNeverExpires()
        {
            var pwdLastSet = _draft.FindEdit(Attr.PwdLastSet);
            if (pwdLastSet is not { Current: ["0"] } || (!pwdLastSet.ForceWrite && pwdLastSet.Original is ["0"]))
            {
                return;
            }

            var uacValues = _draft.CurrentValues(Attr.UserAccountControl);
            if (uacValues.Count > 0 && int.TryParse(uacValues[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var uac)
                && ((UserAccountControl)uac).HasFlag(UserAccountControl.DontExpirePassword))
            {
                _draft.AttributeIssues.Add(PlanValidation.Warning(IssueCodes.PasswordNeverExpiresConflict,
                    "'Password never expires' is set, so the user will not be required to change the password at next logon.",
                    Attr.PwdLastSet));
            }
        }

        private sealed class MembershipState(GroupReference group, bool firstActionWasAdd)
        {
            public GroupReference Group { get; } = group;

            public bool FirstActionWasAdd { get; } = firstActionWasAdd;

            public ValidationIssue? Error { get; init; }

            public bool IsPrimary { get; init; }

            public bool Initially { get; init; }

            public bool IsMember { get; set; }
        }
    }
}
