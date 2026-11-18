using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Computers;
using AdMng.Core.Model;

namespace AdMng.Core.Batch.Creation;

// BuildPlan (docs/dev/phase3-bulk-create.md 2.5): pure – the computation's values plus the option-driven attributes and steps,
// in the ADUC order per kind; passwords generated per account or the shared one copied in (rule 2).
public sealed partial class BulkCreatePlanner
{
    /// <summary>NORMAL_ACCOUNT | ACCOUNTDISABLE: a user is created disabled (AD refuses an enabled account without a password).</summary>
    public const int CreatedUserAccountControl = (int)(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable);

    /// <summary>NORMAL_ACCOUNT: the user after its password was set (<see cref="BulkAccountOptions.Enabled"/>).</summary>
    public const int EnabledUserAccountControl = (int)UserAccountControl.NormalAccount;

    /// <summary>WORKSTATION_TRUST_ACCOUNT | ACCOUNTDISABLE: a computer is created disabled, then gets its random password.</summary>
    public const int CreatedComputerAccountControl = (int)(UserAccountControl.WorkstationTrustAccount | UserAccountControl.AccountDisable);

    /// <summary>WORKSTATION_TRUST_ACCOUNT: the computer after its machine password was set.</summary>
    public const int EnabledComputerAccountControl = (int)UserAccountControl.WorkstationTrustAccount;

    private static readonly char[] DisplayNameSeparators = [',', '.', '-', '_', '#', '\t', ' '];

