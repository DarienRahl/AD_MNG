using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>
/// The per-kind writers of the K7 copy, remove and merge planners (docs/dev/k-computers-and-gpo.md 21.5): one target GPO
/// folder read now, edited in memory document by document (Registry.pol, GptTmpl.inf, audit.csv, preference files) with
/// the minimal-edit rules of K3/K4, every new byte taken from a source folder read now. Units name what they cover through
/// their settings' keys (<see cref="GpoSettingKey.Parse"/>), their file (<see cref="GpoSettingUnit.Source"/>), item id and
/// policy id. In memory only (rule 1); never logs content.
/// </summary>
internal sealed class GpoSettingsWriter
{
    private readonly GpoFolderContent _target;
    private readonly PolicyDefinitionSet? _set;
    private readonly PolicyOwnershipIndex _ownership;
    private readonly Func<string, string?> _sidOf;
    private readonly DateTime _changed;
    private readonly Dictionary<string, object> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(GpoFolderContent, string), object?> _sources = [];
    private readonly HashSet<string> _copiedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="target">The target folder as read now (its bytes are the compare-and-swap of every write).</param>
    /// <param name="set">The definition set the units were built with (policy units need it).</param>
    /// <param name="sidOf">A principal's SID (well-known names, SIDs and the names resolved for this plan), null when unknown.</param>
    /// <param name="changed">The <c>changed</c> time of cloned preference items (local time).</param>
    public GpoSettingsWriter(GpoFolderContent target, PolicyDefinitionSet? set, Func<string, string?> sidOf, DateTime changed)
    {
        _target = target;
        _set = set;
        _ownership = set is null ? PolicyOwnershipIndex.Empty : PolicyOwnershipIndex.Build(set);
        _sidOf = sidOf;
        _changed = changed;
    }

    /// <summary>The files the copies wrote into (the planner adds their extension pairs).</summary>
    public IReadOnlyCollection<string> CopiedPaths => _copiedPaths;

    /// <summary>Changed files: path, the bytes read now (null: new file), the new bytes. Unchanged documents are left out.</summary>
    public IReadOnlyList<(string Path, byte[]? Old, byte[] New)> ChangedFiles()
    {
        var result = new List<(string, byte[]?, byte[])>();
        foreach (var (path, document) in _documents.Where(d => _touched.Contains(d.Key)).OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            var old = _target.Files.TryGetValue(path, out var bytes) ? bytes : null;
            var next = document switch
            {
                RegistryHalf registry => registry.ToBytes(),
                SecurityTemplateDocument template => template.ToBytes(),
                AuditPolicyDocument audit => audit.ToBytes(),
                GppPreferenceDocument preferences => preferences.ToBytes(),
                _ => null,
            };
            if (next is null || (old is not null && old.AsSpan().SequenceEqual(next)))
            {
                continue;
            }

            // A Registry.pol that did not exist and still holds nothing is not written.
            if (old is null && document is RegistryHalf { Entries.Count: 0 })
            {
                continue;
            }

            result.Add((path, old, next));
        }

        return result;
    }

    /// <summary>The Registry.pol document of a half as it is now (null: neither read nor written).</summary>
    public RegistryPolicyDocument? Registry(string path) =>
        _documents.TryGetValue(path, out var document) && document is RegistryHalf half ? new RegistryPolicyDocument(half.Entries) : null;

    // ---- Checks -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Why the unit cannot be copied from <paramref name="source"/> into the target (an issue code), null when it can: no
    /// definition set for a policy, a raw value a target policy owns, a source value that cannot be found again, a target
    /// file that cannot be parsed.
    /// </summary>
    public string? CheckCopy(GpoSettingUnit unit, GpoFolderContent source)
    {
        try
        {
            switch (unit.Kind)
            {
                case GpoSettingUnitKind.AdministrativeTemplate:
                    return Definition(unit) is null ? IssueCodes.GpoSettingNoDefinitions
                        : SourcePolicy(unit, source) is null ? IssueCodes.GpoSettingNotFound : null;
                case GpoSettingUnitKind.RegistryValue or GpoSettingUnitKind.RegistryKey:
                {
                    var path = RegistryPath(unit);
                    var entries = SourceRegistryEntries(unit, source);
                    if (entries.Count == 0)
                    {
                        return IssueCodes.GpoSettingNotFound;
                    }

                    if (_set is not null)
                    {
                        var configured = AdministrativeTemplatePolicies.Configured(new RegistryPolicyDocument(RegistryDocument(path).Entries), _set, PolicyClassOf(unit));
                        if (entries.Any(configured.Covers))
                        {
                            return IssueCodes.GpoSettingPolicyOwned;
                        }
                    }

                    return null;
                }

                case GpoSettingUnitKind.SecurityValue:
                {
                    var info = Info(unit);
                    _ = TemplateDocument();
                    return info.Section is null || info.Name is null || SourceTemplate(source)?.Get(info.Section, info.Name) is null ? IssueCodes.GpoSettingNotFound : null;
                }

                case GpoSettingUnitKind.SecurityRegistryValue:
                    _ = TemplateDocument();
                    return SourceRegistryValue(unit, source) is null ? IssueCodes.GpoSettingNotFound : null;
                case GpoSettingUnitKind.UserRight:
                    _ = TemplateDocument();
                    return SourceRight(unit, source) is null ? IssueCodes.GpoSettingNotFound : null;
                case GpoSettingUnitKind.RestrictedGroup:
                    _ = TemplateDocument();
                    return Relation(unit) is null ? IssueCodes.GpoSettingUnsupported : SourceGroup(unit, source) is null ? IssueCodes.GpoSettingNotFound : null;
                case GpoSettingUnitKind.Service:
                    _ = TemplateDocument();
                    return SourceService(unit, source) is null ? IssueCodes.GpoSettingNotFound : null;
                case GpoSettingUnitKind.AuditSubcategory:
                {
                    _ = AuditDocument();
                    if (Subcategory(unit) is not { } guid)
                    {
                        return IssueCodes.GpoSettingUnsupported;
                    }

                    return SourceAudit(source)?.GetSubcategories().ContainsKey(guid) == true ? null : IssueCodes.GpoSettingNotFound;
                }

                case GpoSettingUnitKind.Preference:
                {
                    var item = SourceItem(unit, source);
                    if (item is null)
                    {
                        return IssueCodes.GpoSettingNotFound;
                    }

                    _ = PreferenceDocument(PreferencePath(unit), item.Value.Document);
                    return null;
                }

                default:
                    return IssueCodes.GpoSettingUnsupported;
            }
        }
        catch (GpoParseException)
        {
            return IssueCodes.GpoSettingUnsupported;
        }
    }

