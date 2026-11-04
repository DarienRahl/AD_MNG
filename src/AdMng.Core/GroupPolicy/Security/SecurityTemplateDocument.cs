using System.Globalization;
using System.Text;

namespace AdMng.Core.GroupPolicy.Security;

/// <summary>One line of <c>[Service General Setting]</c>: <c>"name",&lt;start mode&gt;,"&lt;SDDL&gt;"</c>.</summary>
/// <param name="Name">The service's short name (<c>Spooler</c>).</param>
/// <param name="StartMode">2 automatic, 3 manual, 4 disabled (0 boot and 1 system are kept as read).</param>
/// <param name="Security">The service's security descriptor as SDDL; empty when the template leaves it alone.</param>
public sealed record ServiceStartupSetting(string Name, int StartMode, string Security);

/// <summary>
/// The security template <c>Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf</c> as an editable document (K4, design note
/// docs/dev/k-computers-and-gpo.md 19): every line is kept as read, and an edit replaces, appends or removes only the
/// lines of the settings it changes, so sections and keys this editor does not know (file and registry ACLs, event log
/// settings, comments) survive a save unchanged. Keys are compared case-insensitively; sections of the same name are one
/// section (as SetupAPI reads them). A key that occurs more than once is shown with its last occurrence (as
/// <see cref="SecurityTemplate"/> reads it); which one a client applies is not specified, so a write leaves exactly one
/// line in all sections of that name. Written as secedit
/// writes it: UTF-16LE with BOM, CR LF. Content is untrusted (<see cref="GpoParseException"/> as in
/// <see cref="SecurityTemplate.Parse"/>).
/// </summary>
public sealed class SecurityTemplateDocument
{
    public const string SystemAccess = "System Access";
    public const string KerberosPolicy = "Kerberos Policy";
    public const string EventAudit = "Event Audit";
    public const string PrivilegeRights = "Privilege Rights";
    public const string GroupMembership = "Group Membership";
    public const string RegistryValues = "Registry Values";
    public const string ServiceGeneralSetting = "Service General Setting";

    private readonly List<string> _preamble = [];
    private readonly List<Section> _sections = [];

    private SecurityTemplateDocument()
    {
    }

    /// <summary>The document of a template; an empty or missing file is a new template.</summary>
    /// <exception cref="GpoParseException">The file is not valid text or has no section.</exception>
    public static SecurityTemplateDocument Parse(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            return New();
        }

        var text = GpoText.DecodeUnicodeText(content);
        var document = new SecurityTemplateDocument();
        Section? current = null;
        foreach (var raw in GpoText.Lines(text))
        {
            var line = raw.Trim();
            if (line.Length > 0 && line[0] == '[')
            {
                var close = line.IndexOf(']', StringComparison.Ordinal);
                if (close > 1)
                {
                    current = new Section(line[1..close].Trim(), raw);
                    document._sections.Add(current);
                    continue;
                }
            }

            if (current is null)
            {
                document._preamble.Add(raw);
            }
            else
            {
                current.Lines.Add(TemplateLine.Of(raw));
            }
        }

        if (document._sections.Count == 0)
        {
            if (text.Trim().Length > 0)
            {
                throw new GpoParseException("GptTmpl.inf has no section.");
            }

            return New();
        }

