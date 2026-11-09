using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;

namespace AdMng.Core.Tests.GroupPolicy.Copy;

/// <summary>
/// Setting units built by hand for the K7 planner tests (W2 does not wait for W1's <c>GpoSettingUnits.Of</c>): the 21.2
/// grammar for the kinds the writers cover, simplified where the planners do not look (no value hashing, a small
/// well-known name table, accounts from <see cref="Accounts"/>). The real layer is tested end to end in
/// <c>CopySettingsEndToEndTests</c> once wave 1 is merged.
/// </summary>
public sealed class HandUnits
{
    private static readonly Dictionary<string, string> WellKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Administrators"] = "S-1-5-32-544",
        ["Administratorzy"] = "S-1-5-32-544",
        ["BUILTIN\\Administrators"] = "S-1-5-32-544",
        ["Remote Desktop Users"] = "S-1-5-32-555",
        ["Użytkownicy pulpitu zdalnego"] = "S-1-5-32-555",
    };

    /// <summary>Names the fake resolver knows (<c>DOMAIN\name</c> → SID).</summary>
    public Dictionary<string, string> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many resolver calls were made (one batch per plan).</summary>
    public int ResolveCalls { get; private set; }

    /// <summary>Files whose settings "overflowed" (the hand ReadSettings reports them).</summary>
    public HashSet<string> Overflowed { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal GpoUnitSource Source => new(ReadSettings, PrincipalNames, ResolveAsync, Of, SameValue, KnownSid);

    public GpoSettingsRead ReadSettings(GpoFolderContent content) =>
        new(GroupPolicyReader.Settings(content), [.. content.Files.Keys.Where(Overflowed.Contains)]);

    public static string? KnownSid(string? principal, string? domainSid)
    {
        if (principal is null)
        {
            return null;
        }

        var text = SecurityTemplate.Principal(principal);
        return SecurityTemplate.IsSid(text) ? text.ToUpperInvariant() : WellKnown.TryGetValue(text, out var sid) ? sid : null;
    }

    public IReadOnlyCollection<string> PrincipalNames(IReadOnlyList<GpoSetting> settings, string? domainSid) =>
        [.. settings.Where(s => GpoSettingKey.Parse(s.Key).Area is GpoSettingArea.UserRight or GpoSettingArea.RestrictedGroup)
            .SelectMany(s => GpoSettingKey.Parse(s.Key).Area == GpoSettingArea.RestrictedGroup ? [.. s.Elements(), GpoSettingKey.Parse(s.Key).Name!] : s.Elements())
            .Where(p => p.Length > 0 && KnownSid(p, domainSid) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    public Task<IReadOnlyList<GpoPrincipal>> ResolveAsync(IDirectorySession session, IEnumerable<string> names, string? domainSid, CancellationToken cancellationToken)
    {
        ResolveCalls++;
        return Task.FromResult<IReadOnlyList<GpoPrincipal>>([.. names.Select(n => Accounts.TryGetValue(n, out var sid)
            ? new GpoPrincipal(n, sid, GpoPrincipalKind.Account)
            : new GpoPrincipal(n, null, GpoPrincipalKind.Unresolved))]);
    }

    public static bool SameValue(GpoSettingUnit a, GpoSettingUnit b)
    {
        if (a.Comparability != GpoUnitComparability.Comparable || b.Comparability != GpoUnitComparability.Comparable || a.Disabled || b.Disabled)
        {
            return false;
        }

        return a.Kind == GpoSettingUnitKind.AdministrativeTemplate
            ? a.Policy is not null && a.Policy.Equals(b.Policy)
            : string.Equals(a.ValueFingerprint, b.ValueFingerprint, StringComparison.Ordinal);
    }

    public IReadOnlyList<GpoSettingUnit> Of(GpoFolderContent content, GpoUnitContext context)
    {
        var settings = GroupPolicyReader.Settings(content);
        var raw = new List<GpoSettingUnit>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (context.Definitions is { } set)
        {
            foreach (var (path, scope, policyClass) in new[] { (GroupPolicyPlanner.MachineRegistryPolicyPath, GpoScope.Computer, PolicyClass.Machine), (GroupPolicyPlanner.UserRegistryPolicyPath, GpoScope.User, PolicyClass.User) })
            {
                if (!content.Files.TryGetValue(path, out var bytes))
                {
                    continue;
                }

                var configured = AdministrativeTemplatePolicies.Configured(RegistryPolicyDocument.Parse(bytes), set, policyClass);
                foreach (var policy in configured.Policies)
                {
                    var owned = settings.Where(s => GpoSettingKey.Parse(s.Key).Area == GpoSettingArea.PolicyRegistry && GpoSettingKey.Parse(s.Key).Scope == scope && configured.Covers(s.Key)).ToList();
                    covered.UnionWith(owned.Select(s => s.Key));
                    raw.Add(new GpoSettingUnit
                    {
                        Id = string.Empty,
                        BaseId = "P|" + policyClass + "|" + policy.Definition.Id,
                        Kind = GpoSettingUnitKind.AdministrativeTemplate,
                        Scope = scope,
                        PolicyId = policy.Definition.Id,
                        PolicyClass = policyClass,
                        Policy = policy.Setting,
                        Settings = owned,
                        Source = path,
                    });
                }
            }
        }

        var byItem = new Dictionary<string, List<GpoSetting>>(StringComparer.OrdinalIgnoreCase);
        var byService = new Dictionary<string, List<GpoSetting>>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings.Where(s => !covered.Contains(s.Key)))
        {
            var info = GpoSettingKey.Parse(setting.Key);
            switch (info.Area)
            {
                case GpoSettingArea.Preference when info.ItemKey is { } item:
                    (byItem.TryGetValue(item, out var list) ? list : byItem[item] = []).Add(setting);
                    break;
                case GpoSettingArea.Service:
                    (byService.TryGetValue(info.Name!, out var services) ? services : byService[info.Name!] = []).Add(setting);
                    break;
                default:
                    raw.Add(Single(setting, info, context));
                    break;
            }
        }

        foreach (var (name, list) in byService)
        {
            raw.Add(new GpoSettingUnit
            {
                Id = string.Empty,
                BaseId = "O|Computer/Security/Services/" + name,
                Kind = GpoSettingUnitKind.Service,
                Scope = GpoScope.Computer,
                Settings = list,
                Source = SecurityTemplate.RelativePath,
                ValueFingerprint = Fingerprint(list, "Computer/Security/Services/" + name),
                Comparability = Masked(list) ? GpoUnitComparability.Masked : GpoUnitComparability.Comparable,
                Refusal = Masked(list) ? GpoUnitRefusal.Secret : null,
            });
        }

        foreach (var (item, list) in byItem)
        {
            raw.Add(Preference(content, item, list));
        }

        // Occurrences in file order: BaseId, BaseId#2 …
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return [.. raw.Select(u =>
        {
            var n = counts[u.BaseId] = counts.GetValueOrDefault(u.BaseId) + 1;
            return u with { Id = n == 1 ? u.BaseId : u.BaseId + "#" + n.ToString(CultureInfo.InvariantCulture), Occurrence = n };
        })];
    }

    private GpoSettingUnit Single(GpoSetting setting, GpoSettingKeyInfo info, GpoUnitContext context)
    {
        string? Sid(string p) => KnownSid(p, context.DomainSid) ?? (context.SidsByName is { } map && map.TryGetValue(SecurityTemplate.Principal(p), out var s) ? s : null);
        string Id(string p) => (Sid(p) ?? SecurityTemplate.Principal(p)).ToUpperInvariant();
        var (kind, refusal) = info.Area switch
        {
            GpoSettingArea.PolicyRegistry => (info.ValueName is null || info.ValueName == RegistryPolicyDocument.DeleteAllValues ? GpoSettingUnitKind.RegistryKey : GpoSettingUnitKind.RegistryValue, (GpoUnitRefusal?)null),
            GpoSettingArea.SystemAccess or GpoSettingArea.KerberosPolicy or GpoSettingArea.EventAudit or GpoSettingArea.EventLog or GpoSettingArea.SecuritySection => (GpoSettingUnitKind.SecurityValue, null),
            GpoSettingArea.UserRight => (GpoSettingUnitKind.UserRight, null),
            GpoSettingArea.RestrictedGroup => (GpoSettingUnitKind.RestrictedGroup, null),
            GpoSettingArea.SecurityRegistryValue => (GpoSettingUnitKind.SecurityRegistryValue, null),
            GpoSettingArea.RegistryKeySecurity or GpoSettingArea.FileSecurity => (GpoSettingUnitKind.ObjectSecurity, GpoUnitRefusal.ObjectSecurity),
            GpoSettingArea.AdvancedAudit => (GpoSettingUnitKind.AuditSubcategory, null),
            GpoSettingArea.AuditOption or GpoSettingArea.GlobalSacl => (GpoSettingUnitKind.AuditOption, GpoUnitRefusal.AuditOption),
            GpoSettingArea.Script => (GpoSettingUnitKind.Script, GpoUnitRefusal.Script),
            GpoSettingArea.ScriptConfig => (GpoSettingUnitKind.ScriptConfiguration, GpoUnitRefusal.Script),
            GpoSettingArea.ScriptFile => (GpoSettingUnitKind.ScriptFile, GpoUnitRefusal.Script),
            GpoSettingArea.Comment => (GpoSettingUnitKind.Comment, GpoUnitRefusal.Comment),
            GpoSettingArea.UnreadableFile => (GpoSettingUnitKind.UnreadableFile, GpoUnitRefusal.Unreadable),
            _ => (GpoSettingUnitKind.Other, GpoUnitRefusal.Unsupported),
        };
        var masked = setting.Kind == GpoSettingKind.Masked;
        var principals = kind is GpoSettingUnitKind.UserRight or GpoSettingUnitKind.RestrictedGroup ? setting.Elements().Where(e => e.Length > 0).ToList() : [];
        var baseId = kind switch
        {
            GpoSettingUnitKind.RestrictedGroup => "G|" + Id(info.Name!) + "|" + info.Field,
            GpoSettingUnitKind.AuditSubcategory when info.Subcategory is { } sub => "A|" + sub.Guid.ToString("D", CultureInfo.InvariantCulture),
            _ => "K|" + setting.Key,
        };
        return new GpoSettingUnit
        {
            Id = string.Empty,
            BaseId = baseId,
            Kind = kind,
            Scope = info.Scope,
            Area = info.Area,
            Settings = [setting],
            Source = setting.Source,
            ValueFingerprint = principals.Count > 0 || kind is GpoSettingUnitKind.UserRight or GpoSettingUnitKind.RestrictedGroup
                ? string.Join(",", principals.Select(Id).Order(StringComparer.Ordinal))
                : setting.Value,
            Principals = principals,
            PrincipalSids = [.. principals.Select(Id)],
            Comparability = masked ? GpoUnitComparability.Masked : kind is GpoSettingUnitKind.Script ? GpoUnitComparability.Cumulative : GpoUnitComparability.Comparable,
            Refusal = masked ? GpoUnitRefusal.Secret : refusal,
        };
    }

    private static GpoSettingUnit Preference(GpoFolderContent content, string itemKey, List<GpoSetting> settings)
    {
        var info = GpoSettingKey.Parse(settings[0].Key);
        string? Value(string property) => settings.FirstOrDefault(s => GpoSettingKey.Parse(s.Key).Property is { } p && p.Equals(property, StringComparison.OrdinalIgnoreCase))?.Value;
        var element = Value("@element") ?? string.Empty;
        var type = info.PreferenceType ?? string.Empty;
        var identity = type.ToUpperInvariant() switch
        {
            "DRIVES" => Value("letter"),
            "GROUPS" when element == "Group" => Value("groupSid") is { Length: > 0 } sid ? sid : Value("groupName"),
            "GROUPS" => Value("userName"),
            "REGISTRY" => Value("hive") + "\\" + Value("key") + "!" + Value("name"),
            _ => Value("@name"),
        };
        var source = settings[0].Source ?? string.Empty;
        var collections = content.Files.TryGetValue(source, out var bytes)
            ? GppPreferenceDocument.Parse(bytes).Find(info.ItemId!)?.Collections ?? []
            : [];
        var scoped = collections.Any(c => GpoXml.Child(c, "Filters") is not null || GpoXml.Attribute(c, "disabled") == "1");
        var filters = string.Join("|", collections.Select(c => GpoXml.Child(c, "Filters")?.ToString() ?? string.Empty).Where(t => t.Length > 0)
            .Append(Value("Filters") ?? string.Empty).Where(t => t.Length > 0));
        var masked = settings.Any(s => s.Kind == GpoSettingKind.Masked);
        var ambiguous = identity is null;
        return new GpoSettingUnit
        {
            Id = string.Empty,
            BaseId = "I|" + info.Scope + "|" + type + "|" + element + "|" + (identity ?? info.ItemId),
            Kind = GpoSettingUnitKind.Preference,
            Scope = info.Scope,
            Area = GpoSettingArea.Preference,
            Settings = settings,
            Source = source,
            PreferenceType = type,
            ItemId = info.ItemId,
            ValueFingerprint = Fingerprint(settings.Where(s => GpoSettingKey.Parse(s.Key).Property is not "@changed"), itemKey),
            FiltersFingerprint = filters,
            Disabled = Value("@disabled") == "1" || collections.Any(c => GpoXml.Attribute(c, "disabled") == "1"),
            RemovePolicy = Value("@removePolicy") == "1",
            Comparability = masked ? GpoUnitComparability.Masked : ambiguous ? GpoUnitComparability.Ambiguous : filters.Length > 0 ? GpoUnitComparability.Conditional : GpoUnitComparability.Comparable,
            Refusal = masked ? GpoUnitRefusal.Secret : ambiguous ? GpoUnitRefusal.Unsupported : scoped ? GpoUnitRefusal.ScopedCollection : null,
        };
    }

    private static bool Masked(IEnumerable<GpoSetting> settings) => settings.Any(s => s.Kind == GpoSettingKind.Masked);

    private static string Fingerprint(IEnumerable<GpoSetting> settings, string prefix) =>
        string.Join("\n", settings.Select(s => s.Key[Math.Min(prefix.Length, s.Key.Length)..] + "=" + s.Value).Order(StringComparer.Ordinal));
}
