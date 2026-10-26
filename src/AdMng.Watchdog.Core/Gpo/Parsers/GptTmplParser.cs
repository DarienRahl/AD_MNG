namespace AdMng.Watchdog.Core.Gpo.Parsers;

/// <summary>
/// The security template <c>Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf</c> ([MS-GPSB] 2.2, UTF-16LE with BOM).
/// Keys are <c>Computer/Security/&lt;section&gt;/…</c>:
/// <list type="bullet">
/// <item><c>[System Access]</c>, <c>[Kerberos Policy]</c> – <c>key = value</c> (account policy, sensitive);</item>
/// <item><c>[Event Audit]</c> – 0–3 as audit names (sensitive);</item>
/// <item><c>[Privilege Rights]</c> – <c>SeDebugPrivilege = *S-1-5-32-544,…</c>: sorted list of SIDs (the <c>*</c> removed) or
/// names (2.2.7);</item>
/// <item><c>[Group Membership]</c> – <c>*&lt;group SID&gt;__Members</c> / <c>__Memberof</c>: lists (Restricted Groups, 2.2.6);</item>
/// <item><c>[Registry Values]</c> – <c>MACHINE\…\Name=&lt;type&gt;,&lt;data&gt;</c> → <c>Registry Values/HKLM\…!Name</c> =
/// <c>REG_DWORD:1</c> (2.2.5; masking of 8.5);</item>
/// <item><c>[Service General Setting]</c> – <c>"name",&lt;start mode&gt;,"&lt;SDDL&gt;"</c> → <c>Services/name/StartupMode</c>,
/// <c>…/Security</c>;</item>
/// <item><c>[Registry Keys]</c>, <c>[File Security]</c> – <c>"path",&lt;mode&gt;,"&lt;SDDL&gt;"</c> → <c>…/path/Mode</c>,
/// <c>…/path/Security</c>;</item>
/// <item><c>[System Log]</c>, <c>[Security Log]</c>, <c>[Application Log]</c> and any other section – <c>key = value</c>.</item>
/// </list>
/// <c>[Unicode]</c>, <c>[Version]</c> and <c>[Profile Description]</c> are not settings. A later duplicate key wins.
/// </summary>
public static class GptTmplParser
{
    private const string Prefix = "Computer/Security/";

    public static void Parse(ReadOnlySpan<byte> content, GpoSettingSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var text = GpoText.DecodeUnicodeText(content);
        var sawSection = false;
        foreach (var line in IniReader.Read(text))
        {
            sawSection = true;
            var section = line.Section;
            switch (section.ToUpperInvariant())
            {
                case "UNICODE":
                case "VERSION":
                case "PROFILE DESCRIPTION":
                    break;
                case "SYSTEM ACCESS":
                    KeyValue(sink, "System Access", line, GpoSettingCategory.AccountPolicy);
                    break;
                case "KERBEROS POLICY":
                    KeyValue(sink, "Kerberos Policy", line, GpoSettingCategory.AccountPolicy);
                    break;
                case "EVENT AUDIT":
                    sink.Text(Prefix + "Event Audit/" + line.Key, AuditValues.Name(line.Value ?? string.Empty), GpoSettingCategory.AuditPolicy);
                    break;
                case "PRIVILEGE RIGHTS":
                    sink.List(Prefix + "Privilege Rights/" + line.Key, Principals(line.Value), GpoSettingCategory.PrivilegeRights);
                    break;
                case "GROUP MEMBERSHIP":
                    GroupMembership(sink, line);
                    break;
                case "REGISTRY VALUES":
                    RegistryValue(sink, line);
                    break;
                case "SERVICE GENERAL SETTING":
                    Service(sink, line.Raw);
                    break;
                case "REGISTRY KEYS":
                    ObjectSecurity(sink, "Registry Keys", line.Raw, registry: true);
                    break;
                case "FILE SECURITY":
                    ObjectSecurity(sink, "File Security", line.Raw, registry: false);
                    break;
                case "SYSTEM LOG":
                case "SECURITY LOG":
                case "APPLICATION LOG":
                    KeyValue(sink, section, line, GpoSettingCategory.EventLog);
                    break;
                default:
                    KeyValue(sink, section, line, GpoSettingCategory.Other);
                    break;
            }
        }

        if (!sawSection && text.Trim().Length > 0)
        {
            throw new GpoParseException("GptTmpl.inf has no section.");
        }
    }

