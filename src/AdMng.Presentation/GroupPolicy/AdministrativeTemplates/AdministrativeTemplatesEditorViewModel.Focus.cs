using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Presentation.Changes;
using AdMng.Presentation.GroupPolicy.Modeling;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

/// <summary>"Edytuj to ustawienie" and "Zmiany ustawień" of the administrative templates editor (K6, design note 20.3).</summary>
public sealed partial class AdministrativeTemplatesEditorViewModel
{
    private const string ExtraPrefix = "extra:";

    /// <summary>
    /// Selects a policy: an ADMX policy by its id, or the policy that owns a Registry.pol value of a setting key
    /// (<c>Computer/Registry/HKLM\…!Name</c>); a value no loaded template describes selects "Ustawienia rejestru spoza
    /// szablonów" and its entry. The search and the filters are cleared so the policy is listed. Before the first load the
    /// focus waits for it.
    /// </summary>
    public bool FocusSetting(GroupPolicySettingFocus focus)
    {
        ArgumentNullException.ThrowIfNull(focus);
        if (_gpo is null || Roots.Count == 0)
        {
            _pendingFocus = focus;
            return true;
        }

        PolicyDefinition? definition;
        PolicyClass scope;
        if (focus.PolicyId is { Length: > 0 } id)
        {
            definition = _set.Find(id);
            scope = focus.PolicyClass ?? (definition is not null && !definition.Policy.AppliesTo(PolicyClass.Machine) ? PolicyClass.User : PolicyClass.Machine);
        }
        else if (focus.TryGetRegistryValue(out scope, out var key, out var valueName))
        {
            definition = Owner(scope, key, valueName);
            if (definition is null)
            {
                ShowExtra(scope, key, valueName);
                return true;
            }
        }
        else
        {
            return NotFound(focus);
        }

        if (definition is null || !definition.Policy.AppliesTo(scope))
        {
            return NotFound(focus);
        }

        ClearFilters();
        var root = Roots.First(r => r.Scope == scope);
        var path = definition.Category is { } category
            ? PathTo(root, n => n.Kind == PolicyTreeNodeKind.Category && string.Equals(n.Category?.Id, category.Id, StringComparison.OrdinalIgnoreCase))
            : PathTo(root, n => n.Kind == PolicyTreeNodeKind.Uncategorized);
        path ??= PathTo(root, n => n.Kind == PolicyTreeNodeKind.AllSettings) ?? [root];
        Select(path);
        SelectedPolicy = Policies.FirstOrDefault(r => r.Scope == scope && string.Equals(r.Definition.Id, definition.Id, StringComparison.Ordinal));
        return true;
    }

    /// <summary>
    /// "Zmiany ustawień" of the save: every touched policy whose setting differs from the file as read (its state and
    /// options before and after, as the K5 model words them) and every changed entry no template describes.
    /// </summary>
    /// <param name="machine">The computer half is saved.</param>
    /// <param name="user">The user half is saved.</param>
    internal IReadOnlyList<GroupPolicySettingChange> SettingChanges(bool machine, bool user)
    {
        var text = new GroupPolicyModelText(Localizer, string.Empty);
        var none = Localizer[L.GroupPolicyEditing.NoValue];
        var changes = new List<GroupPolicySettingChange>();
        foreach (var (scope, id) in _touched)
        {
            if (!(scope == PolicyClass.User ? user : machine) || Half(scope) is not { Error: null } half)
            {
                continue;
            }

            var scopeName = Localizer[scope == PolicyClass.User ? L.AdministrativeTemplates.UserConfiguration : L.AdministrativeTemplates.ComputerConfiguration];
            if (id.StartsWith(ExtraPrefix, StringComparison.Ordinal))
            {
                var parts = id[ExtraPrefix.Length..].Split('\n', 2);
                var before = Entry(half.OriginalDocument, parts[0], parts[1]);
                var after = Entry(half.Document, parts[0], parts[1]);
                if (before is null ? after is null : after is not null && before.Type == after.Type && before.Data.AsSpan().SequenceEqual(after.Data))
                {
                    continue;
                }

                var shown = new ExtraRegistryEntryViewModel(before ?? after!, scope);
                changes.Add(new GroupPolicySettingChange(Localizer.Format(L.GroupPolicyEditing.ExtraValue, shown.KeyText, shown.ValueName),
                    before is null ? none : new ExtraRegistryEntryViewModel(before, scope).DataText,
                    after is null ? none : new ExtraRegistryEntryViewModel(after, scope).DataText,
                    scopeName));
                continue;
            }

            if (_set.Find(id) is not { } definition)
            {
                continue;
            }

            var settingBefore = PolicyEngine.Read(definition.Policy, half.OriginalDocument.View, _ownership.OwnedNames);
            var settingAfter = PolicyEngine.Read(definition.Policy, half.Document.View, _ownership.OwnedNames);
            if (Equals(settingBefore, settingAfter))
            {
                continue;
            }

            var group = definition.Category is { } category ? scopeName + " / " + string.Join(" / ", category.PathNames()) : scopeName;
            changes.Add(new GroupPolicySettingChange(definition.DisplayName, text.PolicyValue(definition, settingBefore), text.PolicyValue(definition, settingAfter), group));
        }

        var compare = StringComparer.Create(Localizer.Culture, ignoreCase: true);
        return [.. changes.OrderBy(c => c.Group, compare).ThenBy(c => c.Setting, compare)];
    }