    /// <summary>Why the target unit cannot be removed (an issue code), null when it can.</summary>
    public string? CheckRemove(GpoSettingUnit unit)
    {
        try
        {
            switch (unit.Kind)
            {
                case GpoSettingUnitKind.AdministrativeTemplate:
                    return Definition(unit) is null ? IssueCodes.GpoSettingNoDefinitions : null;
                case GpoSettingUnitKind.RegistryValue or GpoSettingUnitKind.RegistryKey:
                    _ = RegistryDocument(RegistryPath(unit));
                    return null;
                case GpoSettingUnitKind.SecurityValue or GpoSettingUnitKind.SecurityRegistryValue or GpoSettingUnitKind.UserRight
                    or GpoSettingUnitKind.Service:
                    _ = TemplateDocument();
                    return null;
                case GpoSettingUnitKind.RestrictedGroup:
                    _ = TemplateDocument();
                    return Relation(unit) is null ? IssueCodes.GpoSettingUnsupported : null;
                case GpoSettingUnitKind.AuditSubcategory:
                    _ = AuditDocument();
                    return Subcategory(unit) is null ? IssueCodes.GpoSettingUnsupported : null;
                case GpoSettingUnitKind.Preference:
                    return PreferenceTarget(PreferencePath(unit))?.Find(unit.ItemId ?? string.Empty) is null ? IssueCodes.GpoSettingNotFound : null;
                default:
                    return IssueCodes.GpoSettingUnsupported;
            }
        }
        catch (GpoParseException)
        {
            return IssueCodes.GpoSettingUnsupported;
        }
    }

    // ---- Remove -----------------------------------------------------------------------------------------------------------

    /// <summary>Removes a unit of the target (one occurrence; checked with <see cref="CheckRemove"/>).</summary>
    public void Remove(GpoSettingUnit unit)
    {
        _touched.Add(PathOf(unit));
        switch (unit.Kind)
        {
            case GpoSettingUnitKind.AdministrativeTemplate:
                RemovePolicy(unit);
                break;
            case GpoSettingUnitKind.RegistryValue or GpoSettingUnitKind.RegistryKey:
            {
                var document = RegistryDocument(RegistryPath(unit));
                foreach (var setting in unit.Settings)
                {
                    document.Forget(RegistryTargetOf(setting));
                }

                break;
            }

            case GpoSettingUnitKind.SecurityValue:
            {
                var info = Info(unit);
                if (info.Section is not null && info.Name is not null)
                {
                    TemplateDocument().Remove(info.Section, info.Name);
                }

                break;
            }

            case GpoSettingUnitKind.SecurityRegistryValue:
                foreach (var path in TemplateRegistryPaths(TemplateDocument(), unit))
                {
                    TemplateDocument().SetRegistryValue(path, null);
                }

                break;
            case GpoSettingUnitKind.UserRight:
                if (Info(unit).Name is { } right)
                {
                    TemplateDocument().SetPrivilegeRight(right, null);
                }

                break;
            case GpoSettingUnitKind.RestrictedGroup:
                RemoveGroup(Info(unit).Name ?? string.Empty, Relation(unit)!.Value);
                break;
            case GpoSettingUnitKind.Service:
                if (Info(unit).Name is { } service)
                {
                    TemplateDocument().SetService(service, null);
                }

                break;
            case GpoSettingUnitKind.AuditSubcategory:
                AuditDocument().SetSubcategory(Subcategory(unit)!.Value, "-", null);
                break;
            case GpoSettingUnitKind.Preference:
                PreferenceTarget(PreferencePath(unit))?.Remove(unit.ItemId ?? string.Empty);
                break;
        }
    }

