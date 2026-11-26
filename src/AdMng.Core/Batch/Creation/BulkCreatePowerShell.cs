using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.Batch.Creation;

/// <summary>
/// The semantic PowerShell of a created object (docs/dev/phase3-bulk-create.md 2.2, owner B1): <c>New-ADUser</c>,
/// <c>New-ADGroup</c> or <c>New-ADComputer</c> with the Create values, <c>-AccountPassword (Read-Host -AsSecureString …)</c>
/// for a reset (never the password), then <c>Set-AD… -Manager/-ManagedBy</c> and <c>Add-ADGroupMember</c>. The planner sets
/// <see cref="ObjectPlan.PowerShell"/> to it (2.5).
/// </summary>
/// <remarks>
/// The commands are structured (<see cref="PowerShellCommand"/>, module <c>ActiveDirectory</c>): the exporter quotes every
/// literal and writes <c>-OtherAttributes</c> through its attribute entries (hashtable keys with <c>-</c> quoted, secret
/// attributes prompted). Recognised plans are those of <c>BuildPlan</c>: the Create step first, then optionally the reset,
/// the enable / must-change <c>Modify</c> (<c>userAccountControl</c>, <c>pwdLastSet</c>), the manager <c>Modify</c> and
/// <c>AddMember</c> steps; any other step – or a computer with a join right (<see cref="ChangeAction.ModifySecurity"/>, no
/// cmdlet sets that ACE), a contact, an object without its Create step (a retry plan) – gives null, the exporter's generic
/// per-step lines. <c>accountExpires</c> goes to <c>-OtherAttributes</c> as its exact FILETIME (not
/// <c>-AccountExpirationDate</c>, which the script machine's time zone would shift).
/// </remarks>
public static class BulkCreatePowerShell
{
    private const string Module = "ActiveDirectory";

    /// <summary>Create attributes with a parameter of <c>New-ADUser</c> (in the order they are written).</summary>
    private static readonly (string Attribute, string Parameter)[] UserParameters =
    [
        ("sAMAccountName", "SamAccountName"), ("userPrincipalName", "UserPrincipalName"), ("givenName", "GivenName"),
        ("sn", "Surname"), ("initials", "Initials"), ("displayName", "DisplayName"), ("description", "Description"),
        ("mail", "EmailAddress"), ("title", "Title"), ("department", "Department"), ("company", "Company"),
        ("physicalDeliveryOfficeName", "Office"), ("telephoneNumber", "OfficePhone"), ("mobile", "MobilePhone"),
        ("employeeID", "EmployeeID"), ("employeeNumber", "EmployeeNumber"), ("l", "City"), ("streetAddress", "StreetAddress"),
        ("postalCode", "PostalCode"), ("st", "State"), ("c", "Country"), ("wWWHomePage", "HomePage"),
        ("homeDirectory", "HomeDirectory"), ("homeDrive", "HomeDrive"), ("scriptPath", "ScriptPath"), ("profilePath", "ProfilePath"),
    ];

    /// <summary>Create attributes with a parameter of <c>New-ADGroup</c>.</summary>
    private static readonly (string Attribute, string Parameter)[] GroupParameters =
    [
        ("sAMAccountName", "SamAccountName"), ("displayName", "DisplayName"), ("description", "Description"), ("wWWHomePage", "HomePage"),
    ];

    /// <summary>Create attributes with a parameter of <c>New-ADComputer</c>.</summary>
    private static readonly (string Attribute, string Parameter)[] ComputerParameters =
    [
        ("sAMAccountName", "SamAccountName"), ("displayName", "DisplayName"), ("description", "Description"), ("location", "Location"),
        ("dNSHostName", "DNSHostName"),
    ];

