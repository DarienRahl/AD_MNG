using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.Search;

/// <summary>The grouping of settings into units, their identity, value and properties (21.2).</summary>
public static partial class GpoSettingUnits
{
    /// <summary>Separates the lines of a fingerprint (never in a canonical value).</summary>
    private const char LineSeparator = '\u001f';

    /// <summary>
    /// The units of one GPO: settings grouped as the presenter groups its rows (a configured policy, a preference item, a
    /// service's or object's two keys, a script's two keys, every other key alone), in the order their first setting comes.
    /// </summary>
    internal static IReadOnlyList<GpoSettingUnit> Build(IReadOnlyList<GpoSetting> settings, ConfiguredPolicies computer, ConfiguredPolicies user, GpoUnitContext context)
    {
        var computerLookup = computer.Policies.Count > 0 ? new GpoPolicyLookup(computer) : null;
        var userLookup = user.Policies.Count > 0 ? new GpoPolicyLookup(user) : null;
        var order = new List<string>();
        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        var truncatedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            var info = GpoSettingKey.Parse(setting.Key);
            ConfiguredPolicy? policy = null;
            PolicyClass? policyClass = null;
            string key;
            if (setting.Kind != GpoSettingKind.Unreadable && info is { Area: GpoSettingArea.PolicyRegistry, Truncated: false, RegistryKey: { } registryKey }
                && (info.Scope == GpoScope.User ? userLookup : computerLookup)?.Find(registryKey, info.ValueName) is { } owner)
            {
                policy = owner;
                policyClass = info.Scope == GpoScope.User ? PolicyClass.User : PolicyClass.Machine;
                key = "P|" + policyClass + "|" + owner.Definition.Id;
            }
            else if (info is { Area: GpoSettingArea.Preference, ItemKey: { } item, Truncated: false })
            {
                key = "I|" + item;
            }
            else if (info is { Area: GpoSettingArea.Service or GpoSettingArea.RegistryKeySecurity or GpoSettingArea.FileSecurity, Name: { } name, Field: not null, Truncated: false })
            {
                key = "O|" + info.Area + "|" + name;
            }
            else if (info is { Area: GpoSettingArea.Script, Index: { } index, Truncated: false })
            {
                key = "S|" + info.Scope + "|" + info.PowerShell + "|" + info.ScriptPhase + "|" + index.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                key = "K|" + setting.Key;
                if (info is { Truncated: true, Area: GpoSettingArea.Preference, ItemKey: { } cut })
                {
                    truncatedItems.Add(cut);
                }
            }

            if (!groups.TryGetValue(key, out var group))
            {
                group = new Group(key, policy, policyClass);
                groups[key] = group;
                order.Add(key);
            }

            group.Members.Add((setting, info));
        }

