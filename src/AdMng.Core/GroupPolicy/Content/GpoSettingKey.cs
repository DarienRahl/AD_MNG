using System.Globalization;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.GroupPolicy.Content;

/// <summary>
/// What a setting key is about (design note docs/dev/k-computers-and-gpo.md 20.1): the security template's sections, the
/// advanced audit policy, Registry.pol, preferences, scripts, comments and the unreadable-file markers. Not stored.
/// </summary>
public enum GpoSettingArea
{
    /// <summary>A key none of the parsers writes (kept as read).</summary>
    Other = 0,

    /// <summary><c>Computer/Security/System Access/&lt;key&gt;</c>: password, lockout and account options.</summary>
    SystemAccess,

    /// <summary><c>Computer/Security/Kerberos Policy/&lt;key&gt;</c>.</summary>
    KerberosPolicy,

    /// <summary><c>Computer/Security/Event Audit/&lt;AuditX&gt;</c>: the legacy audit categories.</summary>
    EventAudit,

    /// <summary><c>Computer/Security/Privilege Rights/&lt;SeXxx&gt;</c>: a user right (list of principals).</summary>
    UserRight,

    /// <summary><c>Computer/Security/Group Membership/&lt;group&gt;/&lt;Members|MemberOf&gt;</c> (Restricted Groups).</summary>
    RestrictedGroup,

    /// <summary><c>Computer/Security/Registry Values/HKLM\…!&lt;name&gt;</c>: a security option.</summary>
    SecurityRegistryValue,

    /// <summary><c>Computer/Security/Services/&lt;name&gt;/&lt;StartupMode|Security&gt;</c> (System Services).</summary>
    Service,

    /// <summary><c>Computer/Security/Registry Keys/&lt;path&gt;/&lt;Mode|Security&gt;</c>.</summary>
    RegistryKeySecurity,

    /// <summary><c>Computer/Security/File Security/&lt;path&gt;/&lt;Mode|Security&gt;</c>.</summary>
    FileSecurity,

    /// <summary><c>Computer/Security/System Log|Security Log|Application Log/&lt;key&gt;</c>.</summary>
    EventLog,

    /// <summary>Any other section of the security template: <c>Computer/Security/&lt;section&gt;/&lt;key&gt;</c>.</summary>
    SecuritySection,

    /// <summary><c>Computer/Audit/{subcategory GUID}</c> (audit.csv), or a subcategory row without a valid GUID.</summary>
    AdvancedAudit,

    /// <summary><c>Computer/Audit/Option/&lt;name&gt;</c> (CrashOnAuditFail, FullPrivilegeAuditing, …).</summary>
    AuditOption,

    /// <summary><c>Computer/Audit/&lt;Policy Target&gt;/&lt;Subcategory&gt;</c>: global object access SACLs and other rows.</summary>
    GlobalSacl,

    /// <summary><c>Computer/Registry/HKLM\…[!&lt;value&gt;]</c>, <c>User/Registry/HKCU\…</c> (Registry.pol).</summary>
    PolicyRegistry,

    /// <summary><c>&lt;Scope&gt;/Preferences/&lt;Type&gt;/&lt;item&gt;…</c> (Group Policy Preferences).</summary>
    Preference,

    /// <summary><c>&lt;Scope&gt;/Scripts/[PowerShell/]&lt;phase&gt;/&lt;n&gt;[/Parameters]</c> (scripts.ini, psscripts.ini).</summary>
    Script,

    /// <summary><c>&lt;Scope&gt;/Scripts/[PowerShell/]Config/&lt;key&gt;</c> (<c>[ScriptsConfig]</c>).</summary>
    ScriptConfig,

    /// <summary><c>&lt;Scope&gt;/Scripts/Files/&lt;path&gt;</c>: a script file's content hash.</summary>
    ScriptFile,

    /// <summary><c>&lt;Scope&gt;/Comment/&lt;namespace&gt;:&lt;policy&gt;</c> (comment.cmtx) and <c>Gpo/Comment</c> (GPO.cmt).</summary>
    Comment,