        return document;
    }

    /// <summary>A new template: <c>[Unicode]</c> and <c>[Version]</c> as secedit writes them.</summary>
    public static SecurityTemplateDocument New()
    {
        var document = new SecurityTemplateDocument();
        document.Set("Unicode", "Unicode", "yes");
        document.Set("Version", "signature", "\"$CHICAGO$\"");
        document.Set("Version", "Revision", "1");
        return document;
    }

    /// <summary>The section names in file order.</summary>
    public IReadOnlyList<string> Sections => [.. _sections.Select(s => s.Name)];

    /// <summary>The value of <paramref name="key"/> in <paramref name="section"/> (the last occurrence), null when absent.</summary>
    public string? Get(string section, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string? value = null;
        foreach (var line in Lines(section))
        {
            if (line.Value is not null && line.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = line.Value;
            }
        }

        return value;
    }

    /// <summary>The <c>key = value</c> lines of a section: one per key (the last occurrence, in its position).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Values(string section)
    {
        var last = new Dictionary<string, (int Index, string Key, string Value)>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var line in Lines(section))
        {
            if (line.Value is not null && line.Key.Length > 0)
            {
                last[line.Key] = (index, line.Key, line.Value);
            }

            index++;
        }

        return [.. last.Values.OrderBy(v => v.Index).Select(v => new KeyValuePair<string, string>(v.Key, v.Value))];
    }

    /// <summary>
    /// Sets <c>key = value</c>: the last line of the key is replaced (earlier duplicates are removed), else the line is
    /// appended to the section (created at the end of the file when missing). Nothing changes when the value is already so.
    /// </summary>
    public void Set(string section, string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.Contains('=', StringComparison.Ordinal) || HasLineBreak(key) || HasLineBreak(value))
        {
            throw new ArgumentException("A template key must not contain '=' and neither key nor value a line break.", nameof(key));
        }

        Replace(section, line => line.Value is not null && line.Key.Equals(key, StringComparison.OrdinalIgnoreCase),
            TemplateLine.Of(key + " = " + value), line => line.Value == value);
    }

    /// <summary>Removes every line of the key (a section left without entries is removed). True when something was removed.</summary>
    public bool Remove(string section, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return RemoveWhere(section, line => line.Value is not null && line.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    // ---- User rights ----------------------------------------------------------------------------------------------------

    /// <summary><c>[Privilege Rights]</c>: right constant → its holders (SIDs without <c>*</c>, or names as written).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetPrivilegeRights() =>
        Values(PrivilegeRights).ToDictionary(v => v.Key, v => SecurityTemplate.Principals(v.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>Defines a user right with exactly these holders (empty: nobody), or removes it (null: not defined).</summary>
    public void SetPrivilegeRight(string right, IReadOnlyCollection<string>? principals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        if (principals is null)
        {
            Remove(PrivilegeRights, right);
        }
        else
        {
            Set(PrivilegeRights, right, FormatPrincipals(principals));
        }
    }

    // ---- Restricted Groups ----------------------------------------------------------------------------------------------

    /// <summary><c>[Group Membership]</c> entries (Members and MemberOf lines; other relations are kept but not listed).</summary>
    public IReadOnlyList<RestrictedGroupEntry> GetRestrictedGroups()
    {
        var result = new List<RestrictedGroupEntry>();
        var seen = new Dictionary<(string, RestrictedGroupRelation), int>();
        foreach (var (key, value) in Values(GroupMembership))
        {
            if (!SecurityTemplate.TrySplitGroupMembershipKey(key, out var group, out var relation, out _) || group.Length == 0 || relation is not { } known)
            {
                continue;
            }

            var id = (group.ToUpperInvariant(), known);
            var entry = new RestrictedGroupEntry(group, known, SecurityTemplate.Principals(value));
            if (seen.TryGetValue(id, out var at))
            {
                result[at] = entry; // *S-1-5-32-544__Members and *s-1-5-32-544__members: the last one wins
            }
            else
            {
                seen[id] = result.Count;
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Sets one side of a Restricted Groups entry: the members (exactly these; empty: no members) or the groups the group
    /// becomes a member of; null removes that line. The group is written as <c>*SID</c> or the name as given.
    /// </summary>
    public void SetRestrictedGroup(string group, RestrictedGroupRelation relation, IReadOnlyCollection<string>? principals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var normalized = SecurityTemplate.Principal(group);
        bool Same(TemplateLine line) => line.Value is not null
            && SecurityTemplate.TrySplitGroupMembershipKey(line.Key, out var g, out var r, out _)
            && r == relation && g.Equals(normalized, StringComparison.OrdinalIgnoreCase);

        if (principals is null)
        {
            RemoveWhere(GroupMembership, Same);
            return;
        }

        var key = FormatPrincipal(normalized) + "__" + (relation == RestrictedGroupRelation.Members ? "Members" : "Memberof");
        var value = FormatPrincipals(principals);
        Replace(GroupMembership, Same, TemplateLine.Of(key + " = " + value), line => line.Value == value);
    }

    // ---- Registry values (security options) ---------------------------------------------------------------------------

    /// <summary><c>[Registry Values]</c>: secedit path (<c>MACHINE\System\…\Name</c>) → <c>&lt;type&gt;,&lt;data&gt;</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> GetRegistryValues() => Values(RegistryValues);

    /// <summary>Sets a registry value: <c>4,1</c> (REG_DWORD), <c>1,"text"</c> (REG_SZ), <c>7,a,b</c> (REG_MULTI_SZ); null removes it.</summary>
    public void SetRegistryValue(string path, string? typeAndData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (typeAndData is null)
        {
            Remove(RegistryValues, path);
        }
        else
        {
            Set(RegistryValues, path, typeAndData);
        }
    }

    /// <summary><c>4,&lt;number&gt;</c>.</summary>
    public static string DwordValue(uint value) => "4," + value.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>1,"text"</c> (quotes doubled).</summary>
    public static string StringValue(string value) => "1," + Quote(value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>
    /// <c>7,line1,line2</c>: a line with a comma, a quote, a semicolon (an INF comment when unquoted) or spaces at either
    /// end is quoted.
    /// </summary>
    public static string MultiStringValue(IEnumerable<string> lines) =>
        "7," + string.Join(",", (lines ?? throw new ArgumentNullException(nameof(lines))).Select(l => NeedsQuotes(l) ? Quote(l) : l));

    private static bool NeedsQuotes(string value) =>
        value.Length > 0 && (value.IndexOfAny([',', '"', ';']) >= 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    // ---- Services -----------------------------------------------------------------------------------------------------

    /// <summary><c>[Service General Setting]</c> lines (the last line of a service wins; malformed lines are kept but not listed).</summary>
    public IReadOnlyList<ServiceStartupSetting> GetServices()
    {
        var result = new List<ServiceStartupSetting>();
        var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(ServiceGeneralSetting))
        {
            if (ParseService(line) is not { } service)
            {
                continue;
            }

            if (at.TryGetValue(service.Name, out var index))
            {
                result[index] = service;
            }
            else
            {
                at[service.Name] = result.Count;
                result.Add(service);
            }
        }

        return result;
    }

    /// <summary>Sets the start mode (and SDDL) of a service, or removes its line (null).</summary>
    public void SetService(string name, ServiceStartupSetting? setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        bool Same(TemplateLine line) => ParseService(line) is { } s && s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase);
        if (setting is null)
        {
            RemoveWhere(ServiceGeneralSetting, Same);
            return;
        }

        if (HasLineBreak(setting.Name) || HasLineBreak(setting.Security))
        {
            throw new ArgumentException("A service line must not contain a line break.", nameof(setting));
        }

        var raw = Quote(setting.Name.Trim()) + "," + setting.StartMode.ToString(CultureInfo.InvariantCulture) + "," + Quote(setting.Security);
        Replace(ServiceGeneralSetting, Same, TemplateLine.Of(raw), line => ParseService(line) == setting with { Name = setting.Name.Trim() });
    }

    // ---- Serialization ------------------------------------------------------------------------------------------------

    /// <summary>The text of the template (CR LF line ends).</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        foreach (var line in _preamble)
        {
            text.Append(line).Append("\r\n");
        }

        foreach (var section in _sections)
        {
            text.Append(section.Header).Append("\r\n");
            foreach (var line in section.Lines)
            {
                text.Append(line.Raw).Append("\r\n");
            }
        }

        return text.ToString();
    }

    /// <summary>UTF-16LE with BOM, as secedit and GPMC write the template.</summary>
    public byte[] ToBytes() => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(ToText())];

    /// <summary>Never content.</summary>
    public override string ToString() => $"GptTmpl.inf ({_sections.Count} sections)";

    /// <summary><c>*S-1-5-32-544</c> for a SID, the name as given otherwise.</summary>
    public static string FormatPrincipal(string principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var text = SecurityTemplate.Principal(principal);
        return SecurityTemplate.IsSid(text) ? "*" + text : text;
    }

    private static string FormatPrincipals(IEnumerable<string> principals)
    {
        var list = principals.Select(p => p?.Trim() ?? string.Empty).Where(p => p.Length > 0).ToList();
        if (list.Any(p => p.IndexOfAny([',', '"', ';', '=']) >= 0 || HasLineBreak(p)))
        {
            throw new ArgumentException("A principal must not contain a comma, a quote, a semicolon, '=' or a line break.", nameof(principals));
        }

        return string.Join(",", list.Select(FormatPrincipal).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static ServiceStartupSetting? ParseService(TemplateLine line)
    {
        if (line.Value is not null || line.Key.Length == 0 || line.Key[0] == ';')
        {
            return null;
        }

        var parts = IniReader.SplitCommaList(line.Key);
        return parts.Count >= 2 && parts[0].Length > 0 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mode)
            ? new ServiceStartupSetting(parts[0], mode, parts.Count > 2 ? parts[2] : string.Empty)
            : null;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool HasLineBreak(string value) => value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal);

    private IEnumerable<TemplateLine> Lines(string section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return _sections.Where(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase)).SelectMany(s => s.Lines);
    }

    /// <summary>
    /// Removes the matching lines of every section of that name. Only a section this document created (not one that was
    /// read) is removed when it is left without lines, so an edit and its undo give back the bytes that were read and the
    /// comments of a section survive.
    /// </summary>
    private bool RemoveWhere(string section, Func<TemplateLine, bool> match)
    {
        var removed = false;
        foreach (var target in _sections.Where(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (target.Lines.RemoveAll(l => match(l)) == 0)
            {
                continue;
            }

            removed = true;
            if (target.Created && target.Lines.All(l => l.IsBlankOrComment && l.Raw.Trim().Length == 0))
            {
                _sections.Remove(target);
            }
        }

        return removed;
    }

    /// <summary>
    /// Leaves exactly one line for the setting across every section of that name (SetupAPI merges them; which duplicate
    /// a client applies is not specified, so none is left): the last match is replaced and the others are removed, or the
    /// line is appended to the last section of that name. Nothing changes when the only line already is the value.
    /// </summary>
    private void Replace(string section, Func<TemplateLine, bool> same, TemplateLine replacement, Func<TemplateLine, bool> unchanged)
    {
        var matches = new List<(Section Section, int Index)>();
        foreach (var target in _sections.Where(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase)))
        {
            for (var i = 0; i < target.Lines.Count; i++)
            {
                if (same(target.Lines[i]))
                {
                    matches.Add((target, i));
                }
            }
        }

        if (matches.Count == 1 && unchanged(matches[0].Section.Lines[matches[0].Index]))
        {
            return;
        }

        if (matches.Count == 0)
        {
            Append(Require(section), replacement);
            return;
        }

        var (lastSection, lastIndex) = matches[^1];
        lastSection.Lines[lastIndex] = replacement;
        foreach (var (target, index) in matches.Take(matches.Count - 1).Reverse())
        {
            target.Lines.RemoveAt(index); // reversed: a later index of the same section goes first
        }
    }

    /// <summary>The last section of that name (secedit merges sections of the same name), created at the end when missing.</summary>
    private Section Require(string section)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        var existing = _sections.LastOrDefault(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var created = new Section(section, "[" + section + "]") { Created = true };
        _sections.Add(created);
        return created;
    }

    /// <summary>After the section's last entry (blank lines and comments at its end stay at the end).</summary>
    private static void Append(Section section, TemplateLine line)
    {
        var at = section.Lines.Count;
        while (at > 0 && section.Lines[at - 1].IsBlankOrComment)
        {
            at--;
        }

        section.Lines.Insert(at, line);
    }

    private sealed class Section(string name, string header)
    {
        public string Name { get; } = name;

        public string Header { get; } = header;

        public List<TemplateLine> Lines { get; } = [];

        /// <summary>Created by an edit (not read from the file): removed again when an edit empties it.</summary>
        public bool Created { get; init; }
    }

    /// <summary>A line as read: <c>key = value</c> (Value set), a line without '=' (Key = the trimmed line), or blank/comment.</summary>
    private sealed record TemplateLine(string Raw, string Key, string? Value, bool IsBlankOrComment)
    {
        public static TemplateLine Of(string raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                return new TemplateLine(raw, string.Empty, null, true);
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            return equals < 0 ? new TemplateLine(raw, line, null, false) : new TemplateLine(raw, line[..equals].Trim(), line[(equals + 1)..].Trim(), false);
        }
    }
}
