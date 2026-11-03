using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Security;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.GroupPolicy.SecuritySettings;

/// <summary>A principal of a security setting as shown: the value written to the file and its display name.</summary>
/// <param name="Value">The SID (without <c>*</c>) or the name as written.</param>
/// <param name="DisplayName">The resolved or localized name; a SID that cannot be resolved stays the SID.</param>
/// <param name="IsSid">The file holds a SID; otherwise a name each client resolves when it applies the setting.</param>
/// <param name="ToolTip">The SID, or the hint that a typed name is resolved on each client.</param>
public sealed record SecurityPrincipalViewModel(string Value, string DisplayName, bool IsSid, string ToolTip)
{
    /// <summary>The principal was typed as a name (shown with a warning).</summary>
    public bool IsTypedName => !IsSid;

    public override string ToString() => DisplayName;
}

/// <summary>One value of a choice option (also enabled/disabled of a boolean option and the start modes of services).</summary>
public sealed record SecurityChoice(long Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One audit state: null not configured, 0 no auditing, 1 success, 2 failure, 3 both.</summary>
public sealed record AuditStateChoice(int? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The action of a GPP local group item.</summary>
public sealed record GppActionChoice(GppItemAction Action, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A row the editor refreshes in place: setters raised while refreshing are not edits.</summary>
public abstract class SecuritySettingRowBase : ObservableObject
{
    private bool _canEdit;
    private bool _isChanged;

    /// <summary>The row is being refreshed from the document (its property changes are not edits).</summary>
    protected bool IsUpdating { get; private set; }

    /// <summary>The file of the row can be edited now.</summary>
    public bool CanEdit
    {
        get => _canEdit;
        internal set => SetProperty(ref _canEdit, value);
    }

    /// <summary>Differs from the file as read.</summary>
    public bool IsChanged
    {
        get => _isChanged;
        internal set => SetProperty(ref _isChanged, value);
    }

    internal void Update(Action set)
    {
        IsUpdating = true;
        try
        {
            set();
        }
        finally
        {
            IsUpdating = false;
        }
    }
}

/// <summary>"Prawa użytkownika": one right of the catalog (or of the file only).</summary>
public sealed partial class UserRightRowViewModel : SecuritySettingRowBase
{
    private readonly SecuritySettingsEditorViewModel _owner;

    internal UserRightRowViewModel(SecuritySettingsEditorViewModel owner, string constant, string name, bool isKnown)
    {
        _owner = owner;
        Constant = constant;
        Name = name;
        IsKnown = isKnown;
    }

    /// <summary>The constant (<c>SeDebugPrivilege</c>).</summary>
    public string Constant { get; }

    /// <summary>The localized name (the constant for a right the catalog does not know).</summary>
    public string Name { get; }

    /// <summary>The right is in the catalog (unknown rights of the file are listed after the catalog's).</summary>
    public bool IsKnown { get; }

    /// <summary>"Zdefiniuj te ustawienia zasad": the right has a line (no holders: nobody).</summary>
    [ObservableProperty]
    public partial bool IsDefined { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SecurityPrincipalViewModel> Holders { get; internal set; } = [];

    /// <summary>"Nie zdefiniowano", "Nikt" or the holders' names.</summary>
    [ObservableProperty]
    public partial string HoldersText { get; internal set; } = string.Empty;

    partial void OnIsDefinedChanged(bool value)
    {
        if (!IsUpdating)
        {
            _owner.OnRightDefinedChanged(this);
        }
    }

    public override string ToString() => Name;
}

/// <summary>"Grupy z ograniczeniami": one group with its Members and Memberof lines.</summary>
public sealed partial class RestrictedGroupRowViewModel : SecuritySettingRowBase
{
    private readonly SecuritySettingsEditorViewModel _owner;

    internal RestrictedGroupRowViewModel(SecuritySettingsEditorViewModel owner, string group)
    {
        _owner = owner;
        GroupValue = group;
    }

    /// <summary>The group as written (SID without <c>*</c>, or a name).</summary>
    public string GroupValue { get; }

    [ObservableProperty]
    public partial SecurityPrincipalViewModel? Group { get; internal set; }

    /// <summary>"Członkowie tej grupy" are defined (the client then replaces the membership at every refresh).</summary>
    [ObservableProperty]
    public partial bool MembersDefined { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SecurityPrincipalViewModel> Members { get; internal set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<SecurityPrincipalViewModel> MemberOf { get; internal set; } = [];

    [ObservableProperty]
    public partial string MembersText { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial string MemberOfText { get; internal set; } = string.Empty;

    partial void OnMembersDefinedChanged(bool value)
    {
        if (!IsUpdating)
        {
            _owner.OnMembersDefinedChanged(this);
        }
    }

    public override string ToString() => Group?.DisplayName ?? GroupValue;
}

/// <summary>One member line of a GPP local group item.</summary>
/// <param name="Principal">The member.</param>
/// <param name="Action">Add or remove.</param>
/// <param name="ActionText">"Dodaj" / "Usuń".</param>
/// <param name="Member">The member as read (written back unchanged).</param>
public sealed record GppMemberViewModel(SecurityPrincipalViewModel Principal, GppMemberAction Action, string ActionText, GppGroupMember Member)
{
    public override string ToString() => Principal.DisplayName;
}

/// <summary>"Preferencje – grupy lokalne": one local group item of Groups.xml (by its position).</summary>
public sealed partial class GppGroupItemViewModel : SecuritySettingRowBase
{
    private readonly SecuritySettingsEditorViewModel _owner;

    internal GppGroupItemViewModel(SecuritySettingsEditorViewModel owner, int index, IReadOnlyList<GppActionChoice> actions)
    {
        _owner = owner;
        Index = index;
        Actions = actions;
    }

    /// <summary>The position in Groups.xml (the order the client applies the items).</summary>
    public int Index { get; }

    public IReadOnlyList<GppActionChoice> Actions { get; }

    /// <summary>The group's localized name (built-in groups) or its name as written.</summary>
    [ObservableProperty]
    public partial string GroupName { get; internal set; } = string.Empty;

    /// <summary>The group's name and SID as written (tooltip).</summary>
    [ObservableProperty]
    public partial string GroupDetail { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial GppActionChoice? SelectedAction { get; set; }

    [ObservableProperty]
    public partial bool DeleteAllUsers { get; set; }

    [ObservableProperty]
    public partial bool DeleteAllGroups { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<GppMemberViewModel> Members { get; internal set; } = [];

    [ObservableProperty]
    public partial string MembersText { get; internal set; } = string.Empty;

    /// <summary>Item-level targeting: applied only where its filter matches (kept by every save, not edited here).</summary>
    [ObservableProperty]
    public partial bool HasTargeting { get; internal set; }

    /// <summary>The item (or its collection) is disabled: clients skip it.</summary>
    [ObservableProperty]
    public partial bool IsDisabled { get; internal set; }

    partial void OnSelectedActionChanged(GppActionChoice? value)
    {
        if (!IsUpdating)
        {
            _owner.OnGppItemEdited(this);
        }
    }

    partial void OnDeleteAllUsersChanged(bool value)
    {
        if (!IsUpdating)
        {
            _owner.OnGppItemEdited(this);
        }
    }

    partial void OnDeleteAllGroupsChanged(bool value)
    {
        if (!IsUpdating)
        {
            _owner.OnGppItemEdited(this);
        }
    }

    public override string ToString() => GroupName;
}

/// <summary>A group of "Zasady kont i opcje zabezpieczeń" (or the template entries outside the catalog).</summary>
/// <param name="Group">The group (null: the raw entries).</param>
/// <param name="Name">The localized name.</param>
/// <param name="Rows">The options of the group.</param>
/// <param name="Note">The note shown above the rows (domain account policies).</param>
public sealed record SecurityOptionGroupViewModel(SecurityOptionGroup? Group, string Name, IReadOnlyList<SecurityOptionRowViewModel> Rows, string? Note)
{
    /// <summary>The entries of [System Access], [Kerberos Policy] and [Registry Values] the catalog does not know.</summary>
    public bool IsRaw => Group is null;

    public override string ToString() => Name;
}

/// <summary>One account policy or security option: "Nie zdefiniowano" or its value with an editor by kind.</summary>
public sealed partial class SecurityOptionRowViewModel : SecuritySettingRowBase
{
    private readonly SecuritySettingsEditorViewModel _owner;

    internal SecurityOptionRowViewModel(SecuritySettingsEditorViewModel owner, SecurityOptionDefinition definition, string name, IReadOnlyList<SecurityChoice> choices, string? rangeText)
    {
        _owner = owner;
        Definition = definition;
        Name = name;
        Choices = choices;
        RangeText = rangeText;
    }

    public SecurityOptionDefinition Definition { get; }

    public string Name { get; }

    /// <summary>Where it is written: <c>[System Access] MinimumPasswordLength</c> or the registry path.</summary>
    public string KeyText => Definition.Location == SecurityOptionLocation.RegistryValue ? Definition.Key : $"[{Definition.Section}] {Definition.Key}";

    /// <summary>A number or a text: a text box.</summary>
    public bool UsesText => Definition.Kind is SecurityValueKind.Number or SecurityValueKind.Text;

    /// <summary>Lines of text (REG_MULTI_SZ): a multi-line text box.</summary>
    public bool UsesMultiLine => Definition.Kind == SecurityValueKind.MultiText;

    /// <summary>Enabled/disabled or one of the choices: a combo box.</summary>
    public bool UsesChoice => Definition.Kind is SecurityValueKind.Boolean or SecurityValueKind.Choice;

    public IReadOnlyList<SecurityChoice> Choices { get; }

    /// <summary>"0–24" for a number (null otherwise).</summary>
    public string? RangeText { get; }

    /// <summary>"Zdefiniuj następujące ustawienie zasad".</summary>
    [ObservableProperty]
    public partial bool IsDefined { get; set; }

    /// <summary>The value of a number, text or lines option as edited.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SecurityChoice? SelectedChoice { get; set; }

    /// <summary>"Nie zdefiniowano" or the value as shown in the list.</summary>
    [ObservableProperty]
    public partial string ValueText { get; internal set; } = string.Empty;

    /// <summary>Why the value as edited is not valid (the document keeps the last valid value; the save is blocked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; internal set; }

    public bool HasProblem => Problem is not null;

    partial void OnIsDefinedChanged(bool value)
    {
        if (!IsUpdating)
        {
            _owner.OnOptionEdited(this, definedChanged: true);
        }
    }

    partial void OnTextChanged(string value)
    {
        if (!IsUpdating)
        {
            _owner.OnOptionEdited(this, definedChanged: false);
        }
    }

    partial void OnSelectedChoiceChanged(SecurityChoice? value)
    {
        if (!IsUpdating)
        {
            _owner.OnOptionEdited(this, definedChanged: false);
        }
    }

    public override string ToString() => Name;
}

/// <summary>A key of [System Access], [Kerberos Policy] or [Registry Values] the catalog does not know, as written.</summary>
public sealed partial class RawTemplateEntryViewModel : SecuritySettingRowBase
{
    internal RawTemplateEntryViewModel(string section, string key, string value)
    {
        Section = section;
        Key = key;
        Value = value;
    }

    public string Section { get; }

    public string Key { get; }

    /// <summary>The value as written (<c>4,1</c>).</summary>
    public string Value { get; }

    public override string ToString() => $"[{Section}] {Key}";
}

/// <summary>"Inspekcja": a legacy category ([Event Audit]) or an advanced subcategory (audit.csv).</summary>
public sealed partial class AuditRowViewModel : SecuritySettingRowBase
{
    private readonly SecuritySettingsEditorViewModel _owner;

    internal AuditRowViewModel(SecuritySettingsEditorViewModel owner, string id, string name, IReadOnlyList<AuditStateChoice> states, AuditSubcategoryDefinition? subcategory)
    {
        _owner = owner;
        Id = id;
        Name = name;
        States = states;
        Subcategory = subcategory;
    }

    /// <summary>The [Event Audit] key or the subcategory id.</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>The advanced subcategory (null: a legacy category).</summary>
    public AuditSubcategoryDefinition? Subcategory { get; }

    public bool IsAdvanced => Subcategory is not null;

    public IReadOnlyList<AuditStateChoice> States { get; }

    [ObservableProperty]
    public partial AuditStateChoice? SelectedState { get; set; }

    partial void OnSelectedStateChanged(AuditStateChoice? value)
    {
        if (!IsUpdating)
        {
            _owner.OnAuditEdited(this);
        }
    }

    public override string ToString() => Name;
}

/// <summary>A category of the advanced audit policy with its subcategories.</summary>
public sealed record AuditCategoryViewModel(string Category, string Name, IReadOnlyList<AuditRowViewModel> Rows)
{
    public override string ToString() => Name;
}

/// <summary>"Usługi": one line of [Service General Setting].</summary>
public sealed partial class ServiceRowViewModel : SecuritySettingRowBase
{
    private readonly SecuritySettingsEditorViewModel _owner;

    internal ServiceRowViewModel(SecuritySettingsEditorViewModel owner, string name, IReadOnlyList<SecurityChoice> modes)
    {
        _owner = owner;
        Name = name;
        Modes = modes;
    }

    /// <summary>The service's short name.</summary>
    public string Name { get; }

    public IReadOnlyList<SecurityChoice> Modes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModeEditable))]
    public partial int StartMode { get; internal set; }

    /// <summary>Automatic, manual or disabled (null for boot and system, kept as read).</summary>
    [ObservableProperty]
    public partial SecurityChoice? SelectedMode { get; set; }

    [ObservableProperty]
    public partial string ModeText { get; internal set; } = string.Empty;

    /// <summary>The service's security descriptor (SDDL) or "brak"; never edited here.</summary>
    [ObservableProperty]
    public partial string SecurityText { get; internal set; } = string.Empty;

    /// <summary>The start mode is one the editor writes (2, 3, 4).</summary>
    public bool IsModeEditable => SecuritySettingsCatalog.ServiceStartModes.Contains(StartMode);

    partial void OnSelectedModeChanged(SecurityChoice? value)
    {
        if (!IsUpdating)
        {
            _owner.OnServiceModeEdited(this);
        }
    }

    public override string ToString() => Name;
}