    public ChangePlan BuildPlan(BulkCreateComputation computation, string? sharedPassword, string title)
    {
        ArgumentNullException.ThrowIfNull(computation);
        ArgumentNullException.ThrowIfNull(title);
        var input = computation.Input;
        var planIssues = PlanIssues(computation, sharedPassword);
        var objects = new List<ObjectPlan>();
        foreach (var row in computation.Rows)
        {
            if (row.Status is BulkRowStatus.Ok or BulkRowStatus.Warning or BulkRowStatus.Error)
            {
                objects.Add(PlanObject(input, row, planIssues, sharedPassword));
            }
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

    /// <summary>
    /// The errors every object gets: the computation's run errors (a plan built anyway executes nothing) and the checks of a
    /// shared password against the domain policy and the PSOs of the option groups (2.5 step 1).
    /// </summary>
    private static List<ValidationIssue> PlanIssues(BulkCreateComputation computation, string? sharedPassword)
    {
        var issues = computation.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        var input = computation.Input;
        if (!IsUserKind(input.Rules.Kind) || input.Options.PasswordMode != InitialPasswordMode.SameForAll)
        {
            return issues;
        }

        if (string.IsNullOrEmpty(sharedPassword))
        {
            issues.Add(Error(IssueCodes.InvalidValue, "No password was entered."));
            return issues;
        }

        var minimum = computation.PasswordPolicy?.MinimumLength ?? 0;
        var complexity = computation.PasswordPolicy?.ComplexityRequired == true;
        foreach (var pso in computation.Issues.Where(i => i.Code == BulkCreateIssueCodes.GroupPasswordPolicy && i.Arguments.Count >= PsoArguments))
        {
            if (int.TryParse(pso.Arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var psoMinimum))
            {
                minimum = Math.Max(minimum, psoMinimum);
            }

            complexity |= pso.Arguments[2] == "1";
        }

        if (sharedPassword.Length < minimum)
        {
            issues.Add(Error(BulkCreateIssueCodes.PasswordTooShortForDomain, "The password is shorter than the domain minimum.", null, Text(minimum)));
        }

        if (complexity && Categories(sharedPassword) < 3)
        {
            issues.Add(Error(BulkCreateIssueCodes.PasswordNotComplex, "The password does not meet the complexity rule."));
        }

        return issues;
    }

    /// <summary>The AD complexity categories of a password: upper case, lower case, digit, non-alphanumeric, other Unicode letter.</summary>
    internal static int Categories(string password)
    {
        bool upper = false, lower = false, digit = false, special = false, other = false;
        foreach (var c in password)
        {
            if (char.IsUpper(c))
            {
                upper = true;
            }
            else if (char.IsLower(c))
            {
                lower = true;
            }
            else if (char.IsDigit(c))
            {
                digit = true;
            }
            else if (char.IsLetter(c))
            {
                other = true;
            }
            else
            {
                special = true;
            }
        }

        return (upper ? 1 : 0) + (lower ? 1 : 0) + (digit ? 1 : 0) + (special ? 1 : 0) + (other ? 1 : 0);
    }

    private ObjectPlan PlanObject(BulkCreateInput input, BulkCreateRow row, IReadOnlyList<ValidationIssue> planIssues, string? sharedPassword)
    {
        var kind = input.Rules.Kind;
        var issues = row.Issues.Concat(planIssues).ToList();
        var create = CreateStep(input, row);
        var changes = new List<PlannedChange> { create };
        if (row.Status != BulkRowStatus.Error && planIssues.Count == 0)
        {
            changes.AddRange(kind switch
            {
                ObjectKind.User or ObjectKind.InetOrgPerson => UserSteps(input, row, sharedPassword),
                ObjectKind.Computer => ComputerSteps(input, row),
                _ => [],
            });
            if (row.ManagerDn is { Length: > 0 } manager)
            {
                var attribute = kind is ObjectKind.Group or ObjectKind.Computer ? Attr.ManagedBy : Attr.Manager;
                changes.Add(new PlannedChange
                {
                    Action = ChangeAction.Modify,
                    Attributes = [new AttributeChange(attribute, AttributeChangeKind.Replace, [], [manager])],
                });
            }

            changes.AddRange(row.GroupDns.Select(group => new PlannedChange { Action = ChangeAction.AddMember, TargetDn = group }));
        }

        var plan = new ObjectPlan
        {
            DistinguishedName = row.DistinguishedName ?? FallbackDn(input.ContainerDn, row.Number),
            DisplayName = row.Value(Attr.Cn) ?? string.Empty,
            Kind = kind,
            ObjectClasses = CreationNames.ClassChainOf(kind),
            SourceRow = row.Number,
            Changes = changes,
            Issues = issues,
        };
        return plan with { PowerShell = BulkCreatePowerShell.For(plan) };
    }

    /// <summary>A DN for a row without a valid <c>cn</c> (an error row, never executed): <c>CN=#&lt;row&gt;</c> in the container.</summary>
    private static string FallbackDn(string containerDn, int row) =>
        Ldap.DistinguishedName.TryParse(containerDn, out var container)
            ? container.Child("CN", "#" + Text(row)).ToString()
            : "CN=#" + Text(row);

    /// <summary>The Create step: the class, every row value but <c>cn</c> (the RDN) and the option-driven attributes of the kind.</summary>
    private static PlannedChange CreateStep(BulkCreateInput input, BulkCreateRow row)
    {
        var kind = input.Rules.Kind;
        var attributes = new List<AttributeChange>
        {
            new(Attr.ObjectClass, AttributeChangeKind.Replace, [], [CreationNames.StructuralClassOf(kind)]),
        };
        foreach (var value in row.Values)
        {
            var values = value.Values.Where(v => v.Length > 0).ToList();
            if (values.Count > 0 && !Is(value.Attribute, Attr.Cn) && !Is(value.Attribute, Attr.Name) && !Is(value.Attribute, Attr.ObjectClass))
            {
                attributes.Add(new AttributeChange(value.Attribute, AttributeChangeKind.Replace, [], values));
            }
        }

        switch (kind)
        {
            case ObjectKind.User or ObjectKind.InetOrgPerson:
                attributes.Add(Replace(Attr.UserAccountControl, Text(CreatedUserAccountControl)));
                if (row.Value(Attr.AccountExpires) is null && input.Options.ExpiresAt is { } expires)
                {
                    attributes.Add(Replace(Attr.AccountExpires, AdConvert.ToFileTime(expires).ToString(CultureInfo.InvariantCulture)));
                }

                break;
            case ObjectKind.Group:
                attributes.Add(Replace(Attr.GroupType, Text(GroupTypes.Compose(input.Options.GroupScope, input.Options.SecurityGroup))));
                break;
            case ObjectKind.Computer:
                attributes.Add(Replace(Attr.UserAccountControl, Text(CreatedComputerAccountControl)));
                break;
        }

        if (input.Rules.AddPrimarySmtp && row.Value(Attr.Mail) is { Length: > 0 } mail && kind != ObjectKind.Computer)
        {
            attributes.Add(Replace(Attr.ProxyAddresses, "SMTP:" + mail));
        }

        return new PlannedChange { Action = ChangeAction.Create, Attributes = attributes };
    }

    private static AttributeChange Replace(string attribute, string value) => new(attribute, AttributeChangeKind.Replace, [], [value]);

    /// <summary>User (2.5): the initial password, then enable and must-change in one modify (only when a password is set).</summary>
    private List<PlannedChange> UserSteps(BulkCreateInput input, BulkCreateRow row, string? sharedPassword)
    {
        var options = input.Options;
        if (options.PasswordMode == InitialPasswordMode.None)
        {
            return [];
        }

        var steps = new List<PlannedChange> { PasswordStep(options, row, sharedPassword) };
        var modify = new List<AttributeChange>();
        if (options.Enabled)
        {
            modify.Add(new AttributeChange(Attr.UserAccountControl, AttributeChangeKind.Replace, [Text(CreatedUserAccountControl)], [Text(EnabledUserAccountControl)]));
        }

        if (options.MustChangeAtLogon)
        {
            modify.Add(new AttributeChange(Attr.PwdLastSet, AttributeChangeKind.Replace, [], ["0"]));
        }

        if (modify.Count > 0)
        {
            steps.Add(new PlannedChange { Action = ChangeAction.Modify, Attributes = modify });
        }

        return steps;
    }

    private PlannedChange PasswordStep(BulkAccountOptions options, BulkCreateRow row, string? sharedPassword)
    {
        if (options.PasswordMode == InitialPasswordMode.SameForAll)
        {
            var password = sharedPassword ?? string.Empty;
            var issues = ContainsName(password, row)
                ? [Warning(BulkCreateIssueCodes.PasswordContainsName, "The password contains the account name or a part of the display name.")]
                : Array.Empty<ValidationIssue>();
            return new PlannedChange { Action = ChangeAction.ResetPassword, Password = password, Issues = issues };
        }

        return new PlannedChange { Action = ChangeAction.ResetPassword, Password = _passwords.Generate(options.GeneratedLength, ForbiddenTokens(row)) };
    }

    /// <summary>The account name and the parts of the names (as <c>ChangePlanner.ForbiddenPasswordTokens</c>).</summary>
    private static IEnumerable<string> ForbiddenTokens(BulkCreateRow row)
    {
        var tokens = new List<string>();
        if (row.Value(Attr.SamAccountName) is { } sam)
        {
            tokens.Add(sam.TrimEnd('$'));
        }

        foreach (var attribute in new[] { Attr.DisplayName, Attr.GivenName, Attr.Sn })
        {
            if (row.Value(attribute) is { } value)
            {
                tokens.AddRange(value.Split(DisplayNameSeparators, StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return tokens;
    }

    /// <summary>AD's complexity rule: the sAMAccountName, or a display-name part of 3+ characters, in the password (case-insensitive).</summary>
    private static bool ContainsName(string password, BulkCreateRow row) =>
        password.Length > 0 && ForbiddenTokens(row).Any(t => t.Length >= 3 && password.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Computer (2.5): the join right, a random machine password nobody sees, then enabled as a workstation account.</summary>
    private List<PlannedChange> ComputerSteps(BulkCreateInput input, BulkCreateRow row)
    {
        var steps = new List<PlannedChange>();
        if (input.Options.JoinerSid is { Length: > 0 } joiner)
        {
            steps.Add(AclPlanning.ForNewObject(ComputerJoinRights.Grant(joiner, input.Options.JoinerName)));
        }

        steps.Add(new PlannedChange
        {
            Action = ChangeAction.ResetPassword,
            Password = _passwords.Generate(ResetComputerAccountOperation.GeneratedLength, ForbiddenTokens(row)),
        });
        steps.Add(new PlannedChange
        {
            Action = ChangeAction.Modify,
            Attributes = [new AttributeChange(Attr.UserAccountControl, AttributeChangeKind.Replace, [Text(CreatedComputerAccountControl)], [Text(EnabledComputerAccountControl)])],
        });
        return steps;
    }
}
