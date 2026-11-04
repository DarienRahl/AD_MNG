using AdMng.Core.GroupPolicy.Content;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Settings;

/// <summary>Group Policy Preferences: GPMC's folders, the item's type, action, name, main properties, members and targeting.</summary>
public sealed partial class GpoSettingPresenter
{
    /// <summary>The preference folders GPMC lists under "Ustawienia systemu Windows" (the others are under "Ustawienia panelu sterowania").</summary>
    private static readonly HashSet<string> WindowsPreferenceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Applications", "Drives", "EnvironmentVariables", "Files", "Folders", "IniFiles", "Registry", "NetworkShares", "Shortcuts",
    };

    /// <summary>The properties that matter most per preference folder (listed first, in this order).</summary>
    private static readonly Dictionary<string, string[]> MainProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Groups"] = ["groupName", "groupSid", "userName", "newName", "fullName", "description", "deleteAllUsers", "deleteAllGroups"],
        ["Drives"] = ["letter", "path", "label", "useLetter", "persistent", "thisDrive", "allDrives", "userName"],
        ["Files"] = ["fromPath", "targetPath", "readOnly", "archive", "hidden", "suppress"],
        ["Folders"] = ["path", "readOnly", "archive", "hidden", "deleteFolder", "deleteSubFolders", "deleteFiles", "deleteReadOnly", "deleteIgnoreErrors"],
        ["Registry"] = ["hive", "key", "name", "type", "value", "default"],
        ["Shortcuts"] = ["shortcutPath", "targetType", "targetPath", "arguments", "startIn", "iconPath", "comment"],
        ["ScheduledTasks"] =
        [
            "name", "Task/Actions/Exec/Command", "Task/Actions/Exec/Arguments", "Task/Actions/Exec/WorkingDirectory", "runAs", "logonType",
            "appName", "args", "startIn", "comment", "enabled",
        ],
        ["Services"] = ["serviceName", "startupType", "serviceAction", "accountName", "timeout"],
        ["EnvironmentVariables"] = ["name", "value", "user", "partial"],
        ["IniFiles"] = ["path", "section", "property", "value"],
        ["DataSources"] = ["dsn", "driver", "description", "userDSN"],
        ["Printers"] = ["path", "localName", "ipAddress", "port", "location", "comment", "default"],
        ["NetworkShares"] = ["name", "path", "comment"],
    };

    /// <summary>Properties whose values are 0/1 flags ("Tak"/"Nie").</summary>
    private static readonly HashSet<string> FlagProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "deleteAllUsers", "deleteAllGroups", "removeAccounts", "changeLogon", "noChange", "neverExpires", "acctDisabled", "persistent", "useLetter",
        "thisDrive", "allDrives", "readOnly", "archive", "hidden", "suppress", "deleteFolder", "deleteSubFolders", "deleteFiles", "deleteReadOnly",
        "deleteIgnoreErrors", "default", "user", "partial", "userDSN", "enabled", "Enabled", "displayDecimal",
    };

    /// <summary>A preference setting described alone: "&lt;item&gt; – &lt;property&gt;" and the property's value.</summary>
    private GpoSettingText PreferenceProperty(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var item = ItemLabel(info);
        if (info.Property is not { Length: > 0 } property)
        {
            return Row(info, GpoSettingBranch.Preferences, PreferenceGroups(info), item, Single(values));
        }

        var (value, elements) = PropertyValue(property, values);
        value = value.Length == 0 ? _l[L.SecuritySettings.EmptyText] : value;
        return Row(info, GpoSettingBranch.Preferences, PreferenceGroups(info), Part(item, PropertyLabel(property)), value, elements,
            focus: info.ItemKey is { } itemKey && !info.Truncated ? GroupPolicySettingFocus.ForSetting(itemKey) : null);
    }

    /// <summary>
    /// One row for a preference item made of its settings: the item's name, "&lt;action&gt;: &lt;summary&gt;" as the value (a drive's
    /// letter and path, a group's members …) and every property, member, flag and the item-level targeting as elements.
    /// </summary>
    private GpoSettingText DescribeItem(IReadOnlyList<(GpoSetting Setting, GpoSettingKeyInfo Info)> members)
    {
        var info = members[0].Info;
        var properties = new Dictionary<string, GpoSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var (setting, member) in members)
        {
            if (member.Property is { } property)
            {
                properties.TryAdd(property, setting);
            }
        }

        string? Value(string property) => properties.TryGetValue(property, out var s) && s.Kind == GpoSettingKind.Text ? s.Value : null;
        var type = info.PreferenceType ?? string.Empty;
        var element = Value("@element");
        var name = properties.TryGetValue("@name", out var nameSetting) && nameSetting.Kind == GpoSettingKind.Masked
            ? _l[L.GroupPolicy.MaskedValue]
            : Value("@name") is { Length: > 0 } itemName ? itemName
            : Value("groupName") ?? Value("userName") ?? Value("name") ?? ItemLabel(info);
        var action = Value("action") is { } code ? ActionWord(code) : null;
        var summary = ItemSummary(type, element, properties, Value);

        var lines = new List<string>();
        if (element is not null)
        {
            lines.Add(Labeled(_l[L.GpoSettings.PrefItemType], ElementName(element)));
        }

        if (action is not null)
        {
            lines.Add(Labeled(_l[L.GpoSettings.PrefAction], action));
        }

        var main = MainProperties.TryGetValue(type, out var list) ? list : [];
        var ordered = properties.Keys
            .Where(p => p[0] != '@' && !p.Equals("action", StringComparison.OrdinalIgnoreCase) && !IsMembers(p) && !p.Equals("Filters", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Array.FindIndex(main, m => m.Equals(p, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : main.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var property in ordered.Where(p => !IsNoise(p, Value(p))))
        {
            var setting = properties[property];
            var text = setting.Kind switch
            {
                GpoSettingKind.Masked => _l[L.GroupPolicy.MaskedValue],
                GpoSettingKind.Hash => LongValueText([setting.Value]),
                _ => PropertyValue(property, setting.Elements()).Text,
            };

            // The item's own name again ("Nazwa" of a task) and lines another property already gave (a task's run-as account).
            var line = Labeled(PropertyLabel(property), text);
            if (text.Length > 0 && !(property.Equals("name", StringComparison.OrdinalIgnoreCase) && text == name) && !lines.Contains(line))
            {
                lines.Add(line);
            }
        }

        foreach (var membersProperty in properties.Keys.Where(IsMembers))
        {
            lines.AddRange(properties[membersProperty].Elements().Select(MemberText));
        }

        if (Value("@disabled") == "1")
        {
            lines.Add(_l[L.SecuritySettings.DisabledBadge]);
        }

        AddFlag(lines, Value("@removePolicy"), L.GpoSettings.PrefRemovePolicy);
        AddFlag(lines, Value("@userContext"), L.GpoSettings.PrefUserContext);
        AddFlag(lines, Value("@bypassErrors"), L.GpoSettings.PrefBypassErrors);
        if (Value("@desc") is { Length: > 0 } description)
        {
            lines.Add(Labeled(_l[L.GpoSettings.PrefDescription], description));
        }

        if (properties.TryGetValue("Filters", out var filters) && filters.Kind == GpoSettingKind.Text)
        {
            lines.Add(_l.Format(L.GpoSettings.PrefTargeting, FilterSummary(filters.Elements())));
        }

        var valueText = action is null ? summary : summary.Length == 0 ? action : Labeled(action, summary);
        var keys = members.Select(m => m.Setting.Key).ToList();
        return new GpoSettingText
        {
            Key = keys[0],
            Keys = keys,
            Scope = info.Scope,
            Kind = GpoSettingArea.Preference,
            Configuration = Configuration(info.Scope),
            Branch = GpoSettingBranch.Preferences,
            BranchText = BranchText(GpoSettingBranch.Preferences),
            Groups = PreferenceGroups(info),
            Name = name,
            Technical = info.ItemKey ?? info.Key,
            ValueText = valueText.Length == 0 ? ElementName(element ?? type) : valueText,
            Elements = lines,
            Explanation = Value("@disabled") == "1" ? _l[L.SecuritySettings.DisabledBadge] : null,
            // A local user of Groups.xml shares the local groups' keys, but the security settings editor edits only the groups.
            Focus = info.ItemKey is { } itemKey && !info.Truncated && !IsLocalUser(type, element) ? GroupPolicySettingFocus.ForSetting(itemKey) : null,
            Source = members[0].Setting.Source,
        };
    }

    /// <summary>A <c>User</c> item of Groups.xml (a local user, not a local group).</summary>
    private static bool IsLocalUser(string type, string? element) =>
        type.Equals("Groups", StringComparison.OrdinalIgnoreCase) && string.Equals(element, "User", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Preferencje › Ustawienia panelu sterowania › Lokalni użytkownicy i grupy".</summary>
    private string[] PreferenceGroups(GpoSettingKeyInfo info)
    {
        var type = info.PreferenceType ?? string.Empty;
        return
        [
            _l[WindowsPreferenceTypes.Contains(type) ? L.GpoSettings.WindowsSettings : L.GpoSettings.ControlPanelSettings],
            Text(L.GpoSettings.PreferenceTypeKey(type), type),
        ];
    }

    /// <summary>The item's name from <see cref="ItemNames"/>, else "Element {uid}".</summary>
    private string ItemLabel(GpoSettingKeyInfo info) =>
        info.ItemKey is { } key && ItemNames is { } names && names.TryGetValue(key, out var name) && name.Length > 0
            ? name
            : _l.Format(L.GpoSettings.PrefItem, info.ItemId ?? info.Key);

    /// <summary>"Typ elementu", "Nazwa", "Członkowie", "Ścieżka" … (unknown properties as written, child paths by their last part).</summary>
    private string PropertyLabel(string property)
    {
        switch (property.ToUpperInvariant())
        {
            case "@ELEMENT":
                return _l[L.GpoSettings.PrefItemType];
            case "@NAME":
                return _l[L.GpoSettings.PrefName];
            case "@DISABLED":
                return _l[L.GpoSettings.PrefDisabled];
            case "@REMOVEPOLICY":
                return _l[L.GpoSettings.PrefRemovePolicy];
            case "@USERCONTEXT":
                return _l[L.GpoSettings.PrefUserContext];
            case "@BYPASSERRORS":
                return _l[L.GpoSettings.PrefBypassErrors];
            case "@DESC":
                return _l[L.GpoSettings.PrefDescription];
            case "@CHANGED":
                return _l[L.GpoSettings.PrefChanged];
            case "ACTION":
                return _l[L.GpoSettings.PrefAction];
            case "FILTERS":
                return _l[L.GpoSettings.PrefTargetingLabel];
        }

        if (IsMembers(property))
        {
            return _l[L.GpoSettings.PrefMembers];
        }

        var last = property;
        var cut = last.LastIndexOfAny(['/', '@']);
        if (cut >= 0 && cut < last.Length - 1)
        {
            last = last[(cut + 1)..];
        }

        var bracket = last.IndexOf('[', StringComparison.Ordinal);
        if (bracket > 0)
        {
            last = last[..bracket];
        }

        // Resource names ignore case when compiled but are looked up as written: "Arguments" of a task is "arguments".
        var camel = last.Length > 0 ? char.ToLowerInvariant(last[0]) + last[1..] : last;
        return Text(L.GpoSettings.PreferencePropertyKey(last), Text(L.GpoSettings.PreferencePropertyKey(camel), property));
    }

    /// <summary>A property's value: flags as "Tak"/"Nie", the action word, members, targeting, SIDs by name.</summary>
    private (string Text, IReadOnlyList<string> Elements) PropertyValue(string property, IReadOnlyList<string> values)
    {
        if (IsMembers(property))
        {
            var members = values.Select(MemberText).ToList();
            return (members.Count == 0 ? _l[L.SecuritySettings.NoMembers] : string.Join(", ", members), members);
        }

        if (property.Equals("Filters", StringComparison.OrdinalIgnoreCase))
        {
            return (FilterSummary(values), [.. values.Select(FilterText)]);
        }

        var value = Single(values);
        var last = property.LastIndexOfAny(['/', '@']) is var cut and >= 0 && cut < property.Length - 1 ? property[(cut + 1)..] : property;
        var text = property.ToUpperInvariant() switch
        {
            "@ELEMENT" => ElementName(value),
            "@DISABLED" or "@REMOVEPOLICY" or "@USERCONTEXT" or "@BYPASSERRORS" => YesNo(value == "1"),
            "ACTION" => ActionWord(value),
            "STARTUPTYPE" => StartupType(value),
            "TARGETTYPE" => value.ToUpperInvariant() switch
            {
                "FILESYSTEM" => _l[L.GpoSettings.TargetFileSystem],
                "URL" => _l[L.GpoSettings.TargetUrl],
                "SHELL" => _l[L.GpoSettings.TargetShell],
                _ => value,
            },
            "SERVICEACTION" => ServiceAction(value),
            "GROUPSID" or "SID" => value.Length == 0 ? value : PrincipalName(value),
            _ when FlagProperties.Contains(last) && value is "0" or "1" or "true" or "false" => YesNo(value is "1" or "true"),
            _ when values.Count > 1 => string.Join(", ", values),
            _ => value,
        };
        return (text, values.Count > 1 ? values : []);
    }

    /// <summary>"Utwórz", "Zamień", "Aktualizuj", "Usuń" (GPP's C/R/U/D).</summary>
    private string ActionWord(string code) => code.Trim().ToUpperInvariant() switch
    {
        "C" => _l[L.SecuritySettings.GppActionCreate],
        "R" => _l[L.SecuritySettings.GppActionReplace],
        "U" => _l[L.SecuritySettings.GppActionUpdate],
        "D" => _l[L.SecuritySettings.GppActionDelete],
        _ => code,
    };

    private string StartupType(string value) => value.Trim().ToUpperInvariant() switch
    {
        "AUTOMATIC" or "AUTOMATIC_DELAYED" or "DELAYED_AUTOSTART" => _l[L.SecuritySettings.ServiceAutomatic],
        "MANUAL" => _l[L.SecuritySettings.ServiceManual],
        "DISABLED" => _l[L.SecuritySettings.ServiceDisabled],
        "NOCHANGE" or "" => _l[L.GpoSettings.PrefNoChange],
        _ => value,
    };

    private string ServiceAction(string value) => value.Trim().ToUpperInvariant() switch
    {
        "START" => _l[L.GpoSettings.PrefServiceStart],
        "STOP" => _l[L.GpoSettings.PrefServiceStop],
        "RESTART" => _l[L.GpoSettings.PrefServiceRestart],
        "NOCHANGE" or "" => _l[L.GpoSettings.PrefNoChange],
        _ => value,
    };

    /// <summary>"Grupa lokalna", "Mapowanie dysku" … (unknown element names as written).</summary>
    private string ElementName(string element) => Text(L.GpoSettings.PreferenceElementKey(element), element);

    /// <summary>What the item does in a few words, by its folder (a drive's letter and path, a group's members …).</summary>
    private string ItemSummary(string type, string? element, Dictionary<string, GpoSetting> properties, Func<string, string?> value)
    {
        string? V(string property) => value(property) is { Length: > 0 } text ? text : null;
        string Arrow(string? from, string? to) => from is null ? to ?? string.Empty : to is null ? from : from + " → " + to;
        switch (type.ToUpperInvariant())
        {
            case "GROUPS" when !string.Equals(element, "User", StringComparison.OrdinalIgnoreCase):
                var members = properties.Keys.Where(IsMembers).SelectMany(p => properties[p].Kind == GpoSettingKind.Text ? properties[p].Elements() : []).Select(MemberText).ToList();
                return members.Count > 0 ? string.Join(", ", members) : string.Empty;
            case "GROUPS":
                // A local user: the rename or full name, and that a password is set (never the password).
                var user = new List<string>();
                if ((V("newName") is { } renamed ? Arrow(V("userName"), renamed) : V("fullName")) is { Length: > 0 } who)
                {
                    user.Add(who);
                }

                if (properties.TryGetValue("cpassword", out var password) && password.Kind == GpoSettingKind.Masked)
                {
                    var label = PropertyLabel("cpassword");
                    user.Add(char.ToLower(label[0], _l.Culture) + label[1..] + " " + _l[L.GroupPolicy.MaskedValue]);
                }

                return string.Join(", ", user);
            case "DRIVES":
                return Arrow(V("useLetter") == "0" || V("letter") is null ? null : V("letter") + ":", V("path"));
            case "FILES":
                return Arrow(V("fromPath"), V("targetPath"));
            case "SHORTCUTS":
                return Arrow(V("shortcutPath"), V("targetPath"));
            case "FOLDERS" or "NETWORKSHARES":
                return V("path") ?? string.Empty;
            case "REGISTRY":
                if (V("key") is null)
                {
                    return string.Empty;
                }

                // A masked value name (SNMP communities) is hidden, not the key's default value.
                var valueName = properties.TryGetValue("name", out var registryName) && registryName.Kind == GpoSettingKind.Masked
                    ? _l[L.GpoSettings.RegistryHiddenNames]
                    : V("name") ?? _l[L.GpoSettings.RegistryDefaultValue];
                var path = RegistryHive(V("hive")) + "\\" + V("key")!.Trim('\\') + "\\" + valueName;
                var data = properties.TryGetValue("value", out var registryValue) && registryValue.Kind == GpoSettingKind.Masked
                    ? _l[L.GroupPolicy.MaskedValue]
                    : V("value") ?? string.Empty;
                return path + " = " + (V("type") is { } registryType ? _l.Format(L.GpoSettings.RegistryValue, data, registryType) : data);
            case "ENVIRONMENTVARIABLES":
                return V("name") is { } variable ? variable + " = " + (V("value") ?? string.Empty) : string.Empty;
            case "INIFILES":
                return (V("path") is { } ini ? ini + ": " : string.Empty) + "[" + (V("section") ?? string.Empty) + "] " + (V("property") ?? string.Empty)
                    + (V("value") is { } iniValue ? " = " + iniValue : string.Empty);
            case "SCHEDULEDTASKS":
                var command = properties.Keys.FirstOrDefault(p => p.EndsWith("Exec/Command", StringComparison.OrdinalIgnoreCase)) is { } commandKey ? V(commandKey) : V("appName");
                var arguments = properties.Keys.FirstOrDefault(p => p.EndsWith("Exec/Arguments", StringComparison.OrdinalIgnoreCase)) is { } argumentsKey ? V(argumentsKey) : V("args");
                return command is null ? string.Empty : arguments is null ? command : command + " " + arguments;
            case "SERVICES":
                var mode = V("startupType") is { } startup ? StartupType(startup) : null;
                var serviceAction = V("serviceAction") is { } act ? ServiceAction(act) : null;
                return string.Join(", ", new[] { V("serviceName"), mode, serviceAction }.Where(s => s is not null));
            case "PRINTERS":
                return V("path") ?? V("localName") ?? V("ipAddress") ?? string.Empty;
            case "DATASOURCES":
                return V("driver") ?? string.Empty;
            default:
                return V("path") ?? V("targetPath") ?? V("value") ?? string.Empty;
        }
    }

    private static string RegistryHive(string? hive) => (hive ?? string.Empty).ToUpperInvariant() switch
    {
        "HKEY_LOCAL_MACHINE" => "HKLM",
        "HKEY_CURRENT_USER" => "HKCU",
        "HKEY_USERS" => "HKU",
        "HKEY_CLASSES_ROOT" => "HKCR",
        "HKEY_CURRENT_CONFIG" => "HKCC",
        _ => hive ?? string.Empty,
    };

    /// <summary>Properties that carry no meaning for the operator: XML bookkeeping of a task, a shortcut's shell id and unset keys.</summary>
    private static bool IsNoise(string property, string? value)
    {
        var last = property.LastIndexOfAny(['/', '@']) is var cut and >= 0 ? property[cut..] : property;
        return last.Equals("@id", StringComparison.OrdinalIgnoreCase) || last.Equals("@Context", StringComparison.OrdinalIgnoreCase)
            || last.Equals("@version", StringComparison.OrdinalIgnoreCase) || property.Equals("pidl", StringComparison.OrdinalIgnoreCase)
            || (property.Equals("iconIndex", StringComparison.OrdinalIgnoreCase) && value is "0" or "")
            || (property.Equals("shortcutKey", StringComparison.OrdinalIgnoreCase) && value is "0" or "");
    }

    private static bool IsMembers(string property) => property.Equals("Members", StringComparison.OrdinalIgnoreCase)
        || property.EndsWith("/Members", StringComparison.OrdinalIgnoreCase);

    /// <summary>A member of a GPP group (<c>Member action=ADD name=… sid=…</c>): "+ Name" or "− Name"; unsigned "Name" or "Name (Usuń)".</summary>
    private string MemberText(string canonical) => MemberText(canonical, signed: true);

    private string MemberText(string canonical, bool signed)
    {
        var member = CanonicalElement.Parse(canonical);
        var sid = member.Attribute("sid");
        var written = member.Attribute("name");
        var resolved = sid is { Length: > 0 } ? PrincipalName(sid) : null;
        var name = resolved is not null && !resolved.Equals(sid, StringComparison.OrdinalIgnoreCase) ? resolved
            : written is { Length: > 0 } ? written
            : sid ?? canonical;
        var remove = string.Equals(member.Attribute("action"), "REMOVE", StringComparison.OrdinalIgnoreCase);
        return signed ? (remove ? "− " : "+ ") + name
            : remove ? name + " (" + _l[L.SecuritySettings.GppMemberRemove] + ")"
            : name;
    }

    /// <summary>"komputer WAW-PC001 i grupa zabezpieczeń CONTOSO\GG_IT" – the item-level targeting in a sentence.</summary>
    private string FilterSummary(IReadOnlyList<string> filters)
    {
        var text = new System.Text.StringBuilder();
        foreach (var canonical in filters)
        {
            if (text.Length > 0)
            {
                var filter = CanonicalElement.Parse(canonical);
                text.Append(_l[string.Equals(filter.Attribute("bool"), "OR", StringComparison.OrdinalIgnoreCase) ? L.GpoSettings.PrefOr : L.GpoSettings.PrefAnd]);
            }

            text.Append(FilterText(canonical));
        }

        return text.ToString();
    }

    /// <summary>One targeting filter: "nie komputer WAW-PC001", "grupa zabezpieczeń CONTOSO\GG_IT".</summary>
    private string FilterText(string canonical)
    {
        var filter = CanonicalElement.Parse(canonical);
        var label = Text(L.GpoSettings.PreferenceFilterKey(filter.Name), filter.Name);
        var value = filter.Attribute("name") is { Length: > 0 } name ? name
            : filter.Attribute("sid") is { Length: > 0 } sid ? PrincipalName(sid)
            : filter.Attribute("query") ?? filter.Attribute("path") ?? filter.Attribute("value") ?? filter.Attribute("version")
              ?? filter.Attribute("min") ?? filter.Attribute("language") ?? filter.Text;
        var not = filter.Attribute("not") == "1" ? _l[L.GpoSettings.PrefNot] : string.Empty;
        return not + label + (string.IsNullOrEmpty(value) ? string.Empty : " " + value);
    }

    private void AddFlag(List<string> lines, string? value, string key)
    {
        if (value == "1")
        {
            lines.Add(_l[key]);
        }
    }
}

/// <summary>
/// A list element of <see cref="GppXmlParser"/> (<c>Name a=1 b=two words [children]</c>, attributes sorted): its element name,
/// attributes and text. An attribute's value runs up to the next <c> name=</c>.
/// </summary>
internal sealed class CanonicalElement
{
    private readonly Dictionary<string, string> _attributes = new(StringComparer.OrdinalIgnoreCase);

    private CanonicalElement(string name) => Name = name;

    public string Name { get; }

    /// <summary>Text after the attributes (an element's own text, or its children in brackets).</summary>
    public string Text { get; private set; } = string.Empty;

    public string? Attribute(string name) => _attributes.TryGetValue(name, out var value) ? value : null;

    public static CanonicalElement Parse(string canonical)
    {
        var text = canonical ?? string.Empty;
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var element = new CanonicalElement(space < 0 ? text : text[..space]);
        if (space < 0)
        {
            return element;
        }

        var rest = text[(space + 1)..];
        var children = rest.IndexOf(" [", StringComparison.Ordinal) is var bracket and >= 0 && rest.EndsWith(']') ? rest[bracket..].Trim() : null;
        if (children is not null)
        {
            rest = rest[..rest.IndexOf(" [", StringComparison.Ordinal)];
        }

        string? current = null;
        var trailing = new List<string>();
        foreach (var token in rest.Split(' '))
        {
            var equals = token.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && token[..equals].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ':'))
            {
                current = token[..equals];
                element._attributes[current] = token[(equals + 1)..];
            }
            else if (current is not null)
            {
                element._attributes[current] += " " + token;
            }
            else
            {
                trailing.Add(token);
            }
        }

        element.Text = children ?? string.Join(' ', trailing);
        return element;
    }
}