    // ---- Copy -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Copies a unit of <paramref name="source"/> into the target (checked with <see cref="CheckCopy"/>; the target's
    /// occurrences that it replaces were removed before). <paramref name="union"/>: user rights and restricted groups get the
    /// union of the target's and the source's principals. Returns the new uid of a cloned preference item.
    /// </summary>
    public string? Copy(GpoSettingUnit unit, GpoFolderContent source, bool union)
    {
        _touched.Add(PathOf(unit));
        string? newId = null;
        switch (unit.Kind)
        {
            case GpoSettingUnitKind.AdministrativeTemplate:
                CopyPolicy(unit, source);
                _copiedPaths.Add(RegistryPath(unit));
                break;
            case GpoSettingUnitKind.RegistryValue or GpoSettingUnitKind.RegistryKey:
            {
                var path = RegistryPath(unit);
                var document = RegistryDocument(path);
                foreach (var setting in unit.Settings)
                {
                    document.Forget(RegistryTargetOf(setting));
                }

                foreach (var entry in SourceRegistryEntries(unit, source))
                {
                    document.Insert(entry);
                }

                _copiedPaths.Add(path);
                break;
            }

            case GpoSettingUnitKind.SecurityValue:
            {
                var info = Info(unit);
                TemplateDocument().Set(info.Section!, info.Name!, SourceTemplate(source)!.Get(info.Section!, info.Name!)!);
                _copiedPaths.Add(SecurityTemplate.RelativePath);
                break;
            }

            case GpoSettingUnitKind.SecurityRegistryValue:
            {
                var (path, value) = SourceRegistryValue(unit, source)!.Value;
                foreach (var existing in TemplateRegistryPaths(TemplateDocument(), unit))
                {
                    TemplateDocument().SetRegistryValue(existing, null);
                }

                TemplateDocument().SetRegistryValue(path, value);
                _copiedPaths.Add(SecurityTemplate.RelativePath);
                break;
            }

            case GpoSettingUnitKind.UserRight:
            {
                var (right, principals) = SourceRight(unit, source)!.Value;
                var document = TemplateDocument();
                var current = document.GetPrivilegeRights().FirstOrDefault(r => r.Key.Equals(right, StringComparison.OrdinalIgnoreCase));
                document.SetPrivilegeRight(right, union && current.Value is { } existing ? Union(existing, principals) : principals);
                _copiedPaths.Add(SecurityTemplate.RelativePath);
                break;
            }

            case GpoSettingUnitKind.RestrictedGroup:
            {
                var relation = Relation(unit)!.Value;
                var entry = SourceGroup(unit, source)!;
                var document = TemplateDocument();
                var existing = SameGroup(document, entry.Group, relation).SelectMany(e => e.Principals).ToList();
                RemoveGroup(entry.Group, relation);
                document.SetRestrictedGroup(entry.Group, relation, union && existing.Count > 0 ? Union(existing, entry.Principals) : entry.Principals);
                _copiedPaths.Add(SecurityTemplate.RelativePath);
                break;
            }

            case GpoSettingUnitKind.Service:
            {
                var service = SourceService(unit, source)!;
                TemplateDocument().SetService(service.Name, service);
                _copiedPaths.Add(SecurityTemplate.RelativePath);
                break;
            }

            case GpoSettingUnitKind.AuditSubcategory:
            {
                var guid = Subcategory(unit)!.Value;
                var audit = SourceAudit(source)!;
                AuditDocument().SetSubcategory(guid, SubcategoryName(guid, source), audit.GetSubcategories()[guid]);
                _copiedPaths.Add(AuditPolicyDocument.RelativePath);
                break;
            }

            case GpoSettingUnitKind.Preference:
            {
                var path = PreferencePath(unit);
                var (item, sourceDocument) = SourceItem(unit, source)!.Value;
                newId = PreferenceDocument(path, sourceDocument).Append(item.Element, _changed);
                _copiedPaths.Add(path);
                break;
            }

            default:
                throw new InvalidOperationException("The unit kind has no writer.");
        }

        return newId;
    }

    // ---- Units ------------------------------------------------------------------------------------------------------------

    /// <summary>The first setting's key parts (units of the security, audit and registry kinds have one key each, or keys of one entry).</summary>
    private static GpoSettingKeyInfo Info(GpoSettingUnit unit) => GpoSettingKey.Parse(unit.Settings.Count > 0 ? unit.Settings[0].Key : unit.BaseId);

    /// <summary>The target file a unit of a kind with a writer lives in.</summary>
    internal static string PathOf(GpoSettingUnit unit) => unit.Kind switch
    {
        GpoSettingUnitKind.AdministrativeTemplate or GpoSettingUnitKind.RegistryValue or GpoSettingUnitKind.RegistryKey => RegistryPath(unit),
        GpoSettingUnitKind.AuditSubcategory => AuditPolicyDocument.RelativePath,
        GpoSettingUnitKind.Preference => PreferencePath(unit),
        _ => SecurityTemplate.RelativePath,
    };

