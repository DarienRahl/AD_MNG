using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.SecuritySettings;

/// <summary>"Grupy lokalne": Restricted Groups ([Group Membership] of GptTmpl.inf) and GPP local groups (Groups.xml).</summary>
public sealed partial class SecuritySettingsEditorViewModel
{
    private readonly Dictionary<string, RestrictedGroupRowViewModel> _restrictedRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GppGroupItemViewModel> _gppRows = [];
    private IReadOnlyList<GppActionChoice> _gppActions = [];

    // ---- Restricted Groups ---------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<RestrictedGroupRowViewModel> RestrictedGroups { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRestrictedGroupCommand), nameof(AddRestrictedMemberCommand), nameof(AddRestrictedMemberByNameCommand),
        nameof(RemoveRestrictedMemberCommand), nameof(AddRestrictedMemberOfCommand), nameof(AddRestrictedMemberOfByNameCommand), nameof(RemoveRestrictedMemberOfCommand))]
    public partial RestrictedGroupRowViewModel? SelectedRestrictedGroup { get; set; }

    /// <summary>The group typed (or chosen from <see cref="LocalGroupChoices"/>) for "Dodaj grupę".</summary>
    [ObservableProperty]
    public partial string NewRestrictedGroupText { get; set; } = string.Empty;

    /// <summary>The member typed for "Członkowie tej grupy → Dodaj".</summary>
    [ObservableProperty]
    public partial string NewRestrictedMemberText { get; set; } = string.Empty;

    /// <summary>The group typed for "Ta grupa jest członkiem → Dodaj".</summary>
    [ObservableProperty]
    public partial string NewRestrictedMemberOfText { get; set; } = string.Empty;

    // ---- GPP local groups ----------------------------------------------------------------------------------------------

    /// <summary>The local group items of Groups.xml in the order the client applies them.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GppGroupItemViewModel> GppGroups { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveGppGroupCommand), nameof(AddGppMemberCommand), nameof(AddGppMemberByNameCommand), nameof(RemoveGppMemberCommand))]
    public partial GppGroupItemViewModel? SelectedGppGroup { get; set; }

    /// <summary>The group typed (or chosen) for "Nowa grupa lokalna".</summary>
    [ObservableProperty]
    public partial string NewGppGroupText { get; set; } = string.Empty;

    /// <summary>The member typed for the selected item.</summary>
    [ObservableProperty]
    public partial string NewGppMemberText { get; set; } = string.Empty;

    /// <summary>The new member line removes the member ("Usuń z tej grupy") instead of adding it.</summary>
    [ObservableProperty]
    public partial bool NewGppMemberRemoves { get; set; }

    private void BuildGppChoices()
    {
        _gppActions =
        [
            new GppActionChoice(GppItemAction.Create, Localizer[L.SecuritySettings.GppActionCreate]),
            new GppActionChoice(GppItemAction.Replace, Localizer[L.SecuritySettings.GppActionReplace]),
            new GppActionChoice(GppItemAction.Update, Localizer[L.SecuritySettings.GppActionUpdate]),
            new GppActionChoice(GppItemAction.Delete, Localizer[L.SecuritySettings.GppActionDelete]),
        ];
        _gppRows.Clear();
        _restrictedRows.Clear();
        GppGroups = [];
        RestrictedGroups = [];
        SelectedGppGroup = null;
        SelectedRestrictedGroup = null;
    }

    private void RebuildGroups()
    {
        RebuildRestricted();
        RebuildGpp();
    }

    private void RebuildRestricted()
    {
        var current = Restricted(_template?.Document);
        var original = Restricted(_template?.OriginalDocument);
        var rows = new List<RestrictedGroupRowViewModel>();
        foreach (var (key, entry) in current)
        {
            if (!_restrictedRows.TryGetValue(key, out var row))
            {
                row = new RestrictedGroupRowViewModel(this, entry.Group);
                _restrictedRows[key] = row;
            }

            row.Update(() =>
            {
                row.Group = Principal(row.GroupValue);
                row.MembersDefined = entry.Members is not null;
                row.Members = [.. (entry.Members ?? []).Select(Principal)];
                row.MemberOf = [.. (entry.MemberOf ?? []).Select(Principal)];
                row.MembersText = entry.Members is null ? Localizer[L.SecuritySettings.NotDefined]
                    : entry.Members.Count == 0 ? Localizer[L.SecuritySettings.NoMembers]
                    : string.Join(", ", row.Members.Select(m => m.DisplayName));
                row.MemberOfText = entry.MemberOf is null or { Count: 0 } ? Localizer[L.SecuritySettings.NotDefined] : string.Join(", ", row.MemberOf.Select(m => m.DisplayName));
            });
            row.IsChanged = !original.TryGetValue(key, out var before) || !SamePrincipals(before.Members, entry.Members) || !SamePrincipals(before.MemberOf, entry.MemberOf);
            rows.Add(row);
        }

        if (!rows.SequenceEqual(RestrictedGroups))
        {
            var selected = SelectedRestrictedGroup;
            RestrictedGroups = rows;
            SelectedRestrictedGroup = selected is not null && rows.Contains(selected) ? selected : null;
        }
    }

    /// <summary>The Restricted Groups entries by group (upper-cased), in file order, with both relations.</summary>
    private static Dictionary<string, (string Group, IReadOnlyList<string>? Members, IReadOnlyList<string>? MemberOf)> Restricted(SecurityTemplateDocument? document)
    {
        var result = new Dictionary<string, (string Group, IReadOnlyList<string>? Members, IReadOnlyList<string>? MemberOf)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document?.GetRestrictedGroups() ?? [])
        {
            var key = entry.Group.ToUpperInvariant();
            var existing = result.TryGetValue(key, out var found) ? found : (entry.Group, null, null);
            result[key] = entry.Relation == RestrictedGroupRelation.Members ? existing with { Members = entry.Principals } : existing with { MemberOf = entry.Principals };
        }

        return result;
    }

    /// <summary>"Dodaj grupę": a listed local group (its SID), a SID, or the group's name as typed.</summary>
    [RelayCommand(CanExecute = nameof(CanEditTemplateNow))]
    private void AddRestrictedGroup()
    {
        if (ResolveTyped(NewRestrictedGroupText, localGroup: true, out _) is { } group && AddRestricted(group))
        {
            NewRestrictedGroupText = string.Empty;
        }
    }

    /// <summary>"Dodaj grupę z katalogu…": a group of the domain (its SID).</summary>
    [RelayCommand(CanExecute = nameof(CanEditTemplateNow))]
    private async Task PickRestrictedGroupAsync()
    {
        if (await PickAsync(groupsOnly: true).ConfigureAwait(true) is { } picked)
        {
            AddRestricted(picked.Sid);
        }
    }

    /// <summary>A new entry with an empty "Członek grup" (harmless: it adds nothing); the existing entry is selected instead.</summary>
    private bool AddRestricted(string group)
    {
        if (!CanEditTemplate || _template is not { } template)
        {
            return false;
        }

        var key = SecurityTemplate.Principal(group).ToUpperInvariant();
        if (!Restricted(template.Document).ContainsKey(key))
        {
            try
            {
                template.Document.SetRestrictedGroup(group, RestrictedGroupRelation.MemberOf, []);
            }
            catch (ArgumentException)
            {
                ShowRefused();
                return false;
            }

            AfterEdit();
        }

        var row = RestrictedGroups.FirstOrDefault(r => r.GroupValue.Equals(SecurityTemplate.Principal(group), StringComparison.OrdinalIgnoreCase));
        Reveal(row);
        SelectedRestrictedGroup = row;
        return true;
    }

    /// <summary>"Usuń grupę": both lines of the group.</summary>
    [RelayCommand(CanExecute = nameof(CanEditRestricted))]
    private void RemoveRestrictedGroup()
    {
        if (SelectedRestrictedGroup is { } row && CanEditTemplate && _template is { } template)
        {
            template.Document.SetRestrictedGroup(row.GroupValue, RestrictedGroupRelation.Members, null);
            template.Document.SetRestrictedGroup(row.GroupValue, RestrictedGroupRelation.MemberOf, null);
            SelectedRestrictedGroup = null;
            AfterEdit();
        }
    }

    internal void OnMembersDefinedChanged(RestrictedGroupRowViewModel row)
    {
        if (CanEditTemplate && _template is { } template && Restricted(template.Document).TryGetValue(row.GroupValue.ToUpperInvariant(), out var entry))
        {
            template.Document.SetRestrictedGroup(row.GroupValue, RestrictedGroupRelation.Members, row.MembersDefined ? entry.Members ?? [] : null);
            if (!row.MembersDefined && entry.MemberOf is null)
            {
                // The entry stays listed (without members defined) until it is removed.
                template.Document.SetRestrictedGroup(row.GroupValue, RestrictedGroupRelation.MemberOf, []);
            }
        }

        AfterEdit();
    }

    [RelayCommand(CanExecute = nameof(CanEditRestricted))]
    private async Task AddRestrictedMemberAsync()
    {
        if (SelectedRestrictedGroup is { } row && await PickAsync(groupsOnly: false).ConfigureAwait(true) is { } picked)
        {
            ChangeRestricted(row, RestrictedGroupRelation.Members, list => Add(list, picked.Sid));
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditRestricted))]
    private void AddRestrictedMemberByName()
    {
        if (SelectedRestrictedGroup is { } row && ResolveTyped(NewRestrictedMemberText, localGroup: false, out _) is { } value
            && ChangeRestricted(row, RestrictedGroupRelation.Members, list => Add(list, value)))
        {
            NewRestrictedMemberText = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditRestricted))]
    private void RemoveRestrictedMember(SecurityPrincipalViewModel? principal)
    {
        if (SelectedRestrictedGroup is { } row && principal is not null)
        {
            ChangeRestricted(row, RestrictedGroupRelation.Members, list => list.RemoveAll(p => p.Equals(principal.Value, StringComparison.OrdinalIgnoreCase)) > 0);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditRestricted))]
    private async Task AddRestrictedMemberOfAsync()
    {
        if (SelectedRestrictedGroup is { } row && await PickAsync(groupsOnly: true).ConfigureAwait(true) is { } picked)
        {
            ChangeRestricted(row, RestrictedGroupRelation.MemberOf, list => Add(list, picked.Sid));
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditRestricted))]
    private void AddRestrictedMemberOfByName()
    {
        if (SelectedRestrictedGroup is { } row && ResolveTyped(NewRestrictedMemberOfText, localGroup: true, out _) is { } value
            && ChangeRestricted(row, RestrictedGroupRelation.MemberOf, list => Add(list, value)))
        {
            NewRestrictedMemberOfText = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditRestricted))]
    private void RemoveRestrictedMemberOf(SecurityPrincipalViewModel? principal)
    {
        if (SelectedRestrictedGroup is { } row && principal is not null)
        {
            ChangeRestricted(row, RestrictedGroupRelation.MemberOf, list => list.RemoveAll(p => p.Equals(principal.Value, StringComparison.OrdinalIgnoreCase)) > 0);
        }
    }

    private bool CanEditTemplateNow() => CanEditTemplate;

    private bool CanEditRestricted() => CanEditTemplate && SelectedRestrictedGroup is not null;

    /// <summary>Changes one relation of a Restricted Groups entry (adding a member defines "Członkowie").</summary>
    private bool ChangeRestricted(RestrictedGroupRowViewModel row, RestrictedGroupRelation relation, Func<List<string>, bool> change)
    {
        if (!CanEditTemplate || _template is not { } template || !Restricted(template.Document).TryGetValue(row.GroupValue.ToUpperInvariant(), out var entry))
        {
            return false;
        }

        var current = relation == RestrictedGroupRelation.Members ? entry.Members : entry.MemberOf;
        var list = current?.ToList() ?? [];
        if (!change(list))
        {
            return false;
        }

        try
        {
            template.Document.SetRestrictedGroup(row.GroupValue, relation, list);
        }
        catch (ArgumentException)
        {
            ShowRefused();
            return false;
        }

        AfterEdit();
        return true;
    }

    // ---- GPP local groups ----------------------------------------------------------------------------------------------

    private void RebuildGpp()
    {
        var items = _groups?.Document.Items ?? [];
        var original = _groups?.OriginalDocument.Items ?? [];
        while (_gppRows.Count > items.Count)
        {
            _gppRows.RemoveAt(_gppRows.Count - 1);
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (i >= _gppRows.Count)
            {
                _gppRows.Add(new GppGroupItemViewModel(this, i, _gppActions));
            }

            var row = _gppRows[i];
            var item = items[i];
            row.Update(() =>
            {
                row.GroupName = GppGroupName(item);
                row.GroupDetail = string.Join(" · ", new[] { item.GroupName ?? item.Name, item.GroupSid }.OfType<string>());
                row.SelectedAction = _gppActions.FirstOrDefault(a => a.Action == item.Action);
                row.DeleteAllUsers = item.DeleteAllUsers;
                row.DeleteAllGroups = item.DeleteAllGroups;
                row.Members = [.. item.Members.Select(GppMember)];
                row.MembersText = item.Members.Count == 0 ? Localizer[L.SecuritySettings.NoMemberChanges]
                    : string.Join(", ", row.Members.Select(m => (m.Action == GppMemberAction.Remove ? "− " : "+ ") + m.Principal.DisplayName));
                row.HasTargeting = item.HasItemLevelTargeting;
                row.IsDisabled = item.Disabled;
            });
            row.IsChanged = OriginalOf(original, item, i) is not { } before || !SameItem(before, item);
        }

        if (!_gppRows.SequenceEqual(GppGroups))
        {
            var selected = SelectedGppGroup;
            GppGroups = [.. _gppRows];
            SelectedGppGroup = selected is not null && _gppRows.Contains(selected) ? selected : null;
        }
    }

    /// <summary>The item as read with the same uid (an item without uid: the one read at its position, also without uid).</summary>
    private static GppLocalGroup? OriginalOf(IReadOnlyList<GppLocalGroup> original, GppLocalGroup item, int index) =>
        item.Uid is { } uid ? original.FirstOrDefault(o => string.Equals(o.Uid, uid, StringComparison.OrdinalIgnoreCase))
        : index < original.Count && original[index].Uid is null ? original[index]
        : null;

    /// <summary>The key of an item for the change counter: its uid, else its position.</summary>
    private static string GppKey(GppLocalGroup item, int index) => item.Uid?.ToUpperInvariant() ?? "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A built-in group by its localized name, else the name as written.</summary>
    private string GppGroupName(GppLocalGroup item) =>
        item.GroupSid is { } sid && _choicesBySid.TryGetValue(sid, out var choice) ? choice.DisplayName
        : item.GroupName ?? item.Name ?? item.GroupSid ?? string.Empty;

    private GppMemberViewModel GppMember(GppGroupMember member)
    {
        var principal = member.Sid is { } sid
            ? new SecurityPrincipalViewModel(sid, NameOfSid(sid) ?? member.Name ?? sid, IsSid: true, sid)
            : Principal(member.Name ?? string.Empty);
        return new GppMemberViewModel(principal, member.Action,
            Localizer[member.Action == GppMemberAction.Remove ? L.SecuritySettings.GppMemberRemove : L.SecuritySettings.GppMemberAdd], member);
    }

    internal void OnGppItemEdited(GppGroupItemViewModel row) =>
        ChangeGpp(row, edit => edit with
        {
            Action = row.SelectedAction?.Action ?? edit.Action,
            DeleteAllUsers = row.DeleteAllUsers,
            DeleteAllGroups = row.DeleteAllGroups,
        });

    /// <summary>"Nowa grupa lokalna": a listed built-in group (GPMC's name and its SID) or a group named as typed; action "Aktualizuj".</summary>
    [RelayCommand(CanExecute = nameof(CanEditGroupsNow))]
    private void AddGppGroup()
    {
        if (!CanEditGroups || _groups is not { } groups || ResolveTyped(NewGppGroupText, localGroup: true, out var choice) is not { } value)
        {
            return;
        }

        var (name, sid) = choice is { GppName: { } gppName } ? (gppName, (string?)choice.Sid)
            : SecurityTemplate.IsSid(value) ? (value, value)
            : (value, null);
        int index;
        try
        {
            index = groups.Document.Add(new GppLocalGroupEdit(name, sid, GppItemAction.Update, false, false, []), Now());
        }
        catch (ArgumentException)
        {
            ShowRefused();
            return;
        }

        NewGppGroupText = string.Empty;
        AfterEdit();
        var row = index < GppGroups.Count ? GppGroups[index] : null;
        Reveal(row);
        SelectedGppGroup = row;
    }

    /// <summary>"Usuń" of the selected item.</summary>
    [RelayCommand(CanExecute = nameof(CanEditGpp))]
    private void RemoveGppGroup()
    {
        if (SelectedGppGroup is { } row && CanEditGroups && _groups is { } groups && row.Index < groups.Document.Items.Count)
        {
            groups.Document.RemoveAt(row.Index);
            SelectedGppGroup = null;
            AfterEdit();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditGpp))]
    private async Task AddGppMemberAsync()
    {
        if (SelectedGppGroup is { } row && await PickAsync(groupsOnly: false).ConfigureAwait(true) is { } picked)
        {
            AddGppMember(row, new GppGroupMember(picked.Name, picked.Sid, NewGppMemberRemoves ? GppMemberAction.Remove : GppMemberAction.Add));
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditGpp))]
    private void AddGppMemberByName()
    {
        if (SelectedGppGroup is not { } row || ResolveTyped(NewGppMemberText, localGroup: false, out var choice) is not { } value)
        {
            return;
        }

        var action = NewGppMemberRemoves ? GppMemberAction.Remove : GppMemberAction.Add;
        var member = choice is not null ? new GppGroupMember(choice.DisplayName, choice.Sid, action)
            : SecurityTemplate.IsSid(value) ? new GppGroupMember(null, value, action)
            : new GppGroupMember(value, null, action);
        if (AddGppMember(row, member))
        {
            NewGppMemberText = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditGpp))]
    private void RemoveGppMember(GppMemberViewModel? member)
    {
        if (SelectedGppGroup is { } row && member is not null)
        {
            ChangeGpp(row, edit => edit with { Members = [.. edit.Members.Where(m => m != member.Member)] });
        }
    }

    private bool CanEditGroupsNow() => CanEditGroups;

    private bool CanEditGpp() => CanEditGroups && SelectedGppGroup is not null;

    private bool AddGppMember(GppGroupItemViewModel row, GppGroupMember member)
    {
        var duplicate = row.Members.Any(m => m.Action == member.Action
            && (member.Sid is not null ? string.Equals(m.Member.Sid, member.Sid, StringComparison.OrdinalIgnoreCase) : string.Equals(m.Member.Name, member.Name, StringComparison.OrdinalIgnoreCase)));
        if (duplicate)
        {
            Gp.Ui.Notifications.Show(NotificationSeverity.Info, Localizer[L.SecuritySettings.AlreadyListed]);
            return false;
        }

        return ChangeGpp(row, edit => edit with { Members = [.. edit.Members, member] });
    }

    /// <summary>Writes a changed item (its targeting, options and other attributes stay; it gets a new "changed" time).</summary>
    private bool ChangeGpp(GppGroupItemViewModel row, Func<GppLocalGroupEdit, GppLocalGroupEdit> change)
    {
        if (!CanEditGroups || _groups is not { } groups || row.Index >= groups.Document.Items.Count)
        {
            AfterEdit(); // the row shows the file again
            return false;
        }

        var item = groups.Document.Items[row.Index];
        var edit = new GppLocalGroupEdit(item.GroupName ?? item.Name ?? item.GroupSid ?? string.Empty, item.GroupSid, item.Action,
            item.DeleteAllUsers, item.DeleteAllGroups, item.Members);
        try
        {
            groups.Document.Update(row.Index, change(edit), Now());
        }
        catch (ArgumentException)
        {
            ShowRefused();
            AfterEdit();
            return false;
        }

        AfterEdit();
        return true;
    }

    /// <summary>The "changed" stamp of GPP items: the editor's local time, as GPMC writes it.</summary>
    private DateTime Now() => Gp.Time.GetLocalNow().DateTime;
}