    /// <summary>Elements of a principal list: <c>*S-1-5-32-544</c> → <c>S-1-5-32-544</c>, names kept as written.</summary>
    internal static IEnumerable<string> Principals(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : IniReader.SplitCommaList(value).Select(Principal).Where(p => p.Length > 0);

    private static string Principal(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('*'))
        {
            text = text[1..].Trim();
        }

        return text.StartsWith("S-", StringComparison.OrdinalIgnoreCase) && text.Skip(2).All(c => char.IsAsciiDigit(c) || c == '-')
            ? text.ToUpperInvariant()
            : text;
    }

    private static void KeyValue(GpoSettingSink sink, string section, IniLine line, GpoSettingCategory category) =>
        sink.Text(Prefix + section + "/" + line.Key, IniReader.Unquote(line.Value ?? string.Empty), category);

    private static void GroupMembership(GpoSettingSink sink, IniLine line)
    {
        var separator = line.Key.LastIndexOf("__", StringComparison.Ordinal);
        if (separator <= 0)
        {
            KeyValue(sink, "Group Membership", line, GpoSettingCategory.GroupMembership);
            return;
        }

        var group = Principal(line.Key[..separator]);
        var relation = line.Key[(separator + 2)..];
        var name = relation.Equals("Members", StringComparison.OrdinalIgnoreCase) ? "Members"
            : relation.Equals("Memberof", StringComparison.OrdinalIgnoreCase) ? "MemberOf"
            : relation;
        sink.List(Prefix + "Group Membership/" + group + "/" + name, Principals(line.Value), GpoSettingCategory.GroupMembership);
    }

    private static void RegistryValue(GpoSettingSink sink, IniLine line)
    {
        var path = RegistryFormat.FromSecEditPath(line.Key);
        var separator = path.LastIndexOf('\\');
        var keyPath = separator < 0 ? path : path[..separator];
        var valueName = separator < 0 ? string.Empty : path[(separator + 1)..];
        var category = GpoSettingCategories.ForRegistryKey(keyPath);
        var hideName = GpoSecretSettings.HidesValueNames(keyPath);
        var key = Prefix + "Registry Values/" + keyPath + "!" + (hideName ? "*" : valueName);
        if (GpoSecretSettings.IsSecretRegistryValue(keyPath, valueName))
        {
            sink.Masked(key, category);
            return;
        }

        var value = line.Value ?? string.Empty;
        var comma = value.IndexOf(',', StringComparison.Ordinal);
        var typeText = comma < 0 ? value : value[..comma];
        var data = comma < 0 ? string.Empty : value[(comma + 1)..];
        if (!uint.TryParse(typeText.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var type))
        {
            sink.Text(key, value, category);
            return;
        }

        var canonical = type == RegistryFormat.RegMultiSz
            ? string.Join(",", IniReader.SplitCommaList(data).Select(GpoValues.Quote))
            : IniReader.Unquote(data);
        sink.Text(key, RegistryFormat.TypeName(type) + ":" + canonical, category);
    }

    private static void Service(GpoSettingSink sink, string raw)
    {
        var parts = IniReader.SplitCommaList(raw);
        if (parts.Count < 2 || parts[0].Length == 0)
        {
            throw new GpoParseException("Invalid [Service General Setting] line.");
        }

        var name = Prefix + "Services/" + parts[0];
        sink.Text(name + "/StartupMode", parts[1] switch
        {
            "0" => "Boot",
            "1" => "System",
            "2" => "Automatic",
            "3" => "Manual",
            "4" => "Disabled",
            var other => other,
        }, GpoSettingCategory.Services);
        sink.Text(name + "/Security", parts.Count > 2 ? parts[2] : string.Empty, GpoSettingCategory.Services);
    }

    private static void ObjectSecurity(GpoSettingSink sink, string section, string raw, bool registry)
    {
        var parts = IniReader.SplitCommaList(raw);
        if (parts.Count < 2 || parts[0].Length == 0)
        {
            throw new GpoParseException("Invalid [" + section + "] line.");
        }

        var path = registry ? RegistryFormat.FromSecEditPath(parts[0]) : parts[0];
        var name = Prefix + section + "/" + path;
        sink.Text(name + "/Mode", parts[1] switch
        {
            "0" => "Propagate",
            "1" => "DoNotReplace",
            "2" => "Replace",
            var other => other,
        }, GpoSettingCategory.FileRegistrySecurity);
        sink.Text(name + "/Security", parts.Count > 2 ? parts[2] : string.Empty, GpoSettingCategory.FileRegistrySecurity);
    }
}