    private static PolicyClass PolicyClassOf(GpoSettingUnit unit) => unit.Scope == GpoScope.User ? PolicyClass.User : PolicyClass.Machine;

    private static string RegistryPath(GpoSettingUnit unit) =>
        unit.Scope == GpoScope.User ? GroupPolicyPlanner.UserRegistryPolicyPath : GroupPolicyPlanner.MachineRegistryPolicyPath;

    private static RestrictedGroupRelation? Relation(GpoSettingUnit unit) => Info(unit).Field switch
    {
        { } f when f.Equals("Members", StringComparison.OrdinalIgnoreCase) => RestrictedGroupRelation.Members,
        { } f when f.Equals("MemberOf", StringComparison.OrdinalIgnoreCase) || f.Equals("Memberof", StringComparison.OrdinalIgnoreCase) => RestrictedGroupRelation.MemberOf,
        _ => null,
    };

    private static Guid? Subcategory(GpoSettingUnit unit)
    {
        var info = Info(unit);
        return info.Subcategory?.Guid ?? (Guid.TryParse(info.Name, out var guid) ? guid : null);
    }

    /// <summary>The unit's preference file: its source path, else <c>&lt;Machine|User&gt;/Preferences/&lt;Type&gt;/&lt;Type&gt;.xml</c>.</summary>
    internal static string PreferencePath(GpoSettingUnit unit)
    {
        if (!string.IsNullOrEmpty(unit.Source))
        {
            return unit.Source;
        }

        var type = unit.PreferenceType ?? Info(unit).PreferenceType ?? string.Empty;
        return (unit.Scope == GpoScope.User ? "User" : "Machine") + "/Preferences/" + type + "/" + type + ".xml";
    }

    private PolicyDefinition? Definition(GpoSettingUnit unit) => _set is not null && unit.PolicyId is { } id ? _set.Find(id) : null;

    // ---- Administrative templates -----------------------------------------------------------------------------------------

