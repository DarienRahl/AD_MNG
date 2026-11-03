using System.Globalization;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Settings;

/// <summary>
/// Every place that shows GPO settings speaks the operator's language (design note docs/dev/k-computers-and-gpo.md 20.1):
/// turns a setting key of the shared parsers (<see cref="GpoSettingKey"/>) and its canonical value into GPMC's tree path,
/// a name, a readable value and the technical key behind it – account policies with units and their special values
/// explained, audit, user rights and groups with principals by name, security options by the K4 catalog, services, object
/// security, event logs, Registry.pol values (by their administrative template policy when <see cref="ComputerPolicies"/> /
/// <see cref="UserPolicies"/> say which one configures them, else as <c>HKLM\…\Value</c> with typed data), preferences
/// (type, action, item name, main properties, members, item-level targeting), scripts, comments and unreadable files.
/// Masked values stay masked; nothing here reads the directory (SIDs are named from <see cref="SidNames"/>, which the
/// caller resolves once per view through <see cref="Sids(IEnumerable{GpoSetting})"/> and <see cref="TrusteeNames.ResolveAsync"/>).
/// Never throws for a key or value of an unknown shape.
/// </summary>
public sealed partial class GpoSettingPresenter
{
    private readonly ILocalizer _l;
    private readonly IReadOnlyDictionary<string, string> _sidNames;
    private readonly Dictionary<string, string> _knownPrincipals;
    private PolicyLookup? _computerLookup;
    private PolicyLookup? _userLookup;

