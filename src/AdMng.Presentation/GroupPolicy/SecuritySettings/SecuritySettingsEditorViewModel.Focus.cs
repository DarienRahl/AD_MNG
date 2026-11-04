using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.GroupPolicy.SecuritySettings;

/// <summary>"Edytuj to ustawienie" and "Zmiany ustawień" of the security settings editor (K6, design note 20.3).</summary>
public sealed partial class SecuritySettingsEditorViewModel
{
    private const string Masked = "••••••";

    /// <summary>"Edytuj to ustawienie" asked before the files were read: applied when the load ends.</summary>
    private GroupPolicySettingFocus? _pendingFocus;

    /// <summary>
    /// Selects the page and the row of a setting key: <c>Computer/Security/Privilege Rights/…</c> (a right),
    /// <c>…/System Access/…</c>, <c>…/Kerberos Policy/…</c>, <c>…/Registry Values/HKLM\…!Name</c> (an option, or the template
    /// entry the catalog does not know), <c>…/Event Audit/…</c> and <c>Computer/Audit/{guid}</c> (an audit row, highlighted),
    /// <c>…/Group Membership/&lt;group&gt;/…</c> (Restricted Groups), <c>…/Services/&lt;name&gt;/…</c> and
    /// <c>Computer/Preferences/Groups/&lt;uid&gt;/…</c> (a GPP local group). The search and the filters are cleared so the row
    /// is listed. Before the first load the focus waits for it.
    /// </summary>
    public bool FocusSetting(GroupPolicySettingFocus focus)
    {
        ArgumentNullException.ThrowIfNull(focus);
        if (_gpo is null)
        {
            _pendingFocus = focus;
            return true;
        }

        if (focus.SecurityPart() is not { } part)
        {
            return NotFound(focus);
        }

        SearchText = string.Empty;
        OnlyDefined = false;
        OnlyChanged = false;
        foreach (var audit in LegacyAudit.Concat(_advancedRows))
        {
            audit.IsFocused = false;
        }

        var found = GroupPolicySettingFocus.IsAuditArea(part.Area) ? FocusAdvancedAudit(part.Path)
            : GroupPolicySettingFocus.IsSecurityArea(part.Area) ? FocusTemplate(part.Path)
            : FocusGpp(part.Path);
        return found || NotFound(focus);
    }

    private bool FocusTemplate(string path)
    {
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return false;
        }