    private ConfiguredPolicy? SourcePolicy(GpoSettingUnit unit, GpoFolderContent source)
    {
        var definition = Definition(unit);
        if (definition is null || SourceRegistry(source, RegistryPath(unit)) is not { } document)
        {
            return null;
        }

        return AdministrativeTemplatePolicies.Configured(document, _set!, PolicyClassOf(unit)).Policies
            .FirstOrDefault(p => p.Definition.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
    }

    private void RemovePolicy(GpoSettingUnit unit)
    {
        var definition = Definition(unit)!;
        var half = RegistryDocument(RegistryPath(unit));
        var current = new RegistryPolicyDocument(half.Entries);
        var configured = AdministrativeTemplatePolicies.Configured(current, _set!, PolicyClassOf(unit)).Policies
            .FirstOrDefault(p => p.Definition.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
        if (configured is null)
        {
            return;
        }

        if (configured.Setting.State == PolicyState.Inconsistent)
        {
            half.RemoveExactly(configured.Entries);
            return;
        }

        PolicyEngine.Write(definition.Policy, PolicySetting.NotConfigured, current, _ownership.OwnedNames);
        half.Replace(current.Entries);
    }

    private void CopyPolicy(GpoSettingUnit unit, GpoFolderContent source)
    {
        var definition = Definition(unit)!;
        var policy = SourcePolicy(unit, source)!;
        var half = RegistryDocument(RegistryPath(unit));
        if (policy.Setting.State != PolicyState.Inconsistent)
        {
            var current = new RegistryPolicyDocument(half.Entries);
            PolicyEngine.Write(definition.Policy, policy.Setting, current, _ownership.OwnedNames);
            half.Replace(current.Entries);
            return;
        }

        // An inconsistent policy (another tool, a newer ADMX) cannot be written by state: its entries are carried as they are.
        var target = AdministrativeTemplatePolicies.Configured(new RegistryPolicyDocument(half.Entries), _set!, PolicyClassOf(unit)).Policies
            .FirstOrDefault(p => p.Definition.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
        {
            half.RemoveExactly(target.Entries);
        }

        foreach (var entry in policy.Entries)
        {
            half.Insert(entry);
        }
    }

    // ---- Registry.pol (raw values) ----------------------------------------------------------------------------------------

    /// <summary>What a Registry.pol setting key names: a value of a key, every value of a key, a created key or a deleted key.</summary>
    private static RegistryTarget RegistryTargetOf(GpoSetting setting)
    {
        var info = GpoSettingKey.Parse(setting.Key);
        var full = info.RegistryKey ?? string.Empty;
        var key = StripHive(full);
        if (info.ValueName is null)
        {
            return setting.Value.Equals("DELETE_KEY", StringComparison.OrdinalIgnoreCase)
                ? new RegistryTarget(RegistryTargetKind.DeleteKey, key, null)
                : new RegistryTarget(RegistryTargetKind.CreateKey, key, null);
        }

        return info.ValueName.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase)
            ? new RegistryTarget(RegistryTargetKind.DeleteAll, key, null)
            : new RegistryTarget(RegistryTargetKind.Value, key, info.ValueName);
    }

    private static string StripHive(string path)
    {
        var separator = path.IndexOf('\\', StringComparison.Ordinal);
        return separator < 0 ? string.Empty : RegistryPolicyDocument.NormalizeKey(path[(separator + 1)..]);
    }

    /// <summary>The source's entries of the unit; a value named in a shared <c>**DeleteValues</c>/<c>**DeleteKeys</c> list becomes an entry of its own.</summary>
    private List<RegistryPolEntry> SourceRegistryEntries(GpoSettingUnit unit, GpoFolderContent source)
    {
        var result = new List<RegistryPolEntry>();
        if (SourceRegistry(source, RegistryPath(unit)) is not { } document)
        {
            return result;
        }

        var half = new RegistryHalf(document.Entries);
        foreach (var setting in unit.Settings)
        {
            result.AddRange(half.EntriesOf(RegistryTargetOf(setting)));
        }

        return result;
    }

    // ---- GptTmpl.inf ------------------------------------------------------------------------------------------------------

    private (string Path, string Value)? SourceRegistryValue(GpoSettingUnit unit, GpoFolderContent source)
    {
        if (SourceTemplate(source) is not { } document)
        {
            return null;
        }

        var path = TemplateRegistryPaths(document, unit).LastOrDefault();
        return path is null ? null : (path, document.Get(SecurityTemplateDocument.RegistryValues, path)!);
    }

    /// <summary>The <c>[Registry Values]</c> keys of the document (any spelling) that name the unit's value.</summary>
    private static List<string> TemplateRegistryPaths(SecurityTemplateDocument document, GpoSettingUnit unit)
    {
        var info = Info(unit);
        if (info.RegistryKey is null || info.ValueName is null)
        {
            return [];
        }

        var wanted = info.RegistryKey + "\\" + info.ValueName;
        return [.. document.GetRegistryValues().Select(v => v.Key).Where(k => RegistryFormat.FromSecEditPath(k).Equals(wanted, StringComparison.OrdinalIgnoreCase))];
    }

    private (string Right, IReadOnlyList<string> Principals)? SourceRight(GpoSettingUnit unit, GpoFolderContent source)
    {
        var right = Info(unit).Name;
        if (right is null || SourceTemplate(source) is not { } document)
        {
            return null;
        }

        var found = document.GetPrivilegeRights().FirstOrDefault(r => r.Key.Equals(right, StringComparison.OrdinalIgnoreCase));
        return found.Value is null ? null : (found.Key, found.Value);
    }

    private RestrictedGroupEntry? SourceGroup(GpoSettingUnit unit, GpoFolderContent source)
    {
        var group = Info(unit).Name;
        if (group is null || Relation(unit) is not { } relation || SourceTemplate(source) is not { } document)
        {
            return null;
        }

        return SameGroup(document, group, relation).LastOrDefault();
    }

    /// <summary>The <c>[Group Membership]</c> entries of <paramref name="group"/> under any spelling (same SID, else the same name).</summary>
    private List<RestrictedGroupEntry> SameGroup(SecurityTemplateDocument document, string group, RestrictedGroupRelation relation)
    {
        var id = PrincipalId(group);
        return [.. document.GetRestrictedGroups().Where(e => e.Relation == relation && PrincipalId(e.Group) == id)];
    }

    private void RemoveGroup(string group, RestrictedGroupRelation relation)
    {
        var document = TemplateDocument();
        foreach (var entry in SameGroup(document, group, relation))
        {
            document.SetRestrictedGroup(entry.Group, relation, null);
        }
    }

    private ServiceStartupSetting? SourceService(GpoSettingUnit unit, GpoFolderContent source)
    {
        var name = Info(unit).Name;
        return name is null ? null : SourceTemplate(source)?.GetServices().LastOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The union of two principal lists: the target's first, then the source's it lacks (same SID, else the same name).</summary>
    private List<string> Union(IEnumerable<string> first, IEnumerable<string> second)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var principal in first.Concat(second))
        {
            if (seen.Add(PrincipalId(principal)))
            {
                result.Add(principal);
            }
        }

        return result;
    }

    private string PrincipalId(string principal)
    {
        var text = SecurityTemplate.Principal(principal);
        return (_sidOf(text) ?? text).ToUpperInvariant();
    }

    // ---- audit.csv --------------------------------------------------------------------------------------------------------

    /// <summary>The Subcategory column: the catalog's name, else the source row's, else the GUID.</summary>
    private string SubcategoryName(Guid guid, GpoFolderContent source)
    {
        if (SecuritySettingsCatalog.FindSubcategory(guid) is { } known)
        {
            return known.CsvName;
        }

        if (source.Files.TryGetValue(AuditPolicyDocument.RelativePath, out var bytes))
        {
            var lines = GpoText.Lines(GpoText.DecodeAnsiOrUtf8(bytes, allowAnsi: false)).Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count > 0)
            {
                var header = AuditCsvParser.SplitCsv(lines[0]).Select(f => f.Trim()).ToList();
                var nameColumn = header.FindIndex(h => h.Equals("Subcategory", StringComparison.OrdinalIgnoreCase));
                var guidColumn = header.FindIndex(h => h.Equals("Subcategory GUID", StringComparison.OrdinalIgnoreCase));
                foreach (var line in lines.Skip(1))
                {
                    var fields = AuditCsvParser.SplitCsv(line);
                    if (nameColumn >= 0 && guidColumn >= 0 && guidColumn < fields.Count && nameColumn < fields.Count
                        && Guid.TryParse(fields[guidColumn].Trim(), out var rowGuid) && rowGuid == guid && fields[nameColumn].Trim().Length > 0)
                    {
                        return fields[nameColumn].Trim();
                    }
                }
            }
        }

        return guid.ToString("B", CultureInfo.InvariantCulture);
    }