    /// <summary><c>File/&lt;path&gt;</c>: a settings file that could not be read or parsed.</summary>
    UnreadableFile,
}

/// <summary>
/// The parts of a setting key (<see cref="GpoSettingKey.Parse"/>). Only the parts of the key's <see cref="Area"/> are set,
/// every other part is null. Parts are taken from the key as written (keys compare case-insensitively).
/// </summary>
public sealed record GpoSettingKeyInfo
{
    /// <summary>The key as given.</summary>
    public required string Key { get; init; }

    /// <summary>The part of the GPO: the computer part, the user part, or the GPO itself (<c>Gpo/Comment</c>, root files,
    /// keys of no known shape). An unreadable-file marker takes the scope of its file's folder (<c>Machine/</c>, <c>User/</c>).</summary>
    public GpoScope Scope { get; init; }

    public GpoSettingArea Area { get; init; }

    /// <summary>
    /// The security template section (canonical spelling for the known sections: <c>System Access</c>, <c>Kerberos Policy</c>,
    /// <c>Event Audit</c>, <c>Privilege Rights</c>, <c>Group Membership</c>, <c>Registry Values</c>, <c>Service General
    /// Setting</c>, <c>Registry Keys</c>, <c>File Security</c>, <c>System Log</c>, <c>Security Log</c>, <c>Application Log</c>;
    /// others as written); for an audit.csv row without a GUID its <c>Policy Target</c> column. Null otherwise.
    /// </summary>
    public string? Section { get; init; }

    /// <summary>
    /// What the setting names: the <c>[System Access]</c>/<c>[Kerberos Policy]</c>/log key, the legacy audit category
    /// (<c>AuditLogonEvents</c>), the user right constant (<c>SeDebugPrivilege</c>), the restricted group (SID or name),
    /// the service, the registry key or file path of an object security entry, the subcategory GUID (<c>{0cce923f-…}</c>)
    /// or Subcategory column text, the audit option, the policy id of a comment (<c>Namespace:Policy</c>), the
    /// script's line key when it is not <c>&lt;n&gt;CmdLine</c>/<c>&lt;n&gt;Parameters</c>, the script file's path below
    /// <c>Scripts/</c>, the <c>[ScriptsConfig]</c> key, the unreadable file's path.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>The registry key with its hive (<c>HKLM\Software\Policies\…</c>) of Registry.pol, <c>[Registry Values]</c>
    /// and <c>[Registry Keys]</c> keys.</summary>
    public string? RegistryKey { get; init; }

    /// <summary>
    /// The registry value of Registry.pol and <c>[Registry Values]</c> keys: empty for the default value (<c>(Default)</c>),
    /// <c>*</c> when the value names are hidden (a secret key such as SNMP communities), <c>**delvals.</c> for "delete every
    /// value"; null for a key without a value (<c>CREATE_KEY</c>, <c>DELETE_KEY</c>).
    /// </summary>
    public string? ValueName { get; init; }

    /// <summary>The preference type folder (<c>Groups</c>, <c>ScheduledTasks</c>, <c>Registry</c>, …).</summary>
    public string? PreferenceType { get; init; }

    /// <summary>The preference item: its <c>uid</c> (<c>{…}</c>), or <c>&lt;Element&gt;#n</c> for an item without one.</summary>
    public string? ItemId { get; init; }

    /// <summary>The key up to the preference item (<c>Computer/Preferences/Groups/{uid}</c>): every setting of one item shares it.</summary>
    public string? ItemKey { get; init; }

    /// <summary>
    /// What of a preference item the setting is: an item attribute with its <c>@</c> (<c>@name</c>, <c>@element</c>,
    /// <c>@disabled</c>, <c>@removePolicy</c>, <c>@userContext</c>, <c>@bypassErrors</c>, <c>@desc</c>, <c>@changed</c>), a
    /// <c>Properties</c> attribute (<c>action</c>, <c>groupName</c>), a flattened child (<c>Members</c>,
    /// <c>Task/Settings/Enabled</c>, <c>Triggers/Trigger@type</c>) or <c>Filters</c>. Null for a key that ends at the item.
    /// </summary>
    public string? Property { get; init; }