    /// <param name="localizer">The window's language.</param>
    /// <param name="sidNames">Names of domain SIDs (<see cref="TrusteeNames.ResolveAsync"/>); local groups and well-known
    /// principals are named without it.</param>
    /// <param name="definitions">The administrative templates (names of the policies comments refer to); null: none.</param>
    public GpoSettingPresenter(ILocalizer localizer, IReadOnlyDictionary<string, string>? sidNames = null, PolicyDefinitionSet? definitions = null)
    {
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _sidNames = sidNames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Definitions = definitions;
        _knownPrincipals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, sid, _) in SecuritySettingsText.LocalGroups)
        {
            _knownPrincipals[sid] = _l[SecuritySettingsText.LocalGroupNameKey(id)];
        }

        foreach (var (id, sid) in SecuritySettingsText.WellKnownPrincipals)
        {
            _knownPrincipals[sid] = _l[SecuritySettingsText.PrincipalKey(id)];
        }
    }

    /// <summary>The window's language.</summary>
    public ILocalizer Localizer => _l;

    /// <summary>Names of domain SIDs.</summary>
    public IReadOnlyDictionary<string, string> SidNames => _sidNames;

    /// <summary>The administrative templates (null: none were loaded).</summary>
    public PolicyDefinitionSet? Definitions { get; }

    /// <summary>The policies the computer half's Registry.pol configures (null: Registry.pol values are shown as registry values).</summary>
    public ConfiguredPolicies? ComputerPolicies
    {
        get => _computerLookup?.Policies;
        init => _computerLookup = value is null ? null : new PolicyLookup(value);
    }

    /// <summary>The policies the user half's Registry.pol configures.</summary>
    public ConfiguredPolicies? UserPolicies
    {
        get => _userLookup?.Policies;
        init => _userLookup = value is null ? null : new PolicyLookup(value);
    }

    /// <summary>
    /// The names of preference items by their item key (<see cref="GpoSettingKeyInfo.ItemKey"/>, from their <c>@name</c>
    /// settings – <see cref="ItemNamesOf(IEnumerable{GpoSetting})"/>): a property described alone is named after its item.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ItemNames { get; init; }

    /// <summary>The setting in the operator's language.</summary>
    public GpoSettingText Describe(GpoSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return Describe(setting.Key, Values(setting), setting.Kind, setting.IsList) with { Source = setting.Source };
    }

    /// <summary>
    /// A setting known by its key and values only (the Watchdog's change rows): <paramref name="values"/> are the list
    /// elements of a list setting or the one canonical value; <paramref name="masked"/> hides them.
    /// </summary>
    public GpoSettingText Describe(string key, IReadOnlyList<string> values, bool masked)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(values);
        var kind = masked ? GpoSettingKind.Masked
            : values.Count == 1 && values[0].StartsWith(GpoValues.HashPrefix, StringComparison.Ordinal) ? GpoSettingKind.Hash
            : GpoSettingKind.Text;
        return Describe(key, values, kind, values.Count != 1 || IsListArea(key));
    }

    /// <summary>
    /// One element of a list setting: a principal of a right or group by name, a GPP member with its action ("+ Name",
    /// "− Name"; unsigned: "Name" or "Name (Usuń)"), a targeting filter in words; anything else as written.
    /// </summary>
    public string DescribeElement(string key, string element, bool signed = true)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(element);
        var info = GpoSettingKey.Parse(key);
        return info.Area switch
        {
            GpoSettingArea.UserRight or GpoSettingArea.RestrictedGroup => PrincipalName(element),
            GpoSettingArea.Preference when info.Property is { } property && IsMembers(property) => MemberText(element, signed),
            GpoSettingArea.Preference when string.Equals(info.Property, "Filters", StringComparison.OrdinalIgnoreCase) => FilterText(element),
            _ => element,
        };
    }

    /// <summary>The name of a principal of a user right or group: a local group or well-known principal by its localized
    /// name, a domain SID by <see cref="SidNames"/>, anything else as written.</summary>
    public string PrincipalName(string sidOrName)
    {
        ArgumentNullException.ThrowIfNull(sidOrName);
        var text = sidOrName.Trim().TrimStart('*');
        if (!text.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return _knownPrincipals.TryGetValue(text, out var known) ? known
            : TrusteeNames.WellKnown(_l, text) is { } wellKnown ? wellKnown
            : _sidNames.TryGetValue(text, out var name) ? name
            : text;
    }

    /// <summary>
    /// The SIDs the settings name that only the directory can name (domain accounts and groups): user rights, Restricted
    /// Groups, GPP group SIDs, members and item-level targeting groups. Local groups and well-known principals are left out.
    /// </summary>
    public static IReadOnlyList<string> Sids(IEnumerable<GpoSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            if (setting.Kind is GpoSettingKind.Text)
            {
                Collect(GpoSettingKey.Parse(setting.Key), setting.Elements(), sids);
            }
        }

        return [.. sids];
    }

    /// <summary>The SIDs of one setting known by its key and values (<see cref="Sids(IEnumerable{GpoSetting})"/>).</summary>
    public static IReadOnlyList<string> Sids(string key, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(values);
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(GpoSettingKey.Parse(key), values, sids);
        return [.. sids];
    }

    /// <summary>The names of the preference items of <paramref name="settings"/> (their <c>@name</c>), by item key.</summary>
    public static IReadOnlyDictionary<string, string> ItemNamesOf(IEnumerable<GpoSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            if (setting.Kind == GpoSettingKind.Text && setting.Key.EndsWith("@name", StringComparison.OrdinalIgnoreCase)
                && GpoSettingKey.Parse(setting.Key) is { Area: GpoSettingArea.Preference, ItemKey: { } item, Property: { } property }
                && property.Equals("@name", StringComparison.OrdinalIgnoreCase) && setting.Value.Length > 0)
            {
                names.TryAdd(item, setting.Value);
            }
        }

        return names;
    }

    /// <summary>
    /// The policies each half's Registry.pol of <paramref name="content"/> configures (<see cref="AdministrativeTemplatePolicies.Configured"/>);
    /// a missing or unreadable file configures none.
    /// </summary>
    public static (ConfiguredPolicies Computer, ConfiguredPolicies User) Policies(GpoFolderContent content, PolicyDefinitionSet definitions)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(definitions);
        return (Configured(content, "Machine/Registry.pol", definitions, PolicyClass.Machine), Configured(content, "User/Registry.pol", definitions, PolicyClass.User));
    }

    private static ConfiguredPolicies Configured(GpoFolderContent content, string path, PolicyDefinitionSet definitions, PolicyClass scope)
    {
        var file = content.Files.FirstOrDefault(f => string.Equals(f.Key, path, StringComparison.OrdinalIgnoreCase)).Value;
        if (file is null || definitions.PolicyCount == 0)
        {
            return ConfiguredPolicies.None;
        }

        try
        {
            return AdministrativeTemplatePolicies.Configured(RegistryPolicyDocument.Parse(file), definitions, scope);
        }
        catch (GpoParseException)
        {
            return ConfiguredPolicies.None;
        }
    }

    // ---- Dispatch --------------------------------------------------------------------------------------------------------

    private GpoSettingText Describe(string key, IReadOnlyList<string> values, GpoSettingKind kind, bool isList)
    {
        GpoSettingKeyInfo info;
        try
        {
            info = GpoSettingKey.Parse(key);
        }
        catch (ArgumentException)
        {
            info = new GpoSettingKeyInfo { Key = key };
        }

        var text = Area(info, values, isList);
        text = kind switch
        {
            GpoSettingKind.Masked => text with { ValueText = _l[L.GroupPolicy.MaskedValue], Elements = [], IsMasked = true, Explanation = _l[L.GpoSettings.ExplainMasked] },
            GpoSettingKind.Hash when info.Area != GpoSettingArea.ScriptFile => text with
            {
                ValueText = _l[L.GpoSettings.LongValue],
                Elements = [.. values.Where(v => v.StartsWith(GpoValues.HashPrefix, StringComparison.Ordinal))],
                Explanation = _l[L.GpoSettings.ExplainLongValue],
            },
            GpoSettingKind.Unreadable => text with { ValueText = _l[L.GroupPolicy.UnreadableValue], Elements = [], Explanation = _l[L.GpoSettings.ExplainUnreadable] },
            _ => text,
        };
        return info.Truncated
            ? text with { Focus = null, Explanation = Join(text.Explanation, _l[L.GpoSettings.ExplainTruncated]) }
            : text;
    }

    private GpoSettingText Area(GpoSettingKeyInfo info, IReadOnlyList<string> values, bool isList)
    {
        try
        {
            return info.Area switch
            {
                GpoSettingArea.SystemAccess or GpoSettingArea.KerberosPolicy or GpoSettingArea.SecurityRegistryValue => SecurityOption(info, values),
                GpoSettingArea.EventAudit => EventAudit(info, values),
                GpoSettingArea.UserRight => UserRight(info, values),
                GpoSettingArea.RestrictedGroup => RestrictedGroup(info, values),
                GpoSettingArea.Service => Service(info, values),
                GpoSettingArea.RegistryKeySecurity or GpoSettingArea.FileSecurity => ObjectSecurity(info, values),
                GpoSettingArea.EventLog => EventLog(info, values),
                GpoSettingArea.SecuritySection => SecuritySection(info, values),
                GpoSettingArea.AdvancedAudit => AdvancedAudit(info, values),
                GpoSettingArea.AuditOption => AuditOption(info, values),
                GpoSettingArea.GlobalSacl => GlobalSacl(info, values),
                GpoSettingArea.PolicyRegistry => PolicyRegistry(info, values),
                GpoSettingArea.Preference => PreferenceProperty(info, values),
                GpoSettingArea.Script => Script(info, values),
                GpoSettingArea.ScriptConfig => ScriptConfig(info, values),
                GpoSettingArea.ScriptFile => ScriptFile(info, values),
                GpoSettingArea.Comment => Comment(info, values),
                GpoSettingArea.UnreadableFile => UnreadableFile(info),
                _ => Other(info, values, isList),
            };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException)
        {
            // A value of a shape the area does not expect: shown as written rather than not at all.
            return Other(info, values, isList);
        }
    }

    // ---- Building blocks -------------------------------------------------------------------------------------------------

    /// <summary>A row of <paramref name="info"/>'s scope with the given branch and folders.</summary>
    private GpoSettingText Row(GpoSettingKeyInfo info, GpoSettingBranch branch, IReadOnlyList<string> groups, string name, string value,
        IReadOnlyList<string>? elements = null, string? explanation = null, GroupPolicySettingFocus? focus = null) => new()
        {
            Key = info.Key,
            Keys = [info.Key],
            Scope = info.Scope,
            Kind = info.Area,
            Configuration = Configuration(info.Scope),
            Branch = branch,
            BranchText = BranchText(branch),
            Groups = groups,
            Name = name,
            Technical = info.Key,
            ValueText = value,
            Elements = elements ?? [],
            Explanation = explanation,
            Focus = focus ?? (info.Truncated || string.IsNullOrWhiteSpace(info.Key) ? null : GroupPolicySettingFocus.ForSetting(info.Key)),
        };

    private string Configuration(GpoScope scope) => _l[scope switch
    {
        GpoScope.Computer => L.GpoSettings.ComputerConfiguration,
        GpoScope.User => L.GpoSettings.UserConfiguration,
        _ => L.GpoSettings.GpoLevel,
    }];

    private string BranchText(GpoSettingBranch branch) => branch switch
    {
        GpoSettingBranch.Policies => _l[L.GpoSettings.Policies],
        GpoSettingBranch.Preferences => _l[L.GpoSettings.Preferences],
        _ => string.Empty,
    };

    /// <summary>"Ustawienia systemu Windows › Ustawienia zabezpieczeń › …".</summary>
    private string[] SecurityGroups(params string[] below) => [_l[L.GpoSettings.WindowsSettings], _l[L.GpoSettings.SecuritySettings], .. below];

    /// <summary>The text of a dynamic key, or <paramref name="fallback"/> when the resources do not have it.</summary>
    private string Text(string key, string fallback) => _l.Contains(key) ? _l[key] : fallback;

    /// <summary>"{0} – {1}".</summary>
    private string Part(string name, string part) => _l.Format(L.GpoSettings.NamePart, name, part);

    /// <summary>"{0}: {1}".</summary>
    private string Labeled(string label, string value) => _l.Format(L.GpoSettings.LabelValue, label, value);

    private static string? Join(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? second : string.IsNullOrEmpty(second) ? first : first + " " + second;

    private static string Single(IReadOnlyList<string> values) => values.Count == 0 ? string.Empty : values[0];

    private static IReadOnlyList<string> Values(GpoSetting setting) => setting.Kind switch
    {
        GpoSettingKind.Masked or GpoSettingKind.Unreadable => [],
        _ => setting.Elements(),
    };

    /// <summary>A number in the culture with its unit in the right plural form ("42 dni", "5 nieudanych prób logowania").</summary>
    private string Quantity(long value, string one, string few, string many)
    {
        var absolute = Math.Abs(value);
        var key = absolute == 1 ? one : absolute % 10 is >= 2 and <= 4 && absolute % 100 is < 12 or > 14 ? few : many;
        return _l.Format(key, value.ToString("N0", _l.Culture));
    }

    private static bool TryNumber(string text, out long value) =>
        long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private string YesNo(bool value) => GroupPolicyText.YesNo(_l, value);

    /// <summary>The principals of a list sorted by name as the window's culture sorts.</summary>
    private List<string> Principals(IEnumerable<string> values)
    {
        var compare = StringComparer.Create(_l.Culture, ignoreCase: true);
        return [.. values.Where(v => v.Trim().Length > 0).Select(PrincipalName).Order(compare)];
    }

    private static bool IsListArea(string key)
    {
        var info = GpoSettingKey.Parse(key);
        return info.Area is GpoSettingArea.UserRight or GpoSettingArea.RestrictedGroup
            || (info.Area == GpoSettingArea.Preference && info.Property is { } property
                && (property.Equals("Filters", StringComparison.OrdinalIgnoreCase) || property.EndsWith("Members", StringComparison.OrdinalIgnoreCase)));
    }

    private static void Collect(GpoSettingKeyInfo info, IEnumerable<string> values, HashSet<string> sids)
    {
        switch (info.Area)
        {
            case GpoSettingArea.UserRight:
                AddSids(values, sids);
                break;
            case GpoSettingArea.RestrictedGroup:
                AddSids(values, sids);
                AddSids(info.Name is { } group ? [group] : [], sids);
                break;
            case GpoSettingArea.Preference when info.Property is { } property:
                if (property.Equals("groupSid", StringComparison.OrdinalIgnoreCase))
                {
                    AddSids(values, sids);
                }
                else if (property.Equals("Filters", StringComparison.OrdinalIgnoreCase) || property.EndsWith("Members", StringComparison.OrdinalIgnoreCase))
                {
                    AddSids(values.Select(v => CanonicalElement.Parse(v).Attribute("sid") ?? string.Empty), sids);
                }

                break;
        }
    }

    private static void AddSids(IEnumerable<string> values, HashSet<string> sids)
    {
        foreach (var value in values)
        {
            var sid = value.Trim().TrimStart('*');
            if (sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) && !IsKnownLocally(sid))
            {
                sids.Add(sid);
            }
        }
    }

    /// <summary>A local group or well-known principal: named without the directory.</summary>
    private static bool IsKnownLocally(string sid) =>
        sid.StartsWith("S-1-5-32-", StringComparison.OrdinalIgnoreCase)
        || SecuritySettingsText.WellKnownPrincipals.Any(p => p.Sid.Equals(sid, StringComparison.OrdinalIgnoreCase))
        || sid is "S-1-3-0" or "S-1-5-18" or "S-1-1-0" or "S-1-5-11" or "S-1-5-9";
}
