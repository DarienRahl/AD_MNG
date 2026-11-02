using System.Buffers.Binary;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.Ldap;

namespace AdMng.Core.GroupPolicy.Modeling;

/// <summary>
/// Group Policy modelling (K5, design note docs/dev/k-computers-and-gpo.md section 18): which GPOs a computer and a user
/// get, in which order, and which setting wins – computed from the directory and the GPOs' folders without touching the
/// client, like GPMC's Group Policy Modeling. Pure: the caller reads the input (<see cref="GroupPolicyModelReader"/>) and
/// the folders of <see cref="GposToRead"/>.
/// <list type="bullet">
/// <item>The chain of each part comes from <see cref="GroupPolicyInheritance.Explain"/> ([MS-GPOL] 3.2.5.1): site,
/// domain, OUs; disabled links, blocked links and duplicates are listed with the reason. Every link that would apply is
/// then checked: the GPO exists, its configuration of the part is enabled and not empty (version 0), the token has Apply
/// Group Policy (and no deny), for the user part the computer may read the GPO (MS16-072), and its WMI filter is assumed
/// true or false. Precedence 1 = applied last.</item>
/// <item>Loopback (user part, with a computer): the winning <c>UserPolicyMode</c> of the computer's GPOs, or the
/// operator's choice. Replace takes the computer's chain; merge appends it to the user's.</item>
/// <item>Administrative template policies: each applied GPO's Registry.pol of the part read by <see cref="PolicyEngine"/>;
/// the highest-precedence GPO that configures a policy wins (GPMC's reporting rule). Values no template describes and
/// every other normalized setting: per key the highest precedence wins; scripts are cumulative; on a DC the account
/// policy comes only from GPOs linked to the domain head.</item>
/// </list>
/// </summary>
public static class GroupPolicyModeler
{
    /// <summary>The key of the loopback policy (<c>HKLM\…</c>).</summary>
    public const string LoopbackKey = @"Software\Policies\Microsoft\Windows\System";

    /// <summary>The value of the loopback policy: 1 merge, 2 replace.</summary>
    public const string LoopbackValueName = "UserPolicyMode";