    /// <summary>The scripts.ini section (<c>Startup</c>, <c>Shutdown</c>, <c>Logon</c>, <c>Logoff</c>), or for a script file
    /// the first folder below <c>Scripts/</c>.</summary>
    public string? ScriptPhase { get; init; }

    /// <summary>The script's number (<c>0CmdLine</c> → 0).</summary>
    public int? Index { get; init; }

    /// <summary>
    /// The field of a multi-part entry: <c>Members</c>/<c>MemberOf</c> (Restricted Groups), <c>StartupMode</c>/<c>Security</c>
    /// (services), <c>Mode</c>/<c>Security</c> (registry key and file security), <c>Parameters</c> (scripts).
    /// </summary>
    public string? Field { get; init; }

    /// <summary>A psscripts.ini script or setting (<c>…/Scripts/PowerShell/…</c>).</summary>
    public bool PowerShell { get; init; }

    /// <summary>
    /// The key was longer than <see cref="GpoValues.MaxKeyLength"/> and was cut with a hash suffix (<c>…#&lt;hex&gt;</c>): the
    /// parts come from the kept start, the last one ends with <c>…</c> (the suffix is dropped), and no catalog link is made.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>The K4 catalog's account policy or security option (<c>[System Access]</c>, <c>[Kerberos Policy]</c>,
    /// <c>[Registry Values]</c>), null when not listed.</summary>
    public SecurityOptionDefinition? Option { get; init; }

    /// <summary>The K4 catalog's advanced audit subcategory, null when not listed.</summary>
    public AuditSubcategoryDefinition? Subcategory { get; init; }

    /// <summary>A preference item attribute (<see cref="Property"/> starts with <c>@</c>).</summary>
    public bool IsItemAttribute => Property is { Length: > 0 } property && property[0] == '@';

    /// <summary>The scope and area only: the parts of a key are GPO content and stay out of logs.</summary>
    public override string ToString() => Scope + "/" + Area;
}

/// <summary>
/// Reads the setting keys of the shared parsers (<see cref="GpoContentParser"/>) back into their parts (design note
/// docs/dev/k-computers-and-gpo.md 20.1) – from the key alone, since the Watchdog's change rows carry nothing else. Pure; it
/// never throws for a non-null key (a key of no known shape is <see cref="GpoSettingArea.Other"/>).
/// <list type="bullet">
/// <item><c>Computer/Security/&lt;section&gt;/…</c> – <see cref="GptTmplParser"/>; <c>System Access</c>, <c>Kerberos Policy</c>
/// and <c>Registry Values</c> keys are linked to <see cref="SecuritySettingsCatalog.FindOption"/> (<c>HKLM\…!Name</c> is the
/// catalog's <c>MACHINE\…\Name</c>).</item>
/// <item><c>Computer/Audit/…</c> – <see cref="AuditCsvParser"/>; GUIDs are linked to <see cref="SecuritySettingsCatalog.FindSubcategory(Guid)"/>.</item>
/// <item><c>Computer|User/Registry/…</c> – <see cref="RegistryPolParser"/>; the key path ends at the first <c>!</c>.</item>
/// <item><c>Computer|User/Preferences/&lt;Type&gt;/&lt;item&gt;…</c> – <see cref="GppXmlParser"/>.</item>
/// <item><c>Computer|User/Scripts/…</c> – <see cref="ScriptsIniParser"/> and script files (<see cref="GpoContentParser.ScriptKey"/>).</item>
/// <item><c>Computer|User/Comment/…</c>, <c>Gpo/Comment</c> – <see cref="PolicyCommentParser"/>.</item>
/// <item><c>File/&lt;path&gt;</c> – the unreadable-file markers of <c>GroupPolicyReader.Settings</c> and the Watchdog.</item>
/// </list>
/// </summary>
public static class GpoSettingKey
{
    /// <summary>Prefix of the unreadable-file markers.</summary>
    public const string FileMarkerPrefix = "File/";