        var units = new List<GpoSettingUnit>(order.Count);
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in order)
        {
            var group = groups[key];
            var unit = key[0] switch
            {
                'P' => PolicyUnit(group),
                'I' => PreferenceUnit(group, context, truncatedItems),
                'O' => ObjectUnit(group),
                'S' => ScriptUnit(group),
                _ => SingleUnit(group.Members[0].Setting, group.Members[0].Info, context),
            };
            if (unit is null)
            {
                continue; // a collection pseudo-item: structure, not a setting
            }

            var baseId = BaseIdOf(unit.BaseId) == unit.BaseId ? unit.BaseId : unit.BaseId + "|";
            var occurrence = occurrences.GetValueOrDefault(baseId) + 1;
            occurrences[baseId] = occurrence;
            unit = unit with
            {
                BaseId = baseId,
                Id = occurrence == 1 ? baseId : baseId + "#" + occurrence.ToString(CultureInfo.InvariantCulture),
                Occurrence = occurrence,
            };
            units.Add(unit with { Refusal = RefusalOf(unit) });
        }

        return units;
    }

    // ---- Units -------------------------------------------------------------------------------------------------------

    private static GpoSettingUnit PolicyUnit(Group group)
    {
        var settings = group.Settings;
        var policy = group.Policy!;
        var masked = settings.Any(s => s.Kind == GpoSettingKind.Masked);
        return new GpoSettingUnit
        {
            Id = string.Empty,
            BaseId = "P|" + group.PolicyClass + "|" + policy.Definition.Id,
            Kind = GpoSettingUnitKind.AdministrativeTemplate,
            Scope = group.PolicyClass == PolicyClass.User ? GpoScope.User : GpoScope.Computer,
            Area = GpoSettingArea.PolicyRegistry,
            Source = settings[0].Source,
            PolicyId = policy.Definition.Id,
            PolicyClass = group.PolicyClass,
            Policy = policy.Setting,
            Settings = settings,
            // An inconsistent policy decodes to its state alone: its raw entries are its value (they are what a copy writes).
            ValueFingerprint = policy.Setting.State == PolicyState.Inconsistent
                ? PolicyFingerprint(policy.Setting) + LineSeparator + Fingerprint(settings, s => s.Key)
                : PolicyFingerprint(policy.Setting),
            Comparability = masked ? GpoUnitComparability.Masked : GpoUnitComparability.Comparable,
        };
    }

    private static GpoSettingUnit ObjectUnit(Group group)
    {
        var (first, info) = group.Members[0];
        var settings = group.Settings;
        var service = info.Area == GpoSettingArea.Service;
        var folder = service ? "Services" : info.Area == GpoSettingArea.RegistryKeySecurity ? "Registry Keys" : "File Security";
        var baseId = "O|Computer/Security/" + folder + "/" + info.Name;
        var prefix = KeyBefore(first.Key, info.Field);
        return new GpoSettingUnit
        {
            Id = string.Empty,
            BaseId = baseId,
            Kind = service ? GpoSettingUnitKind.Service : GpoSettingUnitKind.ObjectSecurity,
            Scope = info.Scope,
            Area = info.Area,
            Source = first.Source,
            Settings = settings,
            ValueFingerprint = Fingerprint(settings, s => s.Key.Length > prefix.Length ? s.Key[prefix.Length..] : string.Empty),
            Comparability = ComparabilityOf(settings, GpoUnitComparability.Comparable),
        };
    }

    private static GpoSettingUnit ScriptUnit(Group group)
    {
        var (first, info) = group.Members[0];
        var settings = group.Settings;
        var command = group.Members.FirstOrDefault(m => m.Info.Field is null).Setting;
        var parameters = group.Members.FirstOrDefault(m => string.Equals(m.Info.Field, "Parameters", StringComparison.OrdinalIgnoreCase)).Setting;
        var baseId = "S|" + Part(info.Scope) + "|" + (info.PowerShell ? "ps" : "cmd") + "|" + info.ScriptPhase + "|" + ValueText(command) + "|" + ValueText(parameters);
        var prefix = KeyBefore(first.Key, first == parameters ? "Parameters" : null);
        return new GpoSettingUnit
        {
            Id = string.Empty,
            BaseId = baseId,
            Kind = GpoSettingUnitKind.Script,
            Scope = info.Scope,
            Area = GpoSettingArea.Script,
            Source = first.Source,
            Settings = settings,
            ValueFingerprint = Fingerprint(settings, s => s.Key.Length > prefix.Length ? s.Key[prefix.Length..] : string.Empty),
            Comparability = ComparabilityOf(settings, GpoUnitComparability.Cumulative),
        };
    }

    private static GpoSettingUnit SingleUnit(GpoSetting setting, GpoSettingKeyInfo info, GpoUnitContext context)
    {
        IReadOnlyList<GpoSetting> settings = [setting];
        var kind = KindOf(info);
        var baseId = "K|" + setting.Key;
        IReadOnlyList<string> principals = [];
        IReadOnlyList<string> sids = [];
        var fingerprint = Fingerprint(settings, _ => string.Empty);
        switch (kind)
        {
            case GpoSettingUnitKind.UserRight:
            case GpoSettingUnitKind.RestrictedGroup:
            {
                var written = new List<string>();
                if (kind == GpoSettingUnitKind.RestrictedGroup && info.Name is { } groupName)
                {
                    written.Add(Written(groupName));
                }

                var members = setting.Kind == GpoSettingKind.Text ? setting.Elements().Select(Written).Where(e => e.Length > 0).ToList() : [];
                written.AddRange(members);
                principals = written.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                sids = principals.Select(p => SidOf(p, context)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                fingerprint = setting.Kind == GpoSettingKind.Text
                    ? string.Join(LineSeparator, members.Select(m => Normalized(m, context)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                    : "hash:" + setting.Value;
                if (kind == GpoSettingUnitKind.RestrictedGroup && !info.Truncated && info.Name is { } group)
                {
                    baseId = "G|" + Normalized(Written(group), context) + "|" + FieldText(info.Field);
                }

                break;
            }

            case GpoSettingUnitKind.AuditSubcategory when !info.Truncated && info.Subcategory is { } subcategory:
                baseId = "A|" + subcategory.Guid.ToString("B", CultureInfo.InvariantCulture);
                break;
        }

        var comparability = ComparabilityOf(settings, kind == GpoSettingUnitKind.Script ? GpoUnitComparability.Cumulative : GpoUnitComparability.Comparable);
        if (kind == GpoSettingUnitKind.UnreadableFile)
        {
            comparability = GpoUnitComparability.Unreadable;
        }

        return new GpoSettingUnit
        {
            Id = string.Empty,
            BaseId = baseId,
            Kind = kind,
            Scope = info.Scope,
            Area = info.Area,
            Source = setting.Source,
            PreferenceType = info.PreferenceType,
            ItemId = info.ItemId,
            Settings = settings,
            ValueFingerprint = fingerprint,
            Comparability = comparability,
            Principals = principals,
            PrincipalSids = sids,
        };
    }

    // ---- Properties --------------------------------------------------------------------------------------------------

    private static GpoSettingUnitKind KindOf(GpoSettingKeyInfo info) => info.Area switch
    {
        GpoSettingArea.PolicyRegistry => info.ValueName is null or RegistryPolicyDocument.DeleteAllValues ? GpoSettingUnitKind.RegistryKey : GpoSettingUnitKind.RegistryValue,
        GpoSettingArea.SystemAccess or GpoSettingArea.KerberosPolicy or GpoSettingArea.EventAudit or GpoSettingArea.EventLog
            or GpoSettingArea.SecuritySection => GpoSettingUnitKind.SecurityValue,
        GpoSettingArea.UserRight => GpoSettingUnitKind.UserRight,
        GpoSettingArea.RestrictedGroup => GpoSettingUnitKind.RestrictedGroup,
        GpoSettingArea.SecurityRegistryValue => GpoSettingUnitKind.SecurityRegistryValue,
        GpoSettingArea.Service => GpoSettingUnitKind.Service,
        GpoSettingArea.RegistryKeySecurity or GpoSettingArea.FileSecurity => GpoSettingUnitKind.ObjectSecurity,
        GpoSettingArea.AdvancedAudit => GpoSettingUnitKind.AuditSubcategory,
        GpoSettingArea.AuditOption or GpoSettingArea.GlobalSacl => GpoSettingUnitKind.AuditOption,
        GpoSettingArea.Preference when info.ItemKey is not null => GpoSettingUnitKind.Preference,
        GpoSettingArea.Script => GpoSettingUnitKind.Script,
        GpoSettingArea.ScriptConfig => GpoSettingUnitKind.ScriptConfiguration,
        GpoSettingArea.ScriptFile => GpoSettingUnitKind.ScriptFile,
        GpoSettingArea.Comment => GpoSettingUnitKind.Comment,
        GpoSettingArea.UnreadableFile => GpoSettingUnitKind.UnreadableFile,
        _ => GpoSettingUnitKind.Other,
    };

    /// <summary>Masked, then truncated, else <paramref name="otherwise"/>.</summary>
    private static GpoUnitComparability ComparabilityOf(IReadOnlyList<GpoSetting> settings, GpoUnitComparability otherwise) =>
        settings.Any(s => s.Kind == GpoSettingKind.Masked) ? GpoUnitComparability.Masked
        : settings.Any(s => s.Kind == GpoSettingKind.Unreadable) ? GpoUnitComparability.Unreadable
        : settings.Any(s => GpoSettingKey.IsTruncated(s.Key)) ? GpoUnitComparability.Truncated
        : otherwise;

    /// <summary>Why K7 does not copy or remove the unit (null: it can), in the order of 21.2.</summary>
    private static GpoUnitRefusal? RefusalOf(GpoSettingUnit unit) =>
        unit.Comparability == GpoUnitComparability.Masked ? GpoUnitRefusal.Secret
        : unit.Kind is GpoSettingUnitKind.Script or GpoSettingUnitKind.ScriptConfiguration or GpoSettingUnitKind.ScriptFile ? GpoUnitRefusal.Script
        : unit.Kind == GpoSettingUnitKind.ObjectSecurity ? GpoUnitRefusal.ObjectSecurity
        : unit.Kind == GpoSettingUnitKind.AuditOption ? GpoUnitRefusal.AuditOption
        : unit.Kind == GpoSettingUnitKind.Comment ? GpoUnitRefusal.Comment
        : unit.Kind == GpoSettingUnitKind.UnreadableFile || unit.Comparability == GpoUnitComparability.Unreadable ? GpoUnitRefusal.Unreadable
        : unit.Comparability == GpoUnitComparability.Truncated ? GpoUnitRefusal.Truncated
        : unit.Kind == GpoSettingUnitKind.Other || unit.Comparability == GpoUnitComparability.Ambiguous ? GpoUnitRefusal.Unsupported
        : unit.Refusal == GpoUnitRefusal.ScopedCollection ? GpoUnitRefusal.ScopedCollection // set by the preference builder
        : null;

    // ---- Values ------------------------------------------------------------------------------------------------------

    /// <summary>The sorted lines <c>&lt;suffix&gt;=&lt;canonical value&gt;</c> of the settings.</summary>
    private static string Fingerprint(IEnumerable<GpoSetting> settings, Func<GpoSetting, string> suffix) =>
        string.Join(LineSeparator, settings.Select(s => suffix(s) + "=" + CanonicalValue(s)).Order(StringComparer.Ordinal));

    private static string CanonicalValue(GpoSetting setting) => setting.Kind switch
    {
        GpoSettingKind.Masked => "\u0001masked",
        GpoSettingKind.Unreadable => "\u0001unreadable",
        _ => setting.Value.Replace(GpoSetting.ListSeparator, '\u001e'),
    };

    /// <summary>The state and element values of a policy, canonical (equal settings, equal text).</summary>
    private static string PolicyFingerprint(PolicySetting setting) =>
        setting.State + string.Concat(setting.Values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .Select(v => LineSeparator + v.Key.ToUpperInvariant() + "=" + ElementText(v.Value)));

    private static string ElementText(PolicyElementValue value) => value switch
    {
        BooleanElementValue b => b.Value ? "b:1" : "b:0",
        NumberElementValue n => "n:" + n.Value.ToString(CultureInfo.InvariantCulture),
        TextElementValue t => "t:" + t.Value,
        MultiTextElementValue m => "m:" + string.Join('\u001e', m.Lines),
        EnumElementValue e => "e:" + e.Index.ToString(CultureInfo.InvariantCulture),
        ListElementValue l => "l:" + string.Join('\u001e', l.Items.Select(i => i.Name + "\u001d" + i.Value)),
        _ => value.ToString() ?? string.Empty,
    };

    // ---- Principals --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The SID of a principal (well-known, SID text, or resolved by name), null when unknown. A <c>DOMAIN\name</c> that
    /// <see cref="GpoPrincipalResolver.KnownSid"/> maps to a RID of this domain takes the resolver's answer when there is a
    /// map: <c>KONTOSO\Domain Admins</c> of another domain is not this domain's …-512 (without a map – no LDAP, a test – the
    /// well-known mapping stands).
    /// </summary>
    private static string? SidOf(string written, GpoUnitContext context)
    {
        var map = context.SidsByName;
        if (GpoPrincipalResolver.KnownSid(written, context.DomainSid) is { } known
            && (map is null || !GpoPrincipalResolver.IsQualifiedDomainRid(written, known, context.DomainSid)))
        {
            return known.ToUpperInvariant();
        }

        return map is not null && map.TryGetValue(written, out var sid) && !string.IsNullOrEmpty(sid) ? sid.ToUpperInvariant() : null;
    }

    /// <summary>The SID where known, else the name as written in upper case.</summary>
    private static string Normalized(string written, GpoUnitContext context) => SidOf(written, context) ?? written.ToUpperInvariant();

    private static string FieldText(string? field) => field switch
    {
        null => string.Empty,
        _ when field.Equals("Members", StringComparison.OrdinalIgnoreCase) => "Members",
        _ when field.Equals("MemberOf", StringComparison.OrdinalIgnoreCase) => "MemberOf",
        _ => field,
    };

    /// <summary>The attributes of a canonical element (<c>Member action=ADD name=CONTOSO\x sid=S-1-…</c>), values may hold spaces.</summary>
    internal static IReadOnlyDictionary<string, string> CanonicalAttributes(string canonical)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bracket = canonical.IndexOf('[', StringComparison.Ordinal);
        var head = bracket < 0 ? canonical : canonical[..bracket];
        var matches = AttributeStart().Matches(head);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : head.Length;
            result.TryAdd(matches[i].Groups[1].Value, head[start..end].Trim());
        }

        return result;
    }

    [GeneratedRegex(@"(?<= )([A-Za-z][A-Za-z0-9]*)=", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeStart();

    [GeneratedRegex(@"\bsid=(\*?S-1-[0-9-]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FilterSid();

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    private static string Part(GpoScope scope) => scope == GpoScope.User ? "User" : "Computer";

    private static string ValueText(GpoSetting? setting) => setting is null ? string.Empty : CanonicalValue(setting);

    /// <summary>The key up to its last <c>/&lt;field&gt;</c> (the whole key without a field).</summary>
    private static string KeyBefore(string key, string? field) =>
        field is { Length: > 0 } && key.EndsWith("/" + field, StringComparison.OrdinalIgnoreCase) ? key[..^(field.Length + 1)] : key;

    /// <summary>The settings of one unit with their parsed keys, in order.</summary>
    private sealed class Group(string key, ConfiguredPolicy? policy, PolicyClass? policyClass)
    {
        public string Key { get; } = key;

        public ConfiguredPolicy? Policy { get; } = policy;

        public PolicyClass? PolicyClass { get; } = policyClass;

        public List<(GpoSetting Setting, GpoSettingKeyInfo Info)> Members { get; } = [];

        public IReadOnlyList<GpoSetting> Settings => [.. Members.Select(m => m.Setting)];
    }
}
