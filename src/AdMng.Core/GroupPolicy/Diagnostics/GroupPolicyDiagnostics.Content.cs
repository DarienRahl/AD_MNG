using System.Globalization;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>
/// The rules of a GPO's settings (Full): the security template (<c>GptTmpl.inf</c>), the advanced audit policy
/// (<c>audit.csv</c>) and the GPP local groups (<c>Groups.xml</c>), read with the K4 parsers. Setting keys are the
/// normalized keys of <see cref="GroupPolicyReader.Settings"/> (<c>Computer/Security/…</c>).
/// </summary>
public static partial class GroupPolicyDiagnostics
{
    private const string SecurityPrefix = "Computer/Security/";

    /// <summary>The deny-logon user rights.</summary>
    private static readonly HashSet<string> DenyLogonRights = new(StringComparer.OrdinalIgnoreCase)
    {
        "SeDenyNetworkLogonRight", "SeDenyBatchLogonRight", "SeDenyServiceLogonRight", "SeDenyInteractiveLogonRight", "SeDenyRemoteInteractiveLogonRight",
    };

    /// <summary>The sensitive user rights: those of <see cref="LocalRightsCatalog"/> that count on workstations or member servers.</summary>
    private static readonly HashSet<string> SensitiveRights = LocalRightsCatalog.All
        .Where(d => d.Kind == LocalRightKind.UserRight && (d.WorkstationLevel != PrivilegeLevel.None || d.ServerLevel != PrivilegeLevel.None))
        .Select(d => d.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The privileged local groups: those of <see cref="LocalRightsCatalog"/> elevated or more on workstations or member servers.</summary>
    private static readonly HashSet<string> PrivilegedLocalGroups = LocalRightsCatalog.All
        .Where(d => d.Kind == LocalRightKind.LocalGroup && (d.WorkstationLevel >= PrivilegeLevel.Elevated || d.ServerLevel >= PrivilegeLevel.Elevated))
        .Select(d => d.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void ContentRules(Context c, GpoInfo gpo, GpoFolderView view)
    {
        var principals = new PrincipalChecks(c, gpo.Id);
        if (view.Template is { } template)
        {
            AccountPolicyRule(c, gpo, template);
            UserRightsRules(c, gpo, template, principals);
            RestrictedGroupsRules(c, gpo, template, principals);
            SecurityOptionRules(c, gpo, template);
            if (template.Values(SecurityTemplateDocument.EventAudit).Count > 0 && view.Audit?.GetSubcategories().Count > 0)
            {
                // Forcing the subcategories is on by default (Windows Vista and later): only an explicit 0 lets the basic
                // categories win; left undefined, the subcategories win as long as no other policy turns the option off.
                var force = OptionValue(template, "SCENoApplyLegacyAuditPolicy");
                var code = force switch
                {
                    0 => GpoFindingCodes.LegacyAndAdvancedAudit,
                    null => GpoFindingCodes.LegacyAndAdvancedAuditByDefault,
                    _ => null,
                };
                if (code is not null)
                {
                    c.Add(code, gpo.Id, settingKey: OptionKey(SecuritySettingsCatalog.ForceAuditSubcategories));
                }
            }

            RestrictedAndPreferenceRule(c, gpo, template, view.LocalGroups);
        }

        PreferenceGroupsRules(c, gpo, view.LocalGroups, principals);
    }

    private static void AccountPolicyRule(Context c, GpoInfo gpo, SecurityTemplateDocument template)
    {
        if (!c.IsLinked(gpo.Id) || c.IsLinkedToDomainHead(gpo.Id))
        {
            return;
        }

        var key = template.Values(SecurityTemplateDocument.SystemAccess).Select(v => v.Key).FirstOrDefault(GroupPolicyModeler.AccountPolicyNames.Contains) is { } access
            ? SecurityPrefix + SecurityTemplateDocument.SystemAccess + "/" + access
            : template.Values(SecurityTemplateDocument.KerberosPolicy).Select(v => v.Key).FirstOrDefault() is { } kerberos
                ? SecurityPrefix + SecurityTemplateDocument.KerberosPolicy + "/" + kerberos
                : null;
        if (key is not null)
        {
            c.Add(GpoFindingCodes.AccountPolicyBelowDomain, gpo.Id, settingKey: key);
        }
    }

    private static void UserRightsRules(Context c, GpoInfo gpo, SecurityTemplateDocument template, PrincipalChecks principals)
    {
        var broad = GpoPrincipals.BroadSids(c.DomainSid);
        var denied = GpoPrincipals.DenyLogonSids(c.DomainSid);
        foreach (var (right, holders) in template.GetPrivilegeRights())
        {
            var key = SecurityPrefix + SecurityTemplateDocument.PrivilegeRights + "/" + right;
            foreach (var holder in holders)
            {
                principals.Check(key, holder);
                var sid = GpoPrincipals.SidOf(holder, c.DomainSid);
                if (sid is null)
                {
                    continue;
                }

                if (SensitiveRights.Contains(right) && broad.Contains(sid))
                {
                    c.Add(GpoFindingCodes.DangerousRightBroadGroup, gpo.Id, settingKey: key, arguments: [holder]);
                }

                if (DenyLogonRights.Contains(right) && denied.Contains(sid))
                {
                    c.Add(GpoFindingCodes.DenyLogonBroadGroup, gpo.Id, settingKey: key, arguments: [holder]);
                }
            }
        }
    }

    private static void RestrictedGroupsRules(Context c, GpoInfo gpo, SecurityTemplateDocument template, PrincipalChecks principals)
    {
        var broad = GpoPrincipals.BroadSids(c.DomainSid);
        foreach (var entry in template.GetRestrictedGroups())
        {
            var key = RestrictedGroupKey(entry.Group, entry.Relation);
            foreach (var principal in entry.Principals.Prepend(entry.Group))
            {
                principals.Check(key, principal);
            }

            var groupSid = GpoPrincipals.SidOf(entry.Group, c.DomainSid);
            if (entry.Relation == RestrictedGroupRelation.Members)
            {
                if (groupSid is not null && PrivilegedLocalGroups.Contains(groupSid))
                {
                    foreach (var member in entry.Principals.Where(p => GpoPrincipals.SidOf(p, c.DomainSid) is { } sid && broad.Contains(sid)))
                    {
                        c.Add(GpoFindingCodes.BroadGroupInPrivilegedLocalGroup, gpo.Id, settingKey: key, arguments: [member, groupSid]);
                    }
                }

                if (string.Equals(groupSid, GpoPrincipals.Administrators, StringComparison.OrdinalIgnoreCase)
                    && !entry.Principals.Any(p => IsAdministratorOrDomainAdmins(p, c.DomainSid)))
                {
                    c.Add(GpoFindingCodes.RestrictedAdministratorsNoAdmins, gpo.Id, settingKey: key);
                }
            }
            else if (groupSid is not null && broad.Contains(groupSid))
            {
                foreach (var target in entry.Principals)
                {
                    if (GpoPrincipals.SidOf(target, c.DomainSid) is { } local && PrivilegedLocalGroups.Contains(local))
                    {
                        c.Add(GpoFindingCodes.BroadGroupInPrivilegedLocalGroup, gpo.Id, settingKey: key, arguments: [entry.Group, local]);
                    }
                }
            }
        }
    }

    private static void PreferenceGroupsRules(Context c, GpoInfo gpo, IReadOnlyList<GppLocalGroup> groups, PrincipalChecks principals)
    {
        var broad = GpoPrincipals.BroadSids(c.DomainSid);
        foreach (var group in groups.Where(g => !g.Disabled))
        {
            var key = PreferenceGroupKey(group);
            var groupSid = group.GroupSid ?? LocalRightsCatalog.LocalGroupSid(group.GroupName);
            if (group.GroupSid is { } itemSid)
            {
                principals.CheckSid(key, itemSid);
            }

            foreach (var member in group.Members)
            {
                if (member.Sid is { } sid)
                {
                    principals.CheckSid(key, sid);
                }
                else if (member.Name is { } name)
                {
                    principals.Check(key, name);
                }

                var shown = member.Name ?? member.Sid!;
                if (group.Action != GppItemAction.Delete && member.Action == GppMemberAction.Add
                    && groupSid is not null && PrivilegedLocalGroups.Contains(groupSid)
                    && GpoPrincipals.SidOf(member.Sid ?? member.Name, c.DomainSid) is { } memberSid && broad.Contains(memberSid))
                {
                    c.Add(GpoFindingCodes.BroadGroupInPrivilegedLocalGroup, gpo.Id, settingKey: key, arguments: [shown, groupSid]);
                }
            }
        }
    }

    /// <summary>A local group whose members Restricted Groups set (Members form) and GPP changes too: they undo each other at every refresh.</summary>
    private static void RestrictedAndPreferenceRule(Context c, GpoInfo gpo, SecurityTemplateDocument template, IReadOnlyList<GppLocalGroup> groups)
    {
        var preference = groups.Where(g => !g.Disabled)
            .Select(g => g.GroupSid ?? LocalRightsCatalog.LocalGroupSid(g.GroupName) ?? (g.GroupName ?? string.Empty).Trim().ToUpperInvariant())
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in template.GetRestrictedGroups().Where(e => e.Relation == RestrictedGroupRelation.Members))
        {
            var identity = GpoPrincipals.SidOf(entry.Group, c.DomainSid) ?? entry.Group.Trim().ToUpperInvariant();
            if (preference.Contains(identity))
            {
                c.Add(GpoFindingCodes.RestrictedAndPreferenceSameGroup, gpo.Id, settingKey: RestrictedGroupKey(entry.Group, entry.Relation), arguments: [entry.Group]);
            }
        }
    }

    private static void SecurityOptionRules(Context c, GpoInfo gpo, SecurityTemplateDocument template)
    {
        void Flag(string code, string optionId, params string[] arguments) =>
            c.Add(code, gpo.Id, settingKey: OptionKey(Option(optionId)), arguments: arguments);

        // This GPO's value is the one the domain controllers get / the one of the domain's account policy.
        bool WinsOnDomainControllers(string optionId) => WinningOption(c, c.DomainControllersChain, optionId)?.GpoId == gpo.Id;
        bool WinsForTheDomain(string optionId) => WinningOption(c, c.DomainHeadChain, optionId)?.GpoId == gpo.Id;

        if (OptionValue(template, "LmCompatibilityLevel") is { } lm && lm < 3)
        {
            Flag(GpoFindingCodes.LmCompatibilityWeak, "LmCompatibilityLevel", Number(lm));
        }

        if (OptionValue(template, "NoLMHash") == 0)
        {
            Flag(GpoFindingCodes.LmHashStored, "NoLMHash");
        }

        if (OptionValue(template, "ServerRequireSecuritySignature") == 0 && WinsOnDomainControllers("ServerRequireSecuritySignature"))
        {
            Flag(GpoFindingCodes.SmbSigningNotRequired, "ServerRequireSecuritySignature");
        }

        if (OptionValue(template, "LDAPServerIntegrity") is { } ldap && ldap <= 1 && WinsOnDomainControllers("LDAPServerIntegrity"))
        {
            Flag(GpoFindingCodes.LdapSigningNotRequired, "LDAPServerIntegrity", Number(ldap));
        }

        foreach (var (option, weak) in new[] { ("RestrictAnonymousSAM", 0L), ("LSAAnonymousNameLookup", 1L), ("EveryoneIncludesAnonymous", 1L) })
        {
            if (OptionValue(template, option) == weak)
            {
                Flag(GpoFindingCodes.AnonymousAccessAllowed, option, option);
            }
        }

        if (OptionValue(template, "ClearTextPassword") == 1)
        {
            Flag(GpoFindingCodes.ReversibleEncryption, "ClearTextPassword");
        }

        if (OptionValue(template, "EnableGuestAccount") == 1)
        {
            Flag(GpoFindingCodes.GuestEnabled, "EnableGuestAccount");
        }

        if (OptionValue(template, "EnableLUA") == 0)
        {
            Flag(GpoFindingCodes.UacDisabled, "EnableLUA");
        }

        if (OptionValue(template, "WorkstationEnablePlainTextPassword") == 1)
        {
            Flag(GpoFindingCodes.PlainTextSmbPassword, "WorkstationEnablePlainTextPassword");
        }

        var length = OptionValue(template, "MinimumPasswordLength");
        if (length is > 14 && OptionValue(template, SecuritySettingsCatalog.RelaxMinimumPasswordLength.Id) != 1 && !RelaxedWhereTheLengthApplies(c, gpo.Id))
        {
            Flag(GpoFindingCodes.PasswordLengthNeedsRelax, "MinimumPasswordLength", Number(length.Value));
        }

        if (length is < 8 && WinsForTheDomain("MinimumPasswordLength"))
        {
            Flag(GpoFindingCodes.WeakDomainPasswordPolicy, "MinimumPasswordLength", "MinimumPasswordLength", Number(length.Value));
        }

        if (OptionValue(template, "PasswordComplexity") == 0 && WinsForTheDomain("PasswordComplexity"))
        {
            Flag(GpoFindingCodes.WeakDomainPasswordPolicy, "PasswordComplexity", "PasswordComplexity", "0");
        }

        if (OptionValue(template, "LockoutBadCount") == 0 && WinsForTheDomain("LockoutBadCount"))
        {
            Flag(GpoFindingCodes.WeakDomainPasswordPolicy, "LockoutBadCount", "LockoutBadCount", "0");
        }
    }

    /// <summary>
    /// The GPO whose value of a security option wins in <paramref name="chain"/> and that value: the GPO with the highest
    /// precedence (the last applied) whose computer configuration is enabled and whose security template sets the option.
    /// WMI filters and security filtering are assumed to let every GPO apply (as for the conflicts); a GPO whose folder was
    /// not read counts as not setting the option. Null when no GPO of the chain sets it.
    /// </summary>
    private static (Guid GpoId, long Value)? WinningOption(Context c, IReadOnlyList<AppliedGroupPolicy> chain, string optionId)
    {
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (c.Gpo(chain[i].GpoId) is { } gpo && ComputerEnabled(gpo) && c.View(gpo.Id)?.Template is { } template
                && OptionValue(template, optionId) is { } value)
            {
                return (gpo.Id, value);
            }
        }

        return null;
    }