    /// <summary>Length of the suffix <see cref="GpoValues.Key"/> appends to a cut key after its <c>…</c>: <c>#</c> and 16 hex digits.</summary>
    private const int HashSuffixLength = 17;

    /// <summary>The parts of <paramref name="key"/>.</summary>
    public static GpoSettingKeyInfo Parse(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var truncated = IsTruncated(key);
        var text = truncated ? key[..^HashSuffixLength] : key;
        var info = new GpoSettingKeyInfo { Key = key, Truncated = truncated };
        var parsed = ParseText(text, info);
        return truncated ? parsed with { Option = null, Subcategory = null } : parsed;
    }

    /// <summary>The key was cut by <see cref="GpoValues.Key"/> (<c>…#&lt;16 hex digits&gt;</c> at <see cref="GpoValues.MaxKeyLength"/>).</summary>
    public static bool IsTruncated(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != GpoValues.MaxKeyLength || key[^(HashSuffixLength + 1)] != '…' || key[^HashSuffixLength] != '#')
        {
            return false;
        }

        foreach (var c in key.AsSpan()[^(HashSuffixLength - 1)..])
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }

        return true;
    }

    private static GpoSettingKeyInfo ParseText(string text, GpoSettingKeyInfo info)
    {
        if (text.StartsWith(FileMarkerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = text[FileMarkerPrefix.Length..];
            var folder = Head(path, out _);
            var scope = Is(folder, "Machine") ? GpoScope.Computer : Is(folder, "User") ? GpoScope.User : GpoScope.Gpo;
            return info with { Scope = scope, Area = GpoSettingArea.UnreadableFile, Name = path };
        }

        var head = Head(text, out var rest);
        GpoScope? known = Is(head, "Computer") ? GpoScope.Computer : Is(head, "User") ? GpoScope.User : Is(head, "Gpo") ? GpoScope.Gpo : null;
        if (known is not { } partScope)
        {
            return info with { Scope = GpoScope.Gpo, Area = GpoSettingArea.Other, Name = text };
        }

        info = info with { Scope = partScope };
        if (rest is null)
        {
            return info;
        }

        var area = Head(rest, out var remainder);
        if (Is(area, "Comment"))
        {
            return info with { Area = GpoSettingArea.Comment, Name = remainder };
        }

        if (remainder is null)
        {
            return info with { Name = rest };
        }

        return area.ToUpperInvariant() switch
        {
            "SECURITY" => Security(info, remainder),
            "AUDIT" => Audit(info, remainder),
            "REGISTRY" => PolicyRegistry(info, remainder),
            "PREFERENCES" => Preference(info, text, remainder),
            "SCRIPTS" => Script(info, remainder),
            _ => info with { Name = rest },
        };
    }

    // ---- GptTmpl.inf -----------------------------------------------------------------------------------------------------

    private static GpoSettingKeyInfo Security(GpoSettingKeyInfo info, string rest)
    {
        var section = Head(rest, out var name);
        if (name is null)
        {
            return info with { Section = section };
        }

        switch (section.ToUpperInvariant())
        {
            case "SYSTEM ACCESS":
                return info with
                {
                    Area = GpoSettingArea.SystemAccess,
                    Section = SecurityTemplateDocument.SystemAccess,
                    Name = name,
                    Option = SecuritySettingsCatalog.FindOption(SecurityTemplateDocument.SystemAccess, name),
                };
            case "KERBEROS POLICY":
                return info with
                {
                    Area = GpoSettingArea.KerberosPolicy,
                    Section = SecurityTemplateDocument.KerberosPolicy,
                    Name = name,
                    Option = SecuritySettingsCatalog.FindOption(SecurityTemplateDocument.KerberosPolicy, name),
                };
            case "EVENT AUDIT":
                return info with { Area = GpoSettingArea.EventAudit, Section = SecurityTemplateDocument.EventAudit, Name = name };
            case "PRIVILEGE RIGHTS":
                return info with { Area = GpoSettingArea.UserRight, Section = SecurityTemplateDocument.PrivilegeRights, Name = name };
            case "GROUP MEMBERSHIP":
            {
                var (group, field) = Tail(name);
                return info with { Area = GpoSettingArea.RestrictedGroup, Section = SecurityTemplateDocument.GroupMembership, Name = group, Field = field };
            }

            case "REGISTRY VALUES":
            {
                var (registryKey, valueName) = RegistryPath(name);
                var option = valueName is { Length: > 0 } && valueName != "*"
                    ? SecuritySettingsCatalog.FindOption(SecurityTemplateDocument.RegistryValues, ToSecEditPath(registryKey) + "\\" + valueName)
                    : null;
                return info with
                {
                    Area = GpoSettingArea.SecurityRegistryValue,
                    Section = SecurityTemplateDocument.RegistryValues,
                    RegistryKey = registryKey,
                    ValueName = valueName,
                    Option = option,
                };
            }

            case "SERVICES":
            {
                var (service, field) = Tail(name);
                return info with { Area = GpoSettingArea.Service, Section = SecurityTemplateDocument.ServiceGeneralSetting, Name = service, Field = field };
            }

            case "REGISTRY KEYS":
            {
                var (path, field) = Tail(name);
                return info with { Area = GpoSettingArea.RegistryKeySecurity, Section = "Registry Keys", Name = path, RegistryKey = path, Field = field };
            }

            case "FILE SECURITY":
            {
                var (path, field) = Tail(name);
                return info with { Area = GpoSettingArea.FileSecurity, Section = "File Security", Name = path, Field = field };
            }

            case "SYSTEM LOG":
                return info with { Area = GpoSettingArea.EventLog, Section = "System Log", Name = name };
            case "SECURITY LOG":
                return info with { Area = GpoSettingArea.EventLog, Section = "Security Log", Name = name };
            case "APPLICATION LOG":
                return info with { Area = GpoSettingArea.EventLog, Section = "Application Log", Name = name };
            default:
                return info with { Area = GpoSettingArea.SecuritySection, Section = section, Name = name };
        }
    }

    /// <summary><c>&lt;path&gt;/&lt;field&gt;</c> split at the last <c>/</c> (the field names have none); no <c>/</c>: the path alone.</summary>
    private static (string Name, string? Field) Tail(string text)
    {
        var slash = text.LastIndexOf('/');
        return slash < 0 ? (text, null) : (text[..slash], text[(slash + 1)..]);
    }

    /// <summary>
    /// A registry path of a key (<c>HKLM\…</c>) back to the security template's form (<c>MACHINE\…</c>): the inverse of
    /// <see cref="RegistryFormat.FromSecEditPath"/>.
    /// </summary>
    private static string ToSecEditPath(string path)
    {
        var separator = path.IndexOf('\\', StringComparison.Ordinal);
        var root = separator < 0 ? path : path[..separator];
        var rest = separator < 0 ? string.Empty : path[separator..];
        var hive = root.ToUpperInvariant() switch
        {
            "HKLM" => "MACHINE",
            "HKU" => "USER",
            "HKCR" => "CLASSES_ROOT",
            "HKCU" => "CURRENT_USER",
            _ => root,
        };
        return hive + rest;
    }

    // ---- audit.csv -------------------------------------------------------------------------------------------------------

    private static GpoSettingKeyInfo Audit(GpoSettingKeyInfo info, string rest)
    {
        const string OptionPrefix = "Option/";
        if (rest.StartsWith(OptionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return info with { Area = GpoSettingArea.AuditOption, Name = rest[OptionPrefix.Length..] };
        }

        var target = Head(rest, out var subcategory);
        if (subcategory is null)
        {
            return Guid.TryParse(rest, out var guid)
                ? info with { Area = GpoSettingArea.AdvancedAudit, Name = rest, Subcategory = SecuritySettingsCatalog.FindSubcategory(guid) }
                : info with { Name = rest };
        }

        // A subcategory row whose GUID column is empty or invalid ("System" target), else a global SACL row.
        var definition = SecuritySettingsCatalog.FindSubcategory(subcategory);
        return Is(target, "System") || definition is not null
            ? info with { Area = GpoSettingArea.AdvancedAudit, Section = target, Name = subcategory, Subcategory = definition }
            : info with { Area = GpoSettingArea.GlobalSacl, Section = target, Name = subcategory };
    }

    // ---- Registry.pol ----------------------------------------------------------------------------------------------------

    private static GpoSettingKeyInfo PolicyRegistry(GpoSettingKeyInfo info, string rest)
    {
        var (registryKey, valueName) = RegistryPath(rest);
        return info with
        {
            Area = GpoSettingArea.PolicyRegistry,
            RegistryKey = registryKey,
            ValueName = valueName is "(Default)" ? string.Empty : valueName,
        };
    }

    /// <summary><c>HKLM\key!value</c>: the key path ends at the first <c>!</c> (template key names have none; a value name may).</summary>
    private static (string Key, string? ValueName) RegistryPath(string text)
    {
        var bang = text.IndexOf('!', StringComparison.Ordinal);
        return bang < 0 ? (text, null) : (text[..bang], text[(bang + 1)..]);
    }

    // ---- Preferences -----------------------------------------------------------------------------------------------------

    private static GpoSettingKeyInfo Preference(GpoSettingKeyInfo info, string text, string rest)
    {
        var type = Head(rest, out var item);
        info = info with { Area = GpoSettingArea.Preference, PreferenceType = type };
        if (item is null)
        {
            return info;
        }

        var end = ItemIdEnd(item);
        var tail = item[end..];
        var itemStart = text.Length - item.Length;
        return info with
        {
            ItemId = item[..end],
            ItemKey = text[..(itemStart + end)],
            Property = tail.Length == 0 ? null : tail[0] == '@' ? tail : tail[1..],
        };
    }

    /// <summary>
    /// Where the item id ends: a <c>{…}</c> uid at its closing brace when <c>/</c>, <c>@</c> or the end follows (a uid holds
    /// neither in practice), any other id at the first <c>/</c> or <c>@</c>.
    /// </summary>
    private static int ItemIdEnd(string item)
    {
        if (item.StartsWith('{'))
        {
            var close = item.IndexOf('}', StringComparison.Ordinal);
            if (close > 0 && (close + 1 == item.Length || item[close + 1] is '/' or '@'))
            {
                return close + 1;
            }
        }

        var end = item.AsSpan().IndexOfAny('/', '@');
        return end < 0 ? item.Length : end;
    }

    // ---- Scripts ---------------------------------------------------------------------------------------------------------

    private static GpoSettingKeyInfo Script(GpoSettingKeyInfo info, string rest)
    {
        const string FilesPrefix = "Files/", PowerShellPrefix = "PowerShell/", ConfigPrefix = "Config/";
        if (rest.StartsWith(FilesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = rest[FilesPrefix.Length..];
            var folder = Head(path, out var below);
            return info with { Area = GpoSettingArea.ScriptFile, Name = path, ScriptPhase = below is null ? null : folder };
        }

        if (rest.StartsWith(PowerShellPrefix, StringComparison.OrdinalIgnoreCase))
        {
            info = info with { PowerShell = true };
            rest = rest[PowerShellPrefix.Length..];
        }

        if (rest.StartsWith(ConfigPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return info with { Area = GpoSettingArea.ScriptConfig, Name = rest[ConfigPrefix.Length..] };
        }

        var phase = Head(rest, out var entry);
        info = info with { Area = GpoSettingArea.Script, ScriptPhase = phase };
        if (entry is null)
        {
            return info;
        }

        var number = Head(entry, out var field);
        if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && (field is null || Is(field, "Parameters")))
        {
            return info with { Index = index, Field = field };
        }

        return info with { Name = entry };
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------------

    /// <summary>The text up to the first <c>/</c>; <paramref name="rest"/> is what follows it (null without a <c>/</c>).</summary>
    private static string Head(string text, out string? rest)
    {
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            rest = null;
            return text;
        }

        rest = text[(slash + 1)..];
        return text[..slash];
    }

    private static bool Is(string text, string name) => text.Equals(name, StringComparison.OrdinalIgnoreCase);
}
