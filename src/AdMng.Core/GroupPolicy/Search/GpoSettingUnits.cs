using System.Globalization;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.GroupPolicy.Search;

/// <summary>
/// Builds the setting units of one GPO (docs/dev/k-computers-and-gpo.md 21.2): pure, in memory. The units follow the
/// presenter's rows (one administrative template policy, one preference item, a service's or an object's two keys, a
/// script's command line and parameters; every other key alone), but with an identity that is the same in every GPO
/// (<see cref="GpoSettingUnit.BaseId"/>). Ids compare with <see cref="StringComparer.OrdinalIgnoreCase"/>.
/// </summary>
public static partial class GpoSettingUnits
{
    /// <summary>The units of one GPO from its normalized settings and its two parsed Registry.pol documents.</summary>
    public static IReadOnlyList<GpoSettingUnit> Of(IReadOnlyList<GpoSetting> settings, RegistryPolicyDocument? machineRegistry,
        RegistryPolicyDocument? userRegistry, GpoUnitContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        var (computer, user) = ConfiguredOf(machineRegistry, userRegistry, context.Definitions);
        return Build(settings, computer, user, context);
    }

    /// <summary>The units of a folder read in full (planners: <see cref="GroupPolicyReader.ReadSettings"/> + both Registry.pol).</summary>
    /// <remarks>
    /// The <c>Collection</c> chains of the preference items come from the folder's XML files
    /// (<see cref="PreferenceScopes"/>) unless <paramref name="context"/> already carries them; an unreadable Registry.pol
    /// configures no policy (its settings are the unreadable-file marker).
    /// </remarks>
    public static IReadOnlyList<GpoSettingUnit> Of(GpoFolderContent content, GpoUnitContext context)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);
        var settings = GroupPolicyReader.ReadSettings(content).Settings;
        var machine = RegistryOf(content, "Machine/Registry.pol");
        var user = RegistryOf(content, "User/Registry.pol");
        return Of(settings, machine, user, context with { PreferenceScopes = context.PreferenceScopes ?? PreferenceScopes(content) });
    }

    /// <summary>
    /// The <c>Collection</c> chains of the preference items of a folder (<see cref="GppXmlParser.Items"/>), by item key
    /// (<c>&lt;Scope&gt;/Preferences/&lt;Type&gt;/&lt;id&gt;</c>, as <see cref="GpoSettingKeyInfo.ItemKey"/>); items at the root of
    /// their file are left out. A file that cannot be parsed contributes nothing.
    /// </summary>
    public static IReadOnlyDictionary<string, GpoPreferenceScope> PreferenceScopes(GpoFolderContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new Dictionary<string, GpoPreferenceScope>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in content.Files)
        {
            var file = GpoFileClassifier.Classify(path);
            if (file.Role != GpoFileRole.Preferences)
            {
                continue;
            }

            System.Xml.Linq.XDocument document;
            try
            {
                document = GpoXml.Load(bytes);
            }
            catch (GpoParseException)
            {
                continue;
            }

            var prefix = (file.Scope == GpoScope.User ? "User" : "Computer") + "/Preferences/" + (file.PreferenceType ?? "Preferences") + "/";
            foreach (var item in GppXmlParser.Items(document))
            {
                if (item.Collections.Count == 0)
                {
                    continue;
                }

                var disabled = item.Collections.Any(c => GpoXml.Attribute(c, "disabled")?.Trim() == "1");
                var filters = item.Collections
                    .Select(c => GpoXml.Child(c, "Filters"))
                    .Where(f => f is not null && f.HasElements)
                    .Select(f => string.Join("\n", f!.Elements().Select(GppXmlParser.Canonical).Order(StringComparer.OrdinalIgnoreCase)))
                    .ToList();
                result.TryAdd(GpoValues.Key(prefix + item.ItemId), new GpoPreferenceScope(true, disabled, filters));
            }
        }

        return result;
    }

    /// <summary>
    /// The principal names in <paramref name="settings"/> that <see cref="GpoPrincipalResolver.KnownSid"/> cannot map, and the
    /// <c>DOMAIN\name</c> spellings it maps to a RID of this domain (only the resolver knows whether <c>DOMAIN</c> is this
    /// domain) – distinct, as written: the input of one <see cref="GpoPrincipalResolver.ResolveAsync"/> batch before <c>Of</c>.
    /// User rights, Restricted Groups (the group and its lists), GPP groups without a <c>groupSid</c> and GPP members without
    /// a <c>sid=</c>.
    /// </summary>
    public static IReadOnlyCollection<string> PrincipalNames(IReadOnlyList<GpoSetting> settings, string? domainSid)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? written)
        {
            var name = Written(written);
            if (name.Length > 0 && NeedsResolver(name, domainSid) && seen.Add(name))
            {
                names.Add(name);
            }
        }

        var groupSids = settings.Where(s => s.Kind == GpoSettingKind.Text && s.Value.Length > 0)
            .Select(s => GpoSettingKey.Parse(s.Key))
            .Where(i => i is { Area: GpoSettingArea.Preference, ItemKey: not null } && string.Equals(i.Property, "groupSid", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.ItemKey!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            if (setting.Kind != GpoSettingKind.Text)
            {
                continue;
            }

            var info = GpoSettingKey.Parse(setting.Key);
            switch (info.Area)
            {
                case GpoSettingArea.UserRight:
                    setting.Elements().ToList().ForEach(Add);
                    break;
                case GpoSettingArea.RestrictedGroup:
                    Add(info.Name);
                    setting.Elements().ToList().ForEach(Add);
                    break;
                case GpoSettingArea.Preference when info is { ItemKey: { } item, Property: { } property }:
                    if (property.Equals("groupName", StringComparison.OrdinalIgnoreCase) && !groupSids.Contains(item))
                    {
                        Add(setting.Value);
                    }
                    else if (IsMembers(property))
                    {
                        foreach (var member in setting.Elements())
                        {
                            var attributes = CanonicalAttributes(member);
                            if (!attributes.TryGetValue("sid", out var sid) || sid.Length == 0)
                            {
                                Add(attributes.GetValueOrDefault("name"));
                            }
                        }
                    }

                    break;
            }
        }

        return names;
    }

    /// <summary>
    /// Same setting, same value: false unless both are <see cref="GpoUnitComparability.Comparable"/> and neither is
    /// <see cref="GpoSettingUnit.Disabled"/>; policies compare by <see cref="PolicySetting"/> equality, the others by
    /// <see cref="GpoSettingUnit.ValueFingerprint"/>. Masked values are never equal. Units of different
    /// <see cref="GpoSettingUnit.BaseId"/>s are never the same setting.
    /// </summary>
    public static bool SameValue(GpoSettingUnit a, GpoSettingUnit b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Comparability != GpoUnitComparability.Comparable || b.Comparability != GpoUnitComparability.Comparable || a.Disabled || b.Disabled
            || a.Kind != b.Kind || !string.Equals(a.BaseId, b.BaseId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return a.Kind == GpoSettingUnitKind.AdministrativeTemplate
            ? a.Policy is not null && b.Policy is not null && a.Policy.Equals(b.Policy)
            : string.Equals(a.ValueFingerprint, b.ValueFingerprint, StringComparison.Ordinal);
    }

    /// <summary>The <see cref="GpoSettingUnit.BaseId"/> of a unit id (strips <c>#n</c>, n ≥ 2).</summary>
    /// <remarks>
    /// <see cref="Of(IReadOnlyList{GpoSetting}, RegistryPolicyDocument?, RegistryPolicyDocument?, GpoUnitContext)"/> never makes
    /// a base id that ends like an occurrence suffix (such an id gets a closing <c>|</c>), so the round trip is exact.
    /// </remarks>
    public static string BaseIdOf(string unitId)
    {
        ArgumentNullException.ThrowIfNull(unitId);
        return OccurrenceStart(unitId) is { } hash ? unitId[..hash] : unitId;
    }

    /// <summary>
    /// The per-GPO preference item prefix of a setting key (<c>&lt;Scope&gt;/Preferences/&lt;Type&gt;/&lt;id&gt;</c>), null
    /// for other keys – exactly what the old private <c>PreferenceItem(key)</c> copies of the K5 modeller and the K6
    /// conflict rules return.
    /// </summary>
    public static string? ItemKeyOf(string settingKey)
    {
        ArgumentNullException.ThrowIfNull(settingKey);
        var parts = settingKey.Split('/', 5);
        if (parts.Length < 4 || !parts[1].Equals("Preferences", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var item = parts[3];
        var at = item.IndexOf('@', StringComparison.Ordinal);
        return string.Join('/', parts[0], parts[1], parts[2], at < 0 ? item : item[..at]);
    }

    /// <summary>The policies each half's Registry.pol configures (none without a definition set).</summary>
    internal static (ConfiguredPolicies Computer, ConfiguredPolicies User) ConfiguredOf(RegistryPolicyDocument? machine, RegistryPolicyDocument? user, PolicyDefinitionSet? definitions) =>
        definitions is null
            ? (ConfiguredPolicies.None, ConfiguredPolicies.None)
            : (machine is null ? ConfiguredPolicies.None : AdministrativeTemplatePolicies.Configured(machine, definitions, PolicyClass.Machine),
               user is null ? ConfiguredPolicies.None : AdministrativeTemplatePolicies.Configured(user, definitions, PolicyClass.User));

    /// <summary>A Registry.pol of the folder, null when missing or unreadable.</summary>
    internal static RegistryPolicyDocument? RegistryOf(GpoFolderContent content, string path)
    {
        var bytes = content.Files.FirstOrDefault(f => string.Equals(f.Key, path, StringComparison.OrdinalIgnoreCase)).Value;
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return RegistryPolicyDocument.Parse(bytes);
        }
        catch (GpoParseException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="GpoPrincipalResolver.KnownSid"/> cannot map the name, or maps a <c>DOMAIN\name</c> to a RID of this domain
    /// although only the resolver can tell whether <c>DOMAIN</c> is this domain (<c>KONTOSO\Domain Admins</c>).
    /// </summary>
    private static bool NeedsResolver(string written, string? domainSid) =>
        GpoPrincipalResolver.KnownSid(written, domainSid) is not { } known || GpoPrincipalResolver.IsQualifiedDomainRid(written, known, domainSid);

    /// <summary>A principal as written, trimmed and without the security template's <c>*</c>.</summary>
    internal static string Written(string? principal) => principal is null ? string.Empty : principal.Trim().TrimStart('*').Trim();

    /// <summary>Where an occurrence suffix <c>#n</c> (n ≥ 2, no leading zero) starts, or null.</summary>
    private static int? OccurrenceStart(string id)
    {
        var hash = id.LastIndexOf('#');
        if (hash <= 0 || hash == id.Length - 1 || id.Length - hash - 1 > 9 || id[hash + 1] == '0' || id[hash - 1] == '…')
        {
            return null;
        }

        var digits = id.AsSpan(hash + 1);
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return null;
            }
        }

        return int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) >= 2 ? hash : null;
    }

    private static bool IsMembers(string property) => property.Equals("Members", StringComparison.OrdinalIgnoreCase)
        || property.EndsWith("/Members", StringComparison.OrdinalIgnoreCase);
}