        var section = path[..slash];
        var rest = path[(slash + 1)..];
        switch (section.ToUpperInvariant())
        {
            case "PRIVILEGE RIGHTS":
                SelectedPage = SecuritySettingsPage.Rights;
                SelectedRight = UserRights.FirstOrDefault(r => r.Constant.Equals(rest, StringComparison.OrdinalIgnoreCase));
                return SelectedRight is not null;
            case "SYSTEM ACCESS":
                return FocusOption(SecurityTemplateDocument.SystemAccess, rest);
            case "KERBEROS POLICY":
                return FocusOption(SecurityTemplateDocument.KerberosPolicy, rest);
            case "REGISTRY VALUES":
                return FocusOption(SecurityTemplateDocument.RegistryValues, SecEditPath(rest));
            case "EVENT AUDIT":
                SelectedPage = SecuritySettingsPage.Audit;
                return Highlight(LegacyAudit.FirstOrDefault(r => r.Id.Equals(rest, StringComparison.OrdinalIgnoreCase)));
            case "GROUP MEMBERSHIP":
                var group = rest.LastIndexOf('/') is var end and > 0 ? rest[..end] : rest;
                SelectedPage = SecuritySettingsPage.Groups;
                SelectedRestrictedGroup = RestrictedGroups.FirstOrDefault(r => r.GroupValue.Equals(SecurityTemplate.Principal(group), StringComparison.OrdinalIgnoreCase));
                return SelectedRestrictedGroup is not null;
            case "SERVICES":
                var name = rest.LastIndexOf('/') is var last and > 0 ? rest[..last] : rest;
                SelectedPage = SecuritySettingsPage.Services;
                SelectedService = Services.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                return SelectedService is not null;
            default:
                return false;
        }
    }

    /// <summary>An option of the catalog (its group and row), else the template entry the catalog does not know.</summary>
    private bool FocusOption(string section, string key)
    {
        SelectedPage = SecuritySettingsPage.Options;
        if (SecuritySettingsCatalog.FindOption(section, key) is { } definition && _optionRows.FirstOrDefault(r => r.Definition == definition) is { } row)
        {
            SelectedOptionGroup = OptionGroups.FirstOrDefault(g => g.Rows.Contains(row));
            SelectedOption = row;
            return true;
        }

        SelectedOptionGroup = OptionGroups.FirstOrDefault(g => g.IsRaw);
        SelectedRawEntry = RawEntries.FirstOrDefault(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return SelectedRawEntry is not null;
    }

    /// <summary><c>Computer/Audit/{guid}</c>: the subcategory's row; other audit.csv keys (options, per-user) open the page.</summary>
    private bool FocusAdvancedAudit(string path)
    {
        SelectedPage = SecuritySettingsPage.Audit;
        return !Guid.TryParse(path, out var guid) || Highlight(_advancedRows.FirstOrDefault(r => r.Subcategory!.Guid == guid));
    }

    /// <summary><c>Computer/Preferences/Groups/&lt;uid&gt;/…</c>: the GPP item with that uid.</summary>
    private bool FocusGpp(string path)
    {
        SelectedPage = SecuritySettingsPage.Groups;
        var id = path.IndexOf('/', StringComparison.Ordinal) is var slash and > 0 ? path[..slash] : path;
        var items = _groups?.Document.Items ?? [];
        for (var i = 0; i < items.Count && i < GppGroups.Count; i++)
        {
            if (items[i].Uid is { } uid && uid.Trim().Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                SelectedGppGroup = GppGroups[i];
                return true;
            }
        }

        return false;
    }

    private static bool Highlight(AuditRowViewModel? row)
    {
        if (row is null)
        {
            return false;
        }

        row.IsFocused = true;
        return true;
    }

    /// <summary><c>HKLM\System\…!Name</c> of a setting key as GptTmpl.inf writes it (<c>MACHINE\System\…\Name</c>).</summary>
    private static string SecEditPath(string registryValue)
    {
        var bang = registryValue.LastIndexOf('!');
        var path = bang < 0 ? registryValue : registryValue[..bang] + "\\" + registryValue[(bang + 1)..];
        return path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) ? "MACHINE\\" + path[5..] : path;
    }

    private bool NotFound(GroupPolicySettingFocus focus)
    {
        Gp.Ui.Notifications.Show(NotificationSeverity.Info, Localizer.Format(L.GroupPolicyEditing.SettingNotFound, focus.SettingKey ?? focus.PolicyId ?? string.Empty));
        return false;
    }

    // ---- "Zmiany ustawień" ------------------------------------------------------------------------------------------

    /// <summary>
    /// "Zmiany ustawień" of the save: per file that is saved, every setting whose value differs from the file as read – rights,
    /// options and template entries, legacy and advanced audit, Restricted Groups, services and GPP local groups – named as the
    /// pages name them, with secret registry values masked.
    /// </summary>
    internal IReadOnlyList<GroupPolicySettingChange> SettingChanges(SecuritySettingsSave save)
    {
        ArgumentNullException.ThrowIfNull(save);
        var changes = new List<GroupPolicySettingChange>();
        if (save.Template is not null && _template is { Error: null } template)
        {
            TemplateChanges(template.OriginalDocument, template.Document, changes);
        }

        if (save.Audit is not null && _audit is { Error: null } audit)
        {
            var before = audit.OriginalDocument.GetSubcategories();
            var after = audit.Document.GetSubcategories();
            foreach (var guid in before.Keys.Union(after.Keys))
            {
                int? old = before.TryGetValue(guid, out var b) ? b : null;
                int? now = after.TryGetValue(guid, out var a) ? a : null;
                if (old != now)
                {
                    var definition = SecuritySettingsCatalog.FindSubcategory(guid);
                    changes.Add(new GroupPolicySettingChange(
                        definition is null ? guid.ToString("B", System.Globalization.CultureInfo.InvariantCulture) : Localizer[SecuritySettingsText.AuditSubcategoryKey(definition.Id)],
                        AuditText(old), AuditText(now),
                        definition is null ? Localizer[L.SecuritySettings.AdvancedAuditHeader] : Localizer[SecuritySettingsText.AuditCategoryKey(definition.Category)]));
                }
            }
        }

        if (save.Groups is not null && _groups is { Error: null } groups)
        {
            var before = groups.OriginalDocument.Items.Select((item, i) => (Key: GppKey(item, i), Item: item)).GroupBy(b => b.Key).ToDictionary(g => g.Key, g => g.First().Item);
            var after = groups.Document.Items.Select((item, i) => (Key: GppKey(item, i), Item: item)).GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First().Item);
            var none = Localizer[L.GroupPolicyEditing.NoValue];
            foreach (var key in before.Keys.Concat(after.Keys).Distinct())
            {
                var old = before.GetValueOrDefault(key);
                var now = after.GetValueOrDefault(key);
                if (old is not null && now is not null && SameItem(old, now))
                {
                    continue;
                }

                changes.Add(new GroupPolicySettingChange(GppGroupName(now ?? old!), old is null ? none : GppText(old), now is null ? none : GppText(now),
                    Localizer[L.SecuritySettings.GppGroupsHeader]));
            }
        }

        return changes;
    }

    private void TemplateChanges(SecurityTemplateDocument original, SecurityTemplateDocument current, List<GroupPolicySettingChange> changes)
    {
        var notDefined = Localizer[L.SecuritySettings.NotDefined];

        // Prawa użytkownika
        var rightsBefore = original.GetPrivilegeRights();
        var rightsAfter = current.GetPrivilegeRights();
        foreach (var constant in Ordered(rightsBefore.Keys.Concat(rightsAfter.Keys), SecuritySettingsCatalog.UserRights))
        {
            var old = rightsBefore.TryGetValue(constant, out var b) ? b : null;
            var now = rightsAfter.TryGetValue(constant, out var a) ? a : null;
            if (!SamePrincipals(old, now))
            {
                var name = _rightRows.TryGetValue(constant, out var row) ? row.Name : constant;
                changes.Add(new GroupPolicySettingChange(name, PrincipalsText(old, L.SecuritySettings.Nobody), PrincipalsText(now, L.SecuritySettings.Nobody),
                    Localizer[L.SecuritySettings.TabRights]));
            }
        }

        // Zasady kont i opcje zabezpieczeń, then the entries the catalog does not know
        foreach (var section in new[] { SecurityTemplateDocument.SystemAccess, SecurityTemplateDocument.KerberosPolicy, SecurityTemplateDocument.RegistryValues })
        {
            var before = original.Values(section).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
            var after = current.Values(section).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var key in before.Keys.Concat(after.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var old = before.GetValueOrDefault(key);
                var now = after.GetValueOrDefault(key);
                if (SameValue(old, now))
                {
                    continue;
                }

                if (SecuritySettingsCatalog.FindOption(section, key) is { } definition && _optionRows.FirstOrDefault(r => r.Definition == definition) is { } row)
                {
                    changes.Add(new GroupPolicySettingChange(row.Name, old is null ? notDefined : ValueText(definition, old, row),
                        now is null ? notDefined : ValueText(definition, now, row), row.GroupName));
                }
                else
                {
                    var secret = section == SecurityTemplateDocument.RegistryValues && IsSecretRegistryValue(key);
                    changes.Add(new GroupPolicySettingChange($"[{section}] {key}", old is null ? notDefined : secret ? Masked : old,
                        now is null ? notDefined : secret ? Masked : now, Localizer[L.SecuritySettings.RawGroup]));
                }
            }
        }

        // Inspekcja (legacy categories)
        var auditBefore = original.Values(SecurityTemplateDocument.EventAudit).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        var auditAfter = current.Values(SecurityTemplateDocument.EventAudit).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var key in Ordered(auditBefore.Keys.Concat(auditAfter.Keys), SecuritySettingsCatalog.AuditCategories))
        {
            var old = Level(auditBefore.GetValueOrDefault(key));
            var now = Level(auditAfter.GetValueOrDefault(key));
            if (old != now)
            {
                var name = SecuritySettingsCatalog.AuditCategories.Contains(key, StringComparer.OrdinalIgnoreCase) ? Localizer[SecuritySettingsText.EventAuditKey(key)] : key;
                changes.Add(new GroupPolicySettingChange(name, AuditText(old), AuditText(now), Localizer[L.SecuritySettings.LegacyAuditHeader]));
            }
        }

        // Grupy z ograniczeniami (each relation that changed)
        var groupsBefore = Restricted(original);
        var groupsAfter = Restricted(current);
        foreach (var key in groupsBefore.Keys.Concat(groupsAfter.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var old = groupsBefore.TryGetValue(key, out var b) ? b : default;
            var now = groupsAfter.TryGetValue(key, out var a) ? a : default;
            var group = Principal(now.Group ?? old.Group).DisplayName;
            if (!SamePrincipals(old.Members, now.Members))
            {
                changes.Add(new GroupPolicySettingChange(Localizer.Format(L.GroupPolicyEditing.MembersOf, group),
                    PrincipalsText(old.Members, L.SecuritySettings.NoMembers), PrincipalsText(now.Members, L.SecuritySettings.NoMembers),
                    Localizer[L.SecuritySettings.RestrictedGroupsHeader]));
            }

            if (!SamePrincipals(old.MemberOf, now.MemberOf))
            {
                changes.Add(new GroupPolicySettingChange(Localizer.Format(L.GroupPolicyEditing.MemberOfOf, group),
                    PrincipalsText(old.MemberOf, L.SecuritySettings.NoMembers), PrincipalsText(now.MemberOf, L.SecuritySettings.NoMembers),
                    Localizer[L.SecuritySettings.RestrictedGroupsHeader]));
            }
        }

        // Usługi
        var servicesBefore = original.GetServices().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var servicesAfter = current.GetServices().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in servicesBefore.Keys.Concat(servicesAfter.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var old = servicesBefore.GetValueOrDefault(name);
            var now = servicesAfter.GetValueOrDefault(name);
            if (old != now)
            {
                changes.Add(new GroupPolicySettingChange((now ?? old)!.Name, old is null ? notDefined : ModeText(old.StartMode), now is null ? notDefined : ModeText(now.StartMode),
                    Localizer[L.SecuritySettings.TabServices]));
            }
        }
    }

    /// <summary>The catalog's entries in its order, then the others as written.</summary>
    private static IEnumerable<string> Ordered(IEnumerable<string> keys, IReadOnlyList<string> catalog)
    {
        var order = catalog.Select((key, index) => (key, index)).ToDictionary(c => c.key, c => c.index, StringComparer.OrdinalIgnoreCase);
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => order.TryGetValue(k, out var index) ? index : int.MaxValue);
    }

    /// <summary>"Nie zdefiniowano", "Nikt" / "Brak członków" or the principals' names.</summary>
    private string PrincipalsText(IReadOnlyList<string>? principals, string emptyKey) =>
        principals is null ? Localizer[L.SecuritySettings.NotDefined]
        : principals.Count == 0 ? Localizer[emptyKey]
        : string.Join(", ", principals.Select(p => Principal(p).DisplayName));

    private string AuditText(int? level) =>
        LegacyAudit.FirstOrDefault()?.States.FirstOrDefault(s => s.Value == level)?.Label ?? Localizer[L.SecuritySettings.AuditNotConfigured];

    private string ModeText(int startMode) =>
        ServiceModes.FirstOrDefault(m => m.Value == startMode)?.Label
        ?? Localizer.Format(L.SecuritySettings.ServiceModeOther, startMode.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>"Aktualizuj; + KONTOSO\GG_IT, − Łukasz".</summary>
    private string GppText(GppLocalGroup item) => Localizer.Format(L.GroupPolicyEditing.GppItemSummary,
        _gppActions.FirstOrDefault(a => a.Action == item.Action)?.Label ?? item.Action.ToString(),
        item.Members.Count == 0 ? Localizer[L.SecuritySettings.NoMemberChanges]
            : string.Join(", ", item.Members.Select(m => (m.Action == GppMemberAction.Remove ? "− " : "+ ") + GppMember(m).Principal.DisplayName)));

    /// <summary>A [Registry Values] key (<c>MACHINE\…\Name</c>) whose value the shared parsers mask.</summary>
    private static bool IsSecretRegistryValue(string key)
    {
        var path = RegistryFormat.FromSecEditPath(key);
        var separator = path.LastIndexOf('\\');
        var keyPath = separator < 0 ? path : path[..separator];
        var valueName = separator < 0 ? string.Empty : path[(separator + 1)..];
        return GpoSecretSettings.IsSecretRegistryValue(keyPath, valueName) || GpoSecretSettings.HidesValueNames(keyPath);
    }
}