    // ---- Preferences ------------------------------------------------------------------------------------------------------

    private (GppXmlItem Item, GppPreferenceDocument Document)? SourceItem(GpoSettingUnit unit, GpoFolderContent source)
    {
        var path = PreferencePath(unit);
        if (unit.ItemId is null || Source(source, path, bytes => GppPreferenceDocument.Parse(bytes)) is not GppPreferenceDocument document)
        {
            return null;
        }

        return document.Find(unit.ItemId) is { } item ? (item, document) : null;
    }

    // ---- Documents --------------------------------------------------------------------------------------------------------

    private RegistryHalf RegistryDocument(string path) =>
        (RegistryHalf)Document(path, bytes => new RegistryHalf(bytes is null ? [] : RegistryPolFile.Read(bytes)));

    private SecurityTemplateDocument TemplateDocument() =>
        (SecurityTemplateDocument)Document(SecurityTemplate.RelativePath, bytes => bytes is null ? SecurityTemplateDocument.New() : SecurityTemplateDocument.Parse(bytes));

    private AuditPolicyDocument AuditDocument() =>
        (AuditPolicyDocument)Document(AuditPolicyDocument.RelativePath, bytes => bytes is null ? AuditPolicyDocument.New() : AuditPolicyDocument.Parse(bytes));

    /// <summary>The target's preference file; a missing one gets the source file's root element name and <c>clsid</c>.</summary>
    private GppPreferenceDocument PreferenceDocument(string path, GppPreferenceDocument like) =>
        (GppPreferenceDocument)Document(path, bytes => bytes is null || bytes.Length == 0
            ? GppPreferenceDocument.New(like.RootName, like.RootClassId)
            : GppPreferenceDocument.Parse(bytes));

    /// <summary>The target's preference file, null when it has none.</summary>
    private GppPreferenceDocument? PreferenceTarget(string path)
    {
        if (_documents.TryGetValue(path, out var document))
        {
            return document as GppPreferenceDocument;
        }

        if (!_target.Files.TryGetValue(path, out var bytes) || bytes.Length == 0)
        {
            return null;
        }

        var parsed = GppPreferenceDocument.Parse(bytes);
        _documents[path] = parsed;
        return parsed;
    }

    private object Document(string path, Func<byte[]?, object> create)
    {
        if (!_documents.TryGetValue(path, out var document))
        {
            document = create(_target.Files.TryGetValue(path, out var bytes) ? bytes : null);
            _documents[path] = document;
        }

        return document;
    }

    private RegistryPolicyDocument? SourceRegistry(GpoFolderContent source, string path) =>
        Source(source, path, bytes => RegistryPolicyDocument.Parse(bytes)) as RegistryPolicyDocument;

    private SecurityTemplateDocument? SourceTemplate(GpoFolderContent source) =>
        Source(source, SecurityTemplate.RelativePath, bytes => SecurityTemplateDocument.Parse(bytes)) as SecurityTemplateDocument;

    private AuditPolicyDocument? SourceAudit(GpoFolderContent source) =>
        Source(source, AuditPolicyDocument.RelativePath, bytes => AuditPolicyDocument.Parse(bytes)) as AuditPolicyDocument;

    /// <summary>A parsed source file (cached; null when the source has no such file or it cannot be parsed).</summary>
    private object? Source(GpoFolderContent source, string path, Func<byte[], object> parse)
    {
        if (_sources.TryGetValue((source, path), out var cached))
        {
            return cached;
        }

        object? document = null;
        if (source.Files.TryGetValue(path, out var bytes) && bytes.Length > 0)
        {
            try
            {
                document = parse(bytes);
            }
            catch (GpoParseException)
            {
                document = null;
            }
        }

        _sources[(source, path)] = document;
        return document;
    }

    private enum RegistryTargetKind { Value, DeleteAll, CreateKey, DeleteKey }

    /// <summary>What a Registry.pol setting is about (<see cref="Key"/> without hive; for <see cref="RegistryTargetKind.DeleteKey"/> the deleted key).</summary>
    private sealed record RegistryTarget(RegistryTargetKind Kind, string Key, string? ValueName);

    /// <summary>
    /// The entries of one Registry.pol half with the raw edits of the copy: forget what a setting names, insert a source
    /// entry where the client processes it as in the source (key-level deletes before the key's values, values after them).
    /// </summary>
    private sealed class RegistryHalf(IEnumerable<RegistryPolEntry> entries)
    {
        private List<RegistryPolEntry> _entries = [.. entries];