    /// <summary>
    /// The commands of <paramref name="objectPlan"/>, or null for the exporter's generic per-step lines (contacts, computers
    /// with a join right, plans without their Create step and plans with steps <c>BuildPlan</c> does not make).
    /// </summary>
    public static PowerShellEquivalent? For(ObjectPlan objectPlan)
    {
        ArgumentNullException.ThrowIfNull(objectPlan);
        if (objectPlan.Kind is not (ObjectKind.User or ObjectKind.Group or ObjectKind.Computer)
            || objectPlan.Changes.Count == 0 || objectPlan.Changes[0].Action != ChangeAction.Create
            || !DistinguishedName.TryParse(objectPlan.DistinguishedName, out var dn) || dn.IsRoot || dn.Parent is not { IsRoot: false } parent)
        {
            return null;
        }

        if (!Classify(objectPlan, out var steps))
        {
            return null;
        }

        var create = objectPlan.Changes[0];
        var rdnType = dn.Leaf.Type;
        var commands = new List<PowerShellCommand>();
        switch (objectPlan.Kind)
        {
            case ObjectKind.User:
                commands.Add(NewUser(dn, parent, create, rdnType, steps));
                break;
            case ObjectKind.Group when NewGroup(dn, parent, create, rdnType) is { } group:
                commands.Add(group);
                break;
            case ObjectKind.Computer:
                commands.Add(NewComputer(dn, parent, create, rdnType, steps));
                break;
            default:
                return null;
        }

        if (steps.Manager is { } manager)
        {
            var (cmdlet, parameter) = objectPlan.Kind switch
            {
                ObjectKind.User => ("Set-ADUser", "Manager"),
                ObjectKind.Group => ("Set-ADGroup", "ManagedBy"),
                _ => ("Set-ADComputer", "ManagedBy"),
            };
            commands.Add(Command(cmdlet,
                PowerShellParameter.Literal("Identity", dn.ToString()),
                PowerShellParameter.Literal(parameter, manager),
                PowerShellParameter.Server));
        }

        foreach (var group in steps.Groups)
        {
            commands.Add(Command("Add-ADGroupMember",
                PowerShellParameter.Literal("Identity", group),
                PowerShellParameter.Literal("Members", dn.ToString()),
                PowerShellParameter.Server));
        }

        return new PowerShellEquivalent(commands);
    }

    private static PowerShellCommand NewUser(DistinguishedName dn, DistinguishedName parent, PlannedChange create, string rdnType, Steps steps)
    {
        var parameters = new List<PowerShellParameter>
        {
            PowerShellParameter.Literal("Name", dn.Name),
            PowerShellParameter.Literal("Path", parent.ToString()),
        };
        AddValues(parameters, create, rdnType, UserParameters);
        if (steps.Reset)
        {
            parameters.Add(PowerShellParameter.SecurePrompt("AccountPassword", "Initial password for " + dn.Name));
        }

        if (steps.Enabled)
        {
            parameters.Add(PowerShellParameter.Boolean("Enabled", true));
        }

        if (steps.MustChange)
        {
            parameters.Add(PowerShellParameter.Boolean("ChangePasswordAtLogon", true));
        }

        parameters.Add(PowerShellParameter.Server);
        return new PowerShellCommand("New-ADUser", parameters) { Module = Module };
    }

    /// <summary><c>New-ADGroup</c>, or null when the Create step has no readable <c>groupType</c> (the cmdlet needs the scope).</summary>
    private static PowerShellCommand? NewGroup(DistinguishedName dn, DistinguishedName parent, PlannedChange create, string rdnType)
    {
        var groupType = create.Attributes.FirstOrDefault(a => Is(a.Attribute, Attr.GroupType))?.NewValues;
        if (groupType is not [var raw] || !int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var type))
        {
            return null;
        }

        var scope = GroupTypes.GetScope(type) switch
        {
            GroupScope.Global => "Global",
            GroupScope.DomainLocal => "DomainLocal",
            GroupScope.Universal => "Universal",
            _ => null,
        };
        if (scope is null)
        {
            return null;
        }