    /// <summary>
    /// The account policy names of <c>[System Access]</c> (password and lockout policy, forced logoff); with
    /// <c>[Kerberos Policy]</c> they apply to domain accounts only from GPOs linked to the domain head.
    /// </summary>
    public static IReadOnlySet<string> AccountPolicyNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MinimumPasswordAge", "MaximumPasswordAge", "MinimumPasswordLength", "PasswordComplexity", "PasswordHistorySize",
        "ClearTextPassword", "LockoutBadCount", "ResetLockoutCount", "LockoutDuration", "RequireLogonToChangePassword",
        "ForceLogoffWhenHourExpire",
    };

    private const string RegistryPolName = "Registry.pol";
    private const uint RegDword = 4;

    /// <summary>
    /// The GPOs whose folders the model may need: every existing GPO of the computer's and the user's chain (the
    /// computer's chain also feeds the user part under loopback).
    /// </summary>
    public static IReadOnlyCollection<Guid> GposToRead(GroupPolicyModelInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var inheritance = new GroupPolicyInheritance(input.DomainDn, input.Containers);
        var ids = new HashSet<Guid>();
        foreach (var target in new[] { input.Computer, input.User })
        {
            if (target is not null)
            {
                ids.UnionWith(inheritance.Resolve(target.ContainerDn, input.SiteDn).Where(a => a.Link.IsInDomain(input.DomainDn)).Select(a => a.GpoId));
            }
        }

        return [.. ids.Where(input.Gpos.ContainsKey).Order()];
    }

    /// <summary>The model.</summary>
    /// <param name="input">The directory's GPOs, containers and targets.</param>
    /// <param name="contents">The folders of (at least) <see cref="GposToRead"/>; a missing GPO counts as unreadable.</param>
    public static GroupPolicyModel Build(GroupPolicyModelInput input, IReadOnlyDictionary<Guid, ModelingGpoContent> contents)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(contents);
        if (input.Computer is null && input.User is null)
        {
            throw new ArgumentException("A model needs a computer or a user.", nameof(input));
        }

        var context = new Context(input, contents);
        ModeledPart? computer = null;
        if (input.Computer is { } computerTarget)
        {
            var chain = context.Inheritance.Explain(computerTarget.ContainerDn, input.SiteDn);
            computer = context.Part(ModelingPart.Computer, Links(chain, loopback: false), computerTarget, computerToken: null, warnings: []);
        }

        var (mode, source, setBy) = Loopback(input, computer, contents);
        ModeledPart? user = null;
        if (input.User is { } userTarget)
        {
            var warnings = new List<ModelingWarning>();
            if (input.Computer is null)
            {
                warnings.Add(new ModelingWarning(ModelingWarningKind.NoComputer));
            }

            var own = Links(context.Inheritance.Explain(userTarget.ContainerDn, input.SiteDn), loopback: false);
            var links = mode switch
            {
                LoopbackMode.Replace => Links(context.Inheritance.Explain(input.Computer!.ContainerDn, input.SiteDn), loopback: true),
                LoopbackMode.Merge => Merge(own, Links(context.Inheritance.Explain(input.Computer!.ContainerDn, input.SiteDn), loopback: true)),
                _ => own,
            };
            user = context.Part(ModelingPart.User, links, userTarget, input.Computer?.Token, warnings);
        }

        return new GroupPolicyModel { Computer = computer, User = user, Loopback = mode, LoopbackSource = source, LoopbackSetBy = setBy };
    }

    // ---- Chain -----------------------------------------------------------------------------------------------------------

    /// <summary>The links of a part: those that would apply (application order) and those left out of the chain.</summary>
    private sealed record PartLinks(IReadOnlyList<(AppliedGroupPolicy Link, bool Loopback)> Applied, IReadOnlyList<(ExcludedGroupPolicyLink Link, bool Loopback)> Excluded);

    private static PartLinks Links(GroupPolicyChain chain, bool loopback) =>
        new([.. chain.Applied.Select(a => (a, loopback))], [.. chain.Excluded.Select(e => (e, loopback))]);

    /// <summary>Loopback merge: the user's links, then the computer's; a GPO in both counts at its last position.</summary>
    private static PartLinks Merge(PartLinks user, PartLinks computer)
    {
        var all = user.Applied.Concat(computer.Applied).ToList();
        var seen = new HashSet<DistinguishedName>();
        var applied = new List<(AppliedGroupPolicy, bool)>();
        var excluded = user.Excluded.Concat(computer.Excluded).ToList();
        for (var i = all.Count - 1; i >= 0; i--)
        {
            if (seen.Add(DistinguishedName.Parse(all[i].Link.Link.GpoDn)))
            {
                applied.Add(all[i]);
            }
            else
            {
                excluded.Add((new ExcludedGroupPolicyLink(all[i].Link.GpoId, all[i].Link.Link, all[i].Link.Container, GroupPolicyLinkExclusion.Duplicate), all[i].Loopback));
            }
        }

        applied.Reverse();
        return new PartLinks(applied, excluded);
    }

    private static (LoopbackMode Mode, LoopbackSource Source, ModeledSource? SetBy) Loopback(
        GroupPolicyModelInput input, ModeledPart? computer, IReadOnlyDictionary<Guid, ModelingGpoContent> contents)
    {
        if (input.Computer is null || input.User is null)
        {
            return (LoopbackMode.None, LoopbackSource.NotApplicable, null);
        }

        switch (input.Options.Loopback)
        {
            case LoopbackChoice.Off:
                return (LoopbackMode.None, LoopbackSource.Operator, null);
            case LoopbackChoice.Merge:
                return (LoopbackMode.Merge, LoopbackSource.Operator, null);
            case LoopbackChoice.Replace:
                return (LoopbackMode.Replace, LoopbackSource.Operator, null);
        }

        // The highest-precedence computer GPO that sets or deletes UserPolicyMode decides.
        foreach (var gpo in computer?.Applied ?? [])
        {
            if (!contents.TryGetValue(gpo.GpoId, out var content) || content.MachineRegistry is not { } registry)
            {
                continue;
            }

            var state = registry.View.Get(LoopbackKey, LoopbackValueName);
            if (state.Kind == RegistryValueStateKind.Missing)
            {
                continue;
            }

            var setBy = new ModeledSource(gpo.GpoId, gpo.Name, gpo.Precedence ?? 0);
            var mode = state is { IsSet: true, Entry: { Type: RegDword, Data.Length: >= 4 } entry }
                ? BinaryPrimitives.ReadUInt32LittleEndian(entry.Data) switch
                {
                    1 => LoopbackMode.Merge,
                    2 => LoopbackMode.Replace,
                    _ => LoopbackMode.None,
                }
                : LoopbackMode.None;
            return (mode, LoopbackSource.Settings, setBy);
        }

        return (LoopbackMode.None, LoopbackSource.NotConfigured, null);
    }

    // ---- One part ----------------------------------------------------------------------------------------------------------

    private sealed class Context(GroupPolicyModelInput input, IReadOnlyDictionary<Guid, ModelingGpoContent> contents)
    {
        private PolicyIndex? _policies;

        public GroupPolicyInheritance Inheritance { get; } = new(input.DomainDn, input.Containers);

        private PolicyIndex Policies => _policies ??= new PolicyIndex(input.Definitions);

        public ModeledPart Part(ModelingPart part, PartLinks links, ModelingTarget target, IReadOnlySet<string>? computerToken, List<ModelingWarning> warnings)
        {
            if (input.SiteDn is null)
            {
                warnings.Add(new ModelingWarning(ModelingWarningKind.NoSite));
            }

            if (target.TokenIncomplete)
            {
                warnings.Add(new ModelingWarning(ModelingWarningKind.TokenIncomplete));
            }

            // Outcomes in application order; precedence counts the applying GPOs from the last one.
            var evaluated = links.Applied
                .Select(l => (l.Link, l.Loopback, Local: l.Link.Link.IsInDomain(input.DomainDn)))
                .Select(l => (l.Link, l.Loopback, Gpo: l.Local ? input.Gpos.GetValueOrDefault(l.Link.GpoId) : null, l.Local))
                .Select(l => (l.Link, l.Loopback, l.Gpo,
                    Outcome: l.Local ? Outcome(part, l.Gpo, target.Token, computerToken, input.Options.WmiFilters) : ModeledGpoOutcome.OtherDomain))
                .ToList();
            var applying = evaluated.Count(e => e.Outcome is ModeledGpoOutcome.Applied or ModeledGpoOutcome.AppliedIfWmiFilter);
            var precedence = applying;
            var rows = new List<ModeledGpo>();
            var appliedInOrder = new List<ModeledGpo>();
            foreach (var (link, loopback, gpo, outcome) in evaluated)
            {
                var applies = outcome is ModeledGpoOutcome.Applied or ModeledGpoOutcome.AppliedIfWmiFilter;
                var row = new ModeledGpo
                {
                    GpoId = link.GpoId,
                    Name = gpo?.DisplayName ?? SysvolPaths.FolderName(link.GpoId),
                    Container = link.Container,
                    Link = link.Link,
                    Outcome = outcome,
                    Precedence = applies ? precedence-- : null,
                    WmiFilterName = gpo?.WmiFilterName,
                    FromLoopback = loopback,
                };
                rows.Add(row);
                if (applies)
                {
                    appliedInOrder.Add(row);
                }
            }

            var gpos = rows.Where(r => r.Applies).OrderBy(r => r.Precedence)
                .Concat(rows.Where(r => !r.Applies))
                .Concat(links.Excluded.Select(e =>
                {
                    var gpo = e.Link.Link.IsInDomain(input.DomainDn) ? input.Gpos.GetValueOrDefault(e.Link.GpoId) : null;
                    return new ModeledGpo
                    {
                        GpoId = e.Link.GpoId,
                        Name = gpo?.DisplayName ?? SysvolPaths.FolderName(e.Link.GpoId),
                        Container = e.Link.Container,
                        Link = e.Link.Link,
                        Outcome = e.Link.Reason switch
                        {
                            GroupPolicyLinkExclusion.LinkDisabled => ModeledGpoOutcome.LinkDisabled,
                            GroupPolicyLinkExclusion.Blocked => ModeledGpoOutcome.Blocked,
                            _ => ModeledGpoOutcome.Duplicate,
                        },
                        BlockedBy = e.Link.BlockedBy,
                        WmiFilterName = gpo?.WmiFilterName,
                        FromLoopback = e.Loopback,
                    };
                }))
                .ToList();

            var (policies, settings) = Settings(part, appliedInOrder, target, warnings);
            return new ModeledPart { Part = part, Gpos = gpos, Policies = policies, Settings = settings, Warnings = warnings };
        }

        private static ModeledGpoOutcome Outcome(ModelingPart part, ModelingGpo? gpo, IReadOnlySet<string> token, IReadOnlySet<string>? computerToken, WmiFilterAssumption wmi)
        {
            if (gpo is null)
            {
                return ModeledGpoOutcome.NotFound;
            }

            if (gpo.IsDisabled(part))
            {
                return ModeledGpoOutcome.PartDisabled;
            }

            if (gpo.IsEmpty(part))
            {
                return ModeledGpoOutcome.Empty;
            }

            // Security filtering: the account needs Read and Apply Group Policy (Read when the filter knows the readers).
            if (gpo.SecurityFilter is { } filter && (!filter.Applies(token) || (filter.KnowsReaders && !filter.CanRead(token))))
            {
                return ModeledGpoOutcome.SecurityDenied;
            }

            if (part == ModelingPart.User && computerToken is not null && gpo.SecurityFilter is { KnowsReaders: true } read && !read.CanRead(computerToken))
            {
                return ModeledGpoOutcome.ComputerCannotRead;
            }

            if (gpo.HasWmiFilter)
            {
                return wmi == WmiFilterAssumption.AssumeTrue ? ModeledGpoOutcome.AppliedIfWmiFilter : ModeledGpoOutcome.WmiFilterFalse;
            }

            return ModeledGpoOutcome.Applied;
        }

        // ---- Settings --------------------------------------------------------------------------------------------------

        private (List<ModeledPolicy> Policies, List<ModeledSetting> Settings) Settings(
            ModelingPart part, IReadOnlyList<ModeledGpo> appliedInOrder, ModelingTarget target, List<ModelingWarning> warnings)
        {
            var prefix = part == ModelingPart.Computer ? "Computer/" : "User/";
            var folder = part == ModelingPart.Computer ? "Machine/" : "User/";
            var registryPath = folder + RegistryPolName;
            var policyClass = part == ModelingPart.Computer ? PolicyClass.Machine : PolicyClass.User;
            var hasDefinitions = input.Definitions.PolicyCount > 0;
            var policySources = new Dictionary<string, List<ModeledPolicySource>>(StringComparer.OrdinalIgnoreCase);
            var settingSources = new Dictionary<string, List<ModeledSettingSource>>(StringComparer.OrdinalIgnoreCase);
            var categories = new Dictionary<string, GpoSettingCategory>(StringComparer.OrdinalIgnoreCase);
            var registryWithoutTemplates = false;
            var belowDomainWarned = new HashSet<Guid>();
            foreach (var gpo in appliedInOrder)
            {
                var source = new ModeledSource(gpo.GpoId, gpo.Name, gpo.Precedence ?? 0);
                if (!contents.TryGetValue(gpo.GpoId, out var content) || content.Unreadable)
                {
                    warnings.Add(new ModelingWarning(ModelingWarningKind.ContentUnreadable, gpo.GpoId, gpo.Name));
                    continue;
                }

                foreach (var marker in content.Settings.Where(s => s.Kind == GpoSettingKind.Unreadable && s.Source is { } path && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add(new ModelingWarning(ModelingWarningKind.FileUnreadable, gpo.GpoId, gpo.Name, marker.Source));
                }

                // Administrative template policies of this GPO's Registry.pol, and the values they cover: a value only a
                // policy of the other class or an unconfigured one describes stays an extra registry value (as in GPMC).
                var covered = new RegistryCoverage();
                if (content.Registry(part) is { } registry)
                {
                    if (hasDefinitions)
                    {
                        foreach (var definition in Policies.Candidates(registry, policyClass))
                        {
                            var setting = PolicyEngine.Read(definition.Policy, registry.View, Policies.Ownership.OwnedNames);
                            if (setting.State != PolicyState.NotConfigured)
                            {
                                Add(policySources, definition.Id, new ModeledPolicySource(source, setting));
                                covered.Add(definition.Policy);
                            }
                        }
                    }
                    else if (registry.Entries.Count > 0)
                    {
                        registryWithoutTemplates = true;
                    }
                }

                // Item-level targeting of preference items: their keys up to the item.
                var targeted = content.Settings
                    .Where(s => s.Key.EndsWith("/Filters", StringComparison.OrdinalIgnoreCase) && PreferenceItem(s.Key) is not null)
                    .Select(s => PreferenceItem(s.Key)!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var setting in content.Settings)
                {
                    if (!setting.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || setting.Kind == GpoSettingKind.Unreadable || setting.Category == GpoSettingCategory.Comments)
                    {
                        continue;
                    }

                    // Registry.pol values: the policies above show what they cover.
                    if (setting.Source is { } path && path.Equals(registryPath, StringComparison.OrdinalIgnoreCase)
                        && covered.Covers(setting.Key))
                    {
                        continue;
                    }

                    if (target.IsDomainController && part == ModelingPart.Computer && IsAccountPolicy(setting.Key)
                        && gpo.Container.Kind != GroupPolicySomKind.Domain)
                    {
                        if (belowDomainWarned.Add(gpo.GpoId))
                        {
                            warnings.Add(new ModelingWarning(ModelingWarningKind.AccountPolicyBelowDomain, gpo.GpoId, gpo.Name));
                        }

                        continue;
                    }

                    categories.TryAdd(setting.Key, setting.Category);
                    var conditional = PreferenceItem(setting.Key) is { } item && targeted.Contains(item);
                    Add(settingSources, setting.Key, new ModeledSettingSource(source, setting, conditional));
                }
            }

            if (registryWithoutTemplates)
            {
                warnings.Add(new ModelingWarning(ModelingWarningKind.NoDefinitions));
            }

            var policies = policySources
                .Select(p => new ModeledPolicy(input.Definitions.Find(p.Key)!, p.Value[^1], [.. Enumerable.Reverse(p.Value).Skip(1)]))
                .OrderBy(p => p.Definition.Category is { } category ? string.Join(" / ", category.PathNames()) : string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(p => p.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var settings = settingSources
                .Select(s => new ModeledSetting(s.Key, categories[s.Key], s.Value[^1], [.. Enumerable.Reverse(s.Value).Skip(1)], categories[s.Key] == GpoSettingCategory.Scripts))
                .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return (policies, settings);
        }

        private static void Add<T>(Dictionary<string, List<T>> sources, string key, T source)
        {
            if (!sources.TryGetValue(key, out var list))
            {
                list = [];
                sources[key] = list;
            }

            list.Add(source);
        }
    }

    /// <summary>The registry values the configured policies of one GPO's <c>Registry.pol</c> own (their values and list keys).</summary>
    private sealed class RegistryCoverage
    {
        private readonly Dictionary<string, HashSet<string>> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _listKeys = new(StringComparer.OrdinalIgnoreCase);

        public void Add(AdmxPolicy policy)
        {
            foreach (var (key, names) in PolicyEngine.OwnValueNames(policy))
            {
                var normalized = RegistryPolicyDocument.NormalizeKey(key);
                if (!_names.TryGetValue(normalized, out var existing))
                {
                    existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _names[normalized] = existing;
                }

                existing.UnionWith(names);
            }

            _listKeys.UnionWith(PolicyEngine.ListKeys(policy).Select(RegistryPolicyDocument.NormalizeKey));
        }

        /// <summary>
        /// A Registry.pol setting key (<c>Computer/Registry/HKLM\key!value</c>) whose value a configured policy owns. The key
        /// path ends at the first <c>!</c> (template key names have none; a value name may).
        /// </summary>
        public bool Covers(string settingKey)
        {
            if (_names.Count == 0 && _listKeys.Count == 0)
            {
                return false;
            }

            var slash = settingKey.IndexOf("/Registry/", StringComparison.OrdinalIgnoreCase);
            var path = slash < 0 ? null : settingKey[(slash + "/Registry/".Length)..];
            var bang = path?.IndexOf('!', StringComparison.Ordinal) ?? -1;
            if (path is null || bang < 0)
            {
                return false; // a deleted key: not a value of a template
            }

            var name = path[(bang + 1)..];
            var hiveAndKey = path[..bang];
            var firstSlash = hiveAndKey.IndexOf('\\', StringComparison.Ordinal);
            var key = RegistryPolicyDocument.NormalizeKey(firstSlash < 0 ? string.Empty : hiveAndKey[(firstSlash + 1)..]);
            if (_listKeys.Contains(key))
            {
                return true;
            }

            return name is not ("*" or "**delvals.") && _names.TryGetValue(key, out var names) && names.Contains(name == "(Default)" ? string.Empty : name);
        }
    }

    /// <summary>The part of a preference key up to its item (<c>Computer/Preferences/Groups/{uid}</c>); null for other keys.</summary>
    private static string? PreferenceItem(string key)
    {
        var parts = key.Split('/', 5);
        if (parts.Length < 4 || !parts[1].Equals("Preferences", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var item = parts[3];
        var at = item.IndexOf('@', StringComparison.Ordinal);
        return string.Join('/', parts[0], parts[1], parts[2], at < 0 ? item : item[..at]);
    }

    /// <summary>A password, lockout or Kerberos policy key of the security template.</summary>
    private static bool IsAccountPolicy(string key)
    {
        const string SystemAccess = "Computer/Security/System Access/";
        const string Kerberos = "Computer/Security/Kerberos Policy/";
        return key.StartsWith(Kerberos, StringComparison.OrdinalIgnoreCase)
            || (key.StartsWith(SystemAccess, StringComparison.OrdinalIgnoreCase) && AccountPolicyNames.Contains(key[SystemAccess.Length..]));
    }

    /// <summary>The policies of a definition set by the registry keys they own (built once per model).</summary>
    private sealed class PolicyIndex
    {
        private readonly Dictionary<string, List<PolicyDefinition>> _byKey = new(StringComparer.OrdinalIgnoreCase);

        public PolicyIndex(PolicyDefinitionSet set)
        {
            Ownership = PolicyOwnershipIndex.Build(set);
            foreach (var definition in set.Policies)
            {
                var keys = PolicyEngine.OwnValueNames(definition.Policy).Keys.Concat(PolicyEngine.ListKeys(definition.Policy))
                    .Select(RegistryPolicyDocument.NormalizeKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var key in keys)
                {
                    if (!_byKey.TryGetValue(key, out var list))
                    {
                        list = [];
                        _byKey[key] = list;
                    }

                    list.Add(definition);
                }
            }
        }

        public PolicyOwnershipIndex Ownership { get; }

        /// <summary>The policies of <paramref name="policyClass"/> that own a key of <paramref name="registry"/> (each once).</summary>
        public IEnumerable<PolicyDefinition> Candidates(RegistryPolicyDocument registry, PolicyClass policyClass)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in registry.Entries.Select(e => RegistryPolicyDocument.NormalizeKey(e.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_byKey.TryGetValue(key, out var definitions))
                {
                    continue;
                }

                foreach (var definition in definitions)
                {
                    if (definition.Policy.AppliesTo(policyClass) && seen.Add(definition.Id))
                    {
                        yield return definition;
                    }
                }
            }
        }
    }
}