        public IReadOnlyList<RegistryPolEntry> Entries => _entries;

        public byte[] ToBytes() => RegistryPolFile.Write(_entries);

        public void Replace(IEnumerable<RegistryPolEntry> entries) => _entries = [.. entries];

        /// <summary>Removes these entries (same key, name and data; each once).</summary>
        public void RemoveExactly(IEnumerable<RegistryPolEntry> entries)
        {
            foreach (var entry in entries)
            {
                var index = _entries.FindIndex(e => ReferenceEquals(e, entry));
                if (index < 0)
                {
                    index = _entries.FindIndex(e => SameKey(e.Key, entry.Key) && e.ValueName.Equals(entry.ValueName, StringComparison.OrdinalIgnoreCase) && e.SameData(entry));
                }

                if (index >= 0)
                {
                    _entries.RemoveAt(index);
                }
            }
        }

        /// <summary>The entries that say what <paramref name="target"/> names (a shared list's part as an entry of its own).</summary>
        public IEnumerable<RegistryPolEntry> EntriesOf(RegistryTarget target)
        {
            foreach (var entry in _entries)
            {
                switch (target.Kind)
                {
                    case RegistryTargetKind.Value when SameKey(entry.Key, target.Key):
                        if (IsValueEntry(entry) && RegistryPolicyDocument.BaseName(entry.ValueName).Equals(target.ValueName, StringComparison.OrdinalIgnoreCase))
                        {
                            yield return entry;
                        }
                        else if (entry.ValueName.Equals(RegistryPolicyDocument.DeleteValuesList, StringComparison.OrdinalIgnoreCase)
                                 && List(entry).Contains(target.ValueName!, StringComparer.OrdinalIgnoreCase))
                        {
                            yield return new RegistryPolEntry(entry.Key, RegistryPolicyDocument.DeletePrefix + target.ValueName, RegistryFormat.RegSz, RegistryPolEntry.DeletionData);
                        }

                        break;
                    case RegistryTargetKind.DeleteAll when SameKey(entry.Key, target.Key) && entry.ValueName.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase):
                        yield return entry;
                        break;
                    case RegistryTargetKind.CreateKey when SameKey(entry.Key, target.Key) && IsCreateKey(entry):
                        yield return entry;
                        break;
                    case RegistryTargetKind.DeleteKey when entry.ValueName.Equals(RegistryPolicyDocument.DeleteKeysList, StringComparison.OrdinalIgnoreCase):
                        foreach (var subkey in List(entry))
                        {
                            if (SameKey(Join(entry.Key, subkey), target.Key))
                            {
                                yield return new RegistryPolEntry(entry.Key, RegistryPolicyDocument.DeleteKeysList, RegistryFormat.RegSz, RegistryPolFile.StringData(subkey));
                            }
                        }

                        break;
                }
            }
        }