        var parameters = new List<PowerShellParameter>
        {
            PowerShellParameter.Literal("Name", dn.Name),
            PowerShellParameter.Literal("GroupScope", scope),
            PowerShellParameter.Literal("GroupCategory", GroupTypes.IsSecurity(type) ? "Security" : "Distribution"),
            PowerShellParameter.Literal("Path", parent.ToString()),
        };
        AddValues(parameters, create, rdnType, GroupParameters);
        parameters.Add(PowerShellParameter.Server);
        return new PowerShellCommand("New-ADGroup", parameters) { Module = Module };
    }

    /// <summary><c>New-ADComputer</c> without <c>-AccountPassword</c>: the cmdlet sets a random machine password, as the plan does (2.5).</summary>
    private static PowerShellCommand NewComputer(DistinguishedName dn, DistinguishedName parent, PlannedChange create, string rdnType, Steps steps)
    {
        var parameters = new List<PowerShellParameter>
        {
            PowerShellParameter.Literal("Name", dn.Name),
            PowerShellParameter.Literal("Path", parent.ToString()),
        };
        AddValues(parameters, create, rdnType, ComputerParameters);
        if (steps.Enabled)
        {
            parameters.Add(PowerShellParameter.Boolean("Enabled", true));
        }

        parameters.Add(PowerShellParameter.Server);
        return new PowerShellCommand("New-ADComputer", parameters) { Module = Module };
    }

    /// <summary>
    /// The Create values: one value of an attribute with a cmdlet parameter → that parameter (in the parameters' order);
    /// every other value → <c>-OtherAttributes</c>; <c>objectClass</c>, <c>userAccountControl</c>, <c>groupType</c> and the RDN
    /// never (the cmdlet sets them).
    /// </summary>
    private static void AddValues(List<PowerShellParameter> parameters, PlannedChange create, string rdnType, (string Attribute, string Parameter)[] named)
    {
        var values = create.Attributes
            .Where(a => a.NewValues.Count > 0
                && !Is(a.Attribute, Attr.ObjectClass) && !Is(a.Attribute, Attr.UserAccountControl) && !Is(a.Attribute, Attr.GroupType)
                && !Is(a.Attribute, rdnType))
            .ToList();
        foreach (var (attribute, parameter) in named)
        {
            if (values.FirstOrDefault(a => Is(a.Attribute, attribute)) is { NewValues: [var single] } change)
            {
                parameters.Add(PowerShellParameter.Literal(parameter, single));
                values.Remove(change);
            }
        }

        if (values.Count > 0)
        {
            parameters.Add(PowerShellParameter.Table("OtherAttributes", values));
        }
    }

    /// <summary>The steps after the Create step, or false when one of them is not a step of <c>BuildPlan</c>.</summary>
    private static bool Classify(ObjectPlan objectPlan, out Steps steps)
    {
        steps = new Steps();
        var managerAttribute = objectPlan.Kind == ObjectKind.User ? Attr.Manager : Attr.ManagedBy;
        foreach (var change in objectPlan.Changes.Skip(1))
        {
            switch (change.Action)
            {
                case ChangeAction.ResetPassword when objectPlan.Kind is ObjectKind.User or ObjectKind.Computer:
                    steps.Reset = true;
                    break;
                case ChangeAction.AddMember when change.TargetDn is { Length: > 0 } group:
                    steps.Groups.Add(group);
                    break;
                case ChangeAction.Modify when change.Attributes is [{ Kind: AttributeChangeKind.Replace, NewValues: [var dn] } only]
                    && Is(only.Attribute, managerAttribute) && steps.Manager is null:
                    steps.Manager = dn;
                    break;
                case ChangeAction.Modify when change.Attributes.Count > 0 && change.Attributes.All(IsAccountStep):
                    foreach (var attribute in change.Attributes)
                    {
                        if (Is(attribute.Attribute, Attr.PwdLastSet))
                        {
                            steps.MustChange = attribute.NewValues is ["0"];
                        }
                        else if (attribute.NewValues is [var control]
                            && int.TryParse(control, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var flags))
                        {
                            steps.Enabled = (flags & (int)UserAccountControl.AccountDisable) == 0;
                        }
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>A part of the enable / must-change step: <c>userAccountControl</c> or <c>pwdLastSet</c> replaced by one value.</summary>
    private static bool IsAccountStep(AttributeChange change) =>
        change is { Kind: AttributeChangeKind.Replace, NewValues.Count: 1 }
        && (Is(change.Attribute, Attr.UserAccountControl) || Is(change.Attribute, Attr.PwdLastSet));

    private static PowerShellCommand Command(string cmdlet, params PowerShellParameter[] parameters) =>
        new(cmdlet, parameters) { Module = Module };

    private static bool Is(string attribute, string name) => string.Equals(attribute, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>What the steps after the Create step do.</summary>
    private sealed class Steps
    {
        public bool Reset { get; set; }

        public bool Enabled { get; set; }

        public bool MustChange { get; set; }

        public string? Manager { get; set; }

        public List<string> Groups { get; } = [];

        public override string ToString() => $"Steps (reset={Reset}, enabled={Enabled}, mustChange={MustChange}, groups={Groups.Count})";
    }
}
