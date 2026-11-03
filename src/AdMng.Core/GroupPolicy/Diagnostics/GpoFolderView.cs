using System.Buffers.Binary;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>
/// The files of one GPO folder parsed once for the rules (lazily, with the existing parsers): the security template, the
/// advanced audit policy, the GPP local groups, the normalized settings and both Registry.pol documents. A file that cannot
/// be parsed is null here (its <c>File/&lt;path&gt;</c> marker in <see cref="Settings"/> makes it an
/// <see cref="GpoFindingCodes.UnreadableFile"/>). In memory only.
/// </summary>
internal sealed class GpoFolderView(GpoFolderContent content)
{
    private SecurityTemplateDocument? _template;
    private bool _templateParsed;
    private AuditPolicyDocument? _audit;
    private bool _auditParsed;
    private IReadOnlyList<GppLocalGroup>? _localGroups;
    private ModelingGpoContent? _modeling;

    public GpoFolderContent Content { get; } = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary><c>GptTmpl.inf</c>; null when the GPO has none or it cannot be parsed.</summary>
    public SecurityTemplateDocument? Template
    {
        get
        {
            if (!_templateParsed)
            {
                _templateParsed = true;
                if (File(SecurityTemplate.RelativePath) is { Length: > 0 } bytes)
                {
                    try
                    {
                        _template = SecurityTemplateDocument.Parse(bytes);
                    }
                    catch (GpoParseException)
                    {
                        _template = null;
                    }
                }
            }

            return _template;
        }
    }

    /// <summary><c>audit.csv</c>; null when the GPO has none or it cannot be parsed.</summary>
    public AuditPolicyDocument? Audit
    {
        get
        {
            if (!_auditParsed)
            {
                _auditParsed = true;
                if (File(AuditPolicyDocument.RelativePath) is { Length: > 0 } bytes)
                {
                    try
                    {
                        _audit = AuditPolicyDocument.Parse(bytes);
                    }
                    catch (GpoParseException)
                    {
                        _audit = null;
                    }
                }
            }

            return _audit;
        }
    }

    /// <summary>The local group items of the computer's <c>Groups.xml</c> (empty when there is none or it cannot be parsed).</summary>
    public IReadOnlyList<GppLocalGroup> LocalGroups
    {
        get
        {
            if (_localGroups is null)
            {
                _localGroups = [];
                if (File(GppGroupsParser.RelativePath) is { Length: > 0 } bytes)
                {
                    try
                    {
                        _localGroups = GppGroupsParser.Parse(bytes);
                    }
                    catch (GpoParseException)
                    {
                        _localGroups = [];
                    }
                }
            }

            return _localGroups;
        }
    }

    /// <summary>The folder as the modeller takes it (normalized settings, both Registry.pol documents).</summary>
    public ModelingGpoContent Modeling => _modeling ??= GroupPolicyModelReader.FromFolder(Content);

    /// <summary>The normalized settings (<see cref="GroupPolicyReader.Settings"/>), unreadable files as <c>File/&lt;path&gt;</c> markers.</summary>
    public IReadOnlyList<GpoSetting> Settings => Modeling.Settings;

    /// <summary>
    /// The user (<c>User/</c>) or computer (<c>Machine/</c>) part holds settings: a normalized setting of the part (comments
    /// aside), a file of the part that cannot be parsed, or a file of the part no parser reads (folder redirection,
    /// software installation …), which may hold settings.
    /// </summary>
    public bool HasSettings(bool user)
    {
        var prefix = user ? "User/" : "Computer/";
        var folder = user ? "User/" : "Machine/";
        return Settings.Any(s => s.Category != GpoSettingCategory.Comments
                && (s.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || s.Key.StartsWith("File/" + folder, StringComparison.OrdinalIgnoreCase)))
            || Content.Files.Keys.Any(path => path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && GpoFileClassifier.Classify(path).Role == GpoFileRole.Inventory);
    }

    /// <summary>A file of the folder (paths compared without case, as on SYSVOL), null when absent.</summary>
    public byte[]? File(string relativePath) => Content.Files.TryGetValue(relativePath, out var bytes) ? bytes : null;

    /// <summary>The loopback mode the computer's Registry.pol sets (<c>UserPolicyMode</c>): 1 merge, 2 replace, 0 off; null when not set.</summary>
    public uint? LoopbackMode
    {
        get
        {
            if (Modeling.MachineRegistry is not { } registry)
            {
                return null;
            }

            var state = registry.View.Get(GroupPolicyModeler.LoopbackKey, GroupPolicyModeler.LoopbackValueName);
            if (state.Kind == RegistryValueStateKind.Missing)
            {
                return null;
            }

            return state is { IsSet: true, Entry: { Type: RegistryFormat.RegDword, Data.Length: >= 4 } entry }
                ? BinaryPrimitives.ReadUInt32LittleEndian(entry.Data)
                : 0;
        }
    }

    /// <summary>
    /// Every SID the rights and groups name: the holders of <c>[Privilege Rights]</c>, the groups and principals of
    /// <c>[Group Membership]</c>, the groups and members of the GPP local groups.
    /// </summary>
    public IEnumerable<string> PrincipalSids()
    {
        if (Template is { } template)
        {
            foreach (var (_, holders) in template.GetPrivilegeRights())
            {
                foreach (var holder in holders.Where(SecurityTemplate.IsSid))
                {
                    yield return holder.ToUpperInvariant();
                }
            }

            foreach (var entry in template.GetRestrictedGroups())
            {
                foreach (var principal in entry.Principals.Prepend(entry.Group).Where(SecurityTemplate.IsSid))
                {
                    yield return principal.ToUpperInvariant();
                }
            }
        }

        foreach (var group in LocalGroups)
        {
            if (group.GroupSid is { } groupSid)
            {
                yield return groupSid;
            }

            foreach (var member in group.Members)
            {
                if (member.Sid is { } sid)
                {
                    yield return sid;
                }
            }
        }
    }
}