        /// <summary>Removes every entry about <paramref name="target"/>; a shared list loses only that name (or the entry when nothing is left).</summary>
        public void Forget(RegistryTarget target)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                switch (target.Kind)
                {
                    case RegistryTargetKind.Value when SameKey(entry.Key, target.Key):
                        if (IsValueEntry(entry) && RegistryPolicyDocument.BaseName(entry.ValueName).Equals(target.ValueName, StringComparison.OrdinalIgnoreCase))
                        {
                            _entries.RemoveAt(i);
                        }
                        else if (entry.ValueName.Equals(RegistryPolicyDocument.DeleteValuesList, StringComparison.OrdinalIgnoreCase))
                        {
                            Shrink(i, name => name.Equals(target.ValueName, StringComparison.OrdinalIgnoreCase));
                        }

                        break;
                    case RegistryTargetKind.DeleteAll when SameKey(entry.Key, target.Key) && entry.ValueName.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase):
                        _entries.RemoveAt(i);
                        break;
                    case RegistryTargetKind.CreateKey when SameKey(entry.Key, target.Key) && IsCreateKey(entry):
                        _entries.RemoveAt(i);
                        break;
                    case RegistryTargetKind.DeleteKey when entry.ValueName.Equals(RegistryPolicyDocument.DeleteKeysList, StringComparison.OrdinalIgnoreCase):
                        Shrink(i, subkey => SameKey(Join(entry.Key, subkey), target.Key));
                        break;
                }
            }
        }

        /// <summary>
        /// Inserts a source entry: a key-level delete (<c>**delvals.</c>, <c>**DeleteValues</c>, <c>**DeleteKeys</c>) before the
        /// first entry of the key it clears (the target's, or one copied earlier in this plan) – appended at the end it would
        /// clear them; a created key once; a value, deletion or soft value after the key's entries and after any later
        /// directive that would delete it, in place of an existing form of the same value.
        /// </summary>
        public void Insert(RegistryPolEntry entry)
        {
            var key = RegistryPolicyDocument.NormalizeKey(entry.Key);
            var name = entry.ValueName;
            if (name.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase)
                || name.Equals(RegistryPolicyDocument.DeleteValuesList, StringComparison.OrdinalIgnoreCase))
            {
                if (name.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase))
                {
                    _entries.RemoveAll(e => SameKey(e.Key, key) && e.ValueName.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase));
                }

                var first = _entries.FindIndex(e => SameKey(e.Key, key));
                _entries.Insert(first >= 0 ? first : NewKeyIndex(key), entry);
                return;
            }

            if (name.Equals(RegistryPolicyDocument.DeleteKeysList, StringComparison.OrdinalIgnoreCase))
            {
                var deleted = List(entry).Select(s => Join(key, s)).ToList();
                var first = _entries.FindIndex(e => SameKey(e.Key, key) || deleted.Any(d => SameKey(e.Key, d) || IsBelow(e.Key, d)));
                _entries.Insert(first >= 0 ? first : NewKeyIndex(key), entry);
                return;
            }

            if (IsCreateKey(entry))
            {
                if (!_entries.Any(e => SameKey(e.Key, key) && IsCreateKey(e)))
                {
                    var last = _entries.FindLastIndex(e => SameKey(e.Key, key));
                    _entries.Insert(last >= 0 ? last + 1 : NewKeyIndex(key), entry);
                }

                return;
            }

            var baseName = RegistryPolicyDocument.BaseName(name);
            bool SameValue(RegistryPolEntry e) => SameKey(e.Key, key) && IsValueEntry(e) && RegistryPolicyDocument.BaseName(e.ValueName).Equals(baseName, StringComparison.OrdinalIgnoreCase);
            var existing = _entries.FindIndex(SameValue);
            if (existing >= 0 && !DeletedAfter(existing, key, baseName))
            {
                // In the place of the first form of the same value (the others go).
                _entries[existing] = entry;
                for (var i = _entries.Count - 1; i > existing; i--)
                {
                    if (SameValue(_entries[i]))
                    {
                        _entries.RemoveAt(i);
                    }
                }

                return;
            }

            _entries.RemoveAll(SameValue);
            var after = -1;
            for (var i = 0; i < _entries.Count; i++)
            {
                if (SameKey(_entries[i].Key, key) || DeletesKey(_entries[i], key))
                {
                    after = i;
                }
            }

            _entries.Insert(after >= 0 ? after + 1 : NewKeyIndex(key), entry);
        }

        private bool DeletedAfter(int index, string key, string valueName)
        {
            for (var i = index + 1; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (SameKey(entry.Key, key) && (entry.ValueName.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase)
                    || (entry.ValueName.Equals(RegistryPolicyDocument.DeleteValuesList, StringComparison.OrdinalIgnoreCase) && List(entry).Contains(valueName, StringComparer.OrdinalIgnoreCase))))
                {
                    return true;
                }

                if (DeletesKey(entry, key))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DeletesKey(RegistryPolEntry entry, string key) =>
            entry.ValueName.Equals(RegistryPolicyDocument.DeleteKeysList, StringComparison.OrdinalIgnoreCase)
            && List(entry).Select(s => Join(entry.Key, s)).Any(d => SameKey(key, d) || IsBelow(key, d));

        private void Shrink(int index, Func<string, bool> drop)
        {
            var entry = _entries[index];
            var names = List(entry).ToList();
            var kept = names.Where(n => !drop(n)).ToList();
            if (kept.Count == names.Count)
            {
                return;
            }

            if (kept.Count == 0)
            {
                _entries.RemoveAt(index);
            }
            else
            {
                _entries[index] = entry with { Data = RegistryPolFile.StringData(string.Join(";", kept)) };
            }
        }

        private int NewKeyIndex(string key)
        {
            var index = _entries.FindIndex(e => RegistryPolicyDocument.CompareKeys(e.Key, key) > 0);
            return index >= 0 ? index : _entries.Count;
        }

        /// <summary>A value, its deletion or its soft form (not a directive, not a created key).</summary>
        private static bool IsValueEntry(RegistryPolEntry entry) =>
            !IsCreateKey(entry)
            && !entry.ValueName.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase)
            && !entry.ValueName.Equals(RegistryPolicyDocument.DeleteValuesList, StringComparison.OrdinalIgnoreCase)
            && !entry.ValueName.Equals(RegistryPolicyDocument.DeleteKeysList, StringComparison.OrdinalIgnoreCase);

        private static bool IsCreateKey(RegistryPolEntry entry) => entry.ValueName.Length == 0 && entry.Type == RegistryFormat.RegNone && entry.Data.Length == 0;

        private static IEnumerable<string> List(RegistryPolEntry entry) =>
            RegistryPolFile.ReadString(entry.Data).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string Join(string key, string subkey)
        {
            var normalized = RegistryPolicyDocument.NormalizeKey(key);
            var sub = subkey.Trim('\\');
            return normalized.Length == 0 ? sub : normalized + "\\" + sub;
        }

        private static bool SameKey(string a, string b) =>
            RegistryPolicyDocument.NormalizeKey(a).Equals(RegistryPolicyDocument.NormalizeKey(b), StringComparison.OrdinalIgnoreCase);

        private static bool IsBelow(string key, string ancestor) =>
            RegistryPolicyDocument.NormalizeKey(key).StartsWith(RegistryPolicyDocument.NormalizeKey(ancestor) + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