    /// <summary>The loaded policy that owns a value of a key (its own value names or a list key; null: none).</summary>
    private PolicyDefinition? Owner(PolicyClass scope, string key, string? valueName)
    {
        var normalized = RegistryPolicyDocument.NormalizeKey(key);
        foreach (var definition in _set.Policies.Where(d => d.Policy.AppliesTo(scope)))
        {
            if (PolicyEngine.ListKeys(definition.Policy).Any(k => SameKey(k, normalized)))
            {
                return definition;
            }

            foreach (var (owned, names) in PolicyEngine.OwnValueNames(definition.Policy))
            {
                if (SameKey(owned, normalized) && (valueName is null || names.Contains(valueName)))
                {
                    return definition;
                }
            }
        }

        return null;
    }

    private static bool SameKey(string key, string normalized) =>
        string.Equals(RegistryPolicyDocument.NormalizeKey(key), normalized, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Ustawienia rejestru spoza szablonów" of the half, with the entry of the key and value selected when listed.</summary>
    private void ShowExtra(PolicyClass scope, string key, string? valueName)
    {
        var root = Roots.First(r => r.Scope == scope);
        Select(PathTo(root, n => n.Kind == PolicyTreeNodeKind.Extra) ?? [root]);
        var normalized = RegistryPolicyDocument.NormalizeKey(key);
        SelectedExtraEntry = ExtraEntries.FirstOrDefault(e => SameKey(e.Entry.Key, normalized)
            && (valueName is null || string.Equals(Unprefixed(e.ValueName), valueName, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Unprefixed(string valueName) =>
        valueName.StartsWith(RegistryPolicyDocument.DeletePrefix, StringComparison.OrdinalIgnoreCase) ? valueName[RegistryPolicyDocument.DeletePrefix.Length..]
        : valueName.StartsWith(RegistryPolicyDocument.SoftPrefix, StringComparison.OrdinalIgnoreCase) ? valueName[RegistryPolicyDocument.SoftPrefix.Length..]
        : valueName;

    /// <summary>The search and the filters off (a focused policy is listed whatever was searched).</summary>
    private void ClearFilters()
    {
        SearchText = string.Empty;
        OnlyConfigured = false;
        OnlyChanged = false;
    }

    /// <summary>Expands the nodes of a path and selects its last one (the tree follows <see cref="PolicyTreeNodeViewModel.IsSelected"/>).</summary>
    private void Select(IReadOnlyList<PolicyTreeNodeViewModel> path)
    {
        foreach (var ancestor in path.Take(path.Count - 1))
        {
            ancestor.IsExpanded = true;
        }

        var node = path[^1];
        node.IsSelected = true;
        SelectedNode = node;
    }

    /// <summary>The nodes from <paramref name="node"/> down to the first one that matches (null: none).</summary>
    private static List<PolicyTreeNodeViewModel>? PathTo(PolicyTreeNodeViewModel node, Func<PolicyTreeNodeViewModel, bool> match)
    {
        if (match(node))
        {
            return [node];
        }

        foreach (var child in node.Children)
        {
            if (PathTo(child, match) is { } path)
            {
                path.Insert(0, node);
                return path;
            }
        }

        return null;
    }

    private static RegistryPolEntry? Entry(RegistryPolicyDocument document, string key, string valueName) =>
        document.Entries.FirstOrDefault(e => SameKey(e.Key, RegistryPolicyDocument.NormalizeKey(key))
            && string.Equals(e.ValueName, valueName, StringComparison.OrdinalIgnoreCase));

    private bool NotFound(GroupPolicySettingFocus focus)
    {
        Gp.Ui.Notifications.Show(NotificationSeverity.Info, Localizer.Format(L.GroupPolicyEditing.SettingNotFound,
            focus.PolicyId ?? focus.SettingKey ?? string.Empty));
        return false;
    }
}