    /// <summary>
    /// "Relax minimum password length limits" (a setting of the computer that checks the password) is in effect wherever the
    /// GPO's minimum length applies: on the domain controllers for a GPO linked to the domain head - they enforce the
    /// domain's account policy - else on the computers of every container whose chain applies the GPO. False when the GPO
    /// applies nowhere.
    /// </summary>
    private static bool RelaxedWhereTheLengthApplies(Context c, Guid gpoId)
    {
        var relax = SecuritySettingsCatalog.RelaxMinimumPasswordLength.Id;
        List<IReadOnlyList<AppliedGroupPolicy>> chains = c.IsLinkedToDomainHead(gpoId)
            ? [c.DomainControllersChain]
            : [.. c.Snapshot.Containers.Where(x => x.Kind != GroupPolicySomKind.Site)
                .Select(x => c.Inheritance.Resolve(x.Dn, siteDn: null))
                .Where(chain => chain.Any(a => a.GpoId == gpoId))];
        return chains.Count > 0 && chains.All(chain => WinningOption(c, chain, relax) is { Value: 1 });
    }

    /// <summary>The Administrator account (any domain's RID 500), Domain Admins or Enterprise Admins, by SID or by name.</summary>
    private static bool IsAdministratorOrDomainAdmins(string principal, string? domainSid)
    {
        if (GpoPrincipals.SidOf(principal, domainSid) is { } sid && sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
        {
            var rid = sid[(sid.LastIndexOf('-') + 1)..];
            return rid is "500" or "512" or "519";
        }

        var name = principal[(principal.LastIndexOf('\\') + 1)..].Trim();
        return name.Equals("Administrator", StringComparison.OrdinalIgnoreCase) || name.Equals("Domain Admins", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Administratorzy domeny", StringComparison.OrdinalIgnoreCase) || name.Equals("Enterprise Admins", StringComparison.OrdinalIgnoreCase);
    }

    private static SecurityOptionDefinition Option(string id) => SecuritySettingsCatalog.Options.First(o => o.Id == id);

    /// <summary>The number an option of the catalog has in the template (a registry value's data after <c>&lt;type&gt;,</c>), null when not set.</summary>
    private static long? OptionValue(SecurityTemplateDocument template, string optionId)
    {
        var option = Option(optionId);
        if (template.Get(option.Section, option.Key) is not { } raw)
        {
            return null;
        }

        var text = raw.Trim();
        if (option.Location == SecurityOptionLocation.RegistryValue)
        {
            var comma = text.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0)
            {
                return null;
            }

            text = text[(comma + 1)..].Trim();
        }

        return long.TryParse(text.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>The normalized key of an option: <c>Computer/Security/System Access/…</c> or <c>Computer/Security/Registry Values/HKLM\…!Name</c>.</summary>
    private static string OptionKey(SecurityOptionDefinition option)
    {
        if (option.Location != SecurityOptionLocation.RegistryValue)
        {
            return SecurityPrefix + option.Section + "/" + option.Key;
        }

        var path = RegistryFormat.FromSecEditPath(option.Key);
        var separator = path.LastIndexOf('\\');
        return SecurityPrefix + SecurityTemplateDocument.RegistryValues + "/" + (separator < 0 ? path : path[..separator]) + "!" + (separator < 0 ? string.Empty : path[(separator + 1)..]);
    }

    private static string RestrictedGroupKey(string group, RestrictedGroupRelation relation) =>
        SecurityPrefix + SecurityTemplateDocument.GroupMembership + "/" + group + "/" + (relation == RestrictedGroupRelation.Members ? "Members" : "MemberOf");

    private static string PreferenceGroupKey(GppLocalGroup group) =>
        group.Uid is { Length: > 0 } uid ? "Computer/Preferences/Groups/" + uid.Trim() + "/Members" : "Computer/Preferences/Groups";

    /// <summary>A principal written as a name (<c>TypedPrincipalName</c>) or a domain SID that resolves to nothing (<c>UnresolvedPrincipal</c>).</summary>
    private sealed class PrincipalChecks(Context c, Guid gpoId)
    {
        private readonly bool _resolved = c.Evaluates(GpoRuleGroup.Principals);

        public void Check(string key, string principal)
        {
            if (string.IsNullOrWhiteSpace(principal))
            {
                return;
            }

            if (SecurityTemplate.IsSid(principal))
            {
                CheckSid(key, principal);
            }
            else
            {
                c.Add(GpoFindingCodes.TypedPrincipalName, gpoId, settingKey: key, arguments: [principal]);
            }
        }

        public void CheckSid(string key, string sid)
        {
            if (_resolved && GpoPrincipals.IsUnresolved(sid.ToUpperInvariant(), c.DomainSid, c.Snapshot.ExistingSids!))
            {
                c.Add(GpoFindingCodes.UnresolvedPrincipal, gpoId, settingKey: key, arguments: [sid.ToUpperInvariant()]);
            }
        }
    }
}
