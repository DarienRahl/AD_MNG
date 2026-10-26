using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Filters;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.AccountReview;

/// <summary>Resource keys of the "Uprawnienia" tab (dynamic families of <see cref="L.PrivilegedAccess"/>).</summary>
public static class PrivilegedAccessText
{
    public static string LevelKey(PrivilegeLevel level) => $"PrivilegedAccess.Level.{level}";

    public static string LevelHintKey(PrivilegeLevel level) => $"PrivilegedAccess.LevelHint.{level}";

    public static string SourceKey(PrivilegeSource source) => $"PrivilegedAccess.Source.{source}";

    public static string KindKey(PrivilegeFindingKind kind) => $"PrivilegedAccess.Kind.{kind}";

    public static string GroupKey(string catalogId) => $"PrivilegedAccess.Group.{catalogId}";

    public static string GroupNameKey(string catalogId) => $"PrivilegedAccess.GroupName.{catalogId}";

    public static string ScopeKey(PrivilegeScopeKind scope) => $"PrivilegedAccess.Scope.{scope}";

    public static string TypeKey(PrivilegedAccountType type) => $"PrivilegedAccess.Type.{type}";

    public static string PeerKey(PeerDimension dimension) => $"PrivilegedAccess.Peer.{dimension}";

    public static string TargetKey(PrivilegeRuleTarget target) => $"PrivilegedAccess.Target.{target}";

    public static string ColumnKey(string column) => $"PrivilegedAccess.Column.{column}";

    /// <summary>The name of a right of an ACL delegation (U4b).</summary>
    public static string AclRightKey(AclRight right) => $"PrivilegedAccess.AclRight.{right}";

    /// <summary>What a local group or user right of <see cref="LocalRightsCatalog"/> gives (U4c).</summary>
    public static string LocalRightKey(string id) => $"PrivilegedAccess.LocalRight.{id}";

    /// <summary>The name of a user right as the security policy editor shows it (U4c).</summary>
    public static string UserRightNameKey(string id) => $"PrivilegedAccess.UserRightName.{id}";

    public static string MechanismKey(LocalRightMechanism mechanism) => $"PrivilegedAccess.Mechanism.{mechanism}";

    /// <summary>A notice of a grant source (<see cref="PrivilegeSourceNotice.Code"/>).</summary>
    public static string NoticeKey(string code) => $"PrivilegedAccess.Notice.{code}";

    /// <summary>The finding kinds of the sources U4a implements (their keys are complete; U4b/U4c kinds have keys too).</summary>
    public static IReadOnlyList<PrivilegeFindingKind> Kinds { get; } = Enum.GetValues<PrivilegeFindingKind>();
}

/// <summary>Columns of the privileged accounts list (ids are the binding keys of <see cref="PrivilegedAccountRowViewModel"/>).</summary>
public static class PrivilegedAccessColumns
{
    public const string Name = "Name";
    public const string Login = "Login";
    public const string Type = "Type";
    public const string Level = "Level";
    public const string Sources = "Sources";
    public const string Findings = "Findings";
    public const string Peers = "Peers";
    public const string Location = "Location";
    public const string Details = "Details";
    public const string DistinguishedName = "DistinguishedName";

    /// <summary>The columns of the list.</summary>
    public static IReadOnlyList<string> Shown { get; } = [Name, Login, Type, Level, Sources, Findings, Peers, Location];

    /// <summary>Every column id (localization keys <c>PrivilegedAccess.Column.{id}</c>); the CSV export adds the last two.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Shown, Details, DistinguishedName];
}

/// <summary>Texts of the findings and rows in the UI language.</summary>
public sealed class PrivilegedAccessFormatter(ILocalizer localizer, string domainDnsName)
{
    public ILocalizer Localizer { get; } = localizer;

    public string Level(PrivilegeLevel level) => Localizer[PrivilegedAccessText.LevelKey(level)];

    public string Source(PrivilegeSource source) => Localizer[PrivilegedAccessText.SourceKey(source)];

    public string Type(PrivilegedAccountType type) => Localizer[PrivilegedAccessText.TypeKey(type)];

    /// <summary>
    /// What the finding is: "Członek grupy Operatorzy kont", "Delegowanie nieograniczone", "Nietypowe grupy",
    /// "Uprawnienie w zabezpieczeniach obiektu Firma" (U4b: the object whose ACL holds the right), "Uprawnienie lokalne z
    /// zasad grupy: Administratorzy" (U4c: the local group or user right).
    /// </summary>
    public string Title(PrivilegeFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var subject = finding switch
        {
            { Kind: PrivilegeFindingKind.WellKnownAccount } => finding.Values.FirstOrDefault() ?? finding.SubjectName ?? string.Empty,
            { Kind: PrivilegeFindingKind.AclRight } => finding.ScopeName ?? ObjectName(finding.ScopeDn) ?? finding.SubjectName ?? string.Empty,
            { Kind: PrivilegeFindingKind.LocalRight, GroupPolicy: { } gpo } => RightName(gpo.Definition),
            _ => finding.SubjectName ?? string.Empty,
        };
        return Localizer.Format(PrivilegedAccessText.KindKey(finding.Kind), subject);
    }

    /// <summary>
    /// "Administratorzy" (a local group by its Windows name, a domain group by its catalogue name) or "Debugowanie programów
    /// (SeDebugPrivilege)".
    /// </summary>
    public string RightName(LocalRightDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Kind == LocalRightKind.UserRight
            ? Localizer.Format(L.PrivilegedAccess.UserRight, Localizer[PrivilegedAccessText.UserRightNameKey(definition.Id)], definition.Key)
            : Localizer[PrivilegedAccessText.GroupNameKey(definition.Id)];
    }

    /// <summary>
    /// What a local right from a GPO gives, the mechanism, the GPO and the computers it reaches, plus enforced links and
    /// conditions (item-level targeting, WMI filter).
    /// </summary>
    public string LocalRight(GroupPolicyGrantDetails gpo)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        var culture = Localizer.Culture;
        var computers = Localizer.Format(L.PrivilegedAccess.LocalRightComputers, gpo.Computers.ToString("N0", culture),
            gpo.DomainControllers.ToString("N0", culture), gpo.Servers.ToString("N0", culture), gpo.Workstations.ToString("N0", culture));
        var domainGroup = gpo.Definition.Kind == LocalRightKind.DomainGroup;
        var gives = Localizer[domainGroup ? PrivilegedAccessText.GroupKey(gpo.Definition.Id) : PrivilegedAccessText.LocalRightKey(gpo.Definition.Id)];
        var parts = new List<string>
        {
            Localizer.Format(L.PrivilegedAccess.LocalRightGrants, gives, Localizer[PrivilegedAccessText.MechanismKey(gpo.Mechanism)], gpo.GpoName, computers),
        };
        if (domainGroup)
        {
            parts.Add(Localizer[L.PrivilegedAccess.DomainGroupEnforced]);
        }

        if (gpo.LinkEnforced)
        {
            parts.Add(Localizer[L.PrivilegedAccess.LinkEnforced]);
        }

        if (gpo.ItemLevelTargeting)
        {
            parts.Add(Localizer[L.PrivilegedAccess.ConditionalTargeting]);
        }

        if (gpo.WmiFilter)
        {
            parts.Add(Localizer[L.PrivilegedAccess.ConditionalWmi]);
        }

        return string.Join(" ", parts);
    }

    /// <summary>A notice of a grant source in the UI language.</summary>
    public string Notice(PrivilegeSourceNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return Localizer.Format(PrivilegedAccessText.NoticeKey(notice.Code), [.. notice.Arguments]);
    }

    /// <summary>"Henryk Żuławski → GG_Helpdesk → GG_Wsparcie_IT → Operatorzy kont" (primary and implicit groups named).</summary>
    public string Path(PrivilegeFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return string.Join(" → ", finding.Path.Select(Step));
    }

    /// <summary>Where the right applies: "Domena contoso.test", "Kontrolery domeny contoso.test", "Samo konto" …</summary>
    public string Scope(PrivilegeFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var argument = finding.Scope switch
        {
            PrivilegeScopeKind.Container or PrivilegeScopeKind.Computers when finding.ScopeDn is { } dn => AddressResolver.ToDisplayPath(dn),
            PrivilegeScopeKind.Peers when finding.Peer is { } peer => PeerSet(peer),
            _ => domainDnsName,
        };
        return Localizer.Format(PrivilegedAccessText.ScopeKey(finding.Scope), argument);
    }

    /// <summary>What the right gives (built-in groups), or the peer comparison; empty otherwise.</summary>
    public string Grants(PrivilegeFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.CatalogId is { } id)
        {
            return Localizer[PrivilegedAccessText.GroupKey(id)];
        }

        if (finding.Peer is { } peer)
        {
            return Localizer.Format(PrivilegedAccessText.PeerKey(peer.Dimension), peer.Groups.Count.ToString("N0", Localizer.Culture),
                peer.PeerCount.ToString("N0", Localizer.Culture), PeerSet(peer));
        }

        if (finding.GroupPolicy is { } gpo)
        {
            return LocalRight(gpo);
        }

        return string.Empty;
    }

    /// <summary>SPNs, delegation targets, SIDs; for peers the unusual groups with how many others hold them.</summary>
    public string Values(PrivilegeFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.Peer is { } peer)
        {
            return string.Join(", ", peer.Groups.Select(g => $"{g.Name} ({Holders(g.Holders)})"));
        }

        if (finding.GroupPolicy is { } gpo)
        {
            return Localizer.Format(L.PrivilegedAccess.LocalRightValues, gpo.GpoName, RightName(gpo.Definition));
        }

        return finding.Kind switch
        {
            PrivilegeFindingKind.WellKnownAccount => string.Empty,
            PrivilegeFindingKind.AclRight => string.Join(", ", finding.Values.Select(AclRightText)),
            _ => string.Join(", ", finding.Values),
        };
    }

    /// <summary>A right of an ACL delegation in the UI language: "Resetowanie hasła (user)"; other values as they are.</summary>
    public string AclRightText(string value)
    {
        if (!AclRights.TryParse(value, out var right, out var detail))
        {
            return value;
        }

        var text = Localizer[PrivilegedAccessText.AclRightKey(right)];
        return string.IsNullOrEmpty(detail) ? text : $"{text} ({detail})";
    }

    /// <summary>One line of a finding (CSV "Details"): "Tier 0: Członek grupy Operatorzy kont (Henryk → … → Operatorzy kont)".</summary>
    public string Line(PrivilegeFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var path = finding.Path.Count > 1 ? $" ({Path(finding)})" : string.Empty;
        var values = Values(finding) is { Length: > 0 } v ? $": {v}" : string.Empty;
        return $"{Level(finding.Level)}: {Title(finding)}{values}{path}";
    }

    public string Holders(int holders) => holders == 0
        ? Localizer[L.PrivilegedAccess.HoldersNone]
        : Localizer.Format(L.PrivilegedAccess.Holders, holders.ToString("N0", Localizer.Culture));

    /// <summary>The RDN value of a DN; the DNS name for a domain head (DC=…).</summary>
    private static string? ObjectName(string? dn) => Core.Ldap.DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot
        ? string.Equals(parsed.Leaf.Type, "DC", StringComparison.OrdinalIgnoreCase) ? parsed.DnsDomain : parsed.Name
        : null;

    private string PeerSet(PeerOutlierDetails peer) => peer.Dimension == PeerDimension.OrganizationalUnit
        ? AddressResolver.ToDisplayPath(peer.PeerSet)
        : peer.PeerSet;

    private string Step(PrivilegePathStep step) => step.Kind switch
    {
        PrivilegePathStepKind.PrimaryGroup => $"{step.Name} {Localizer[L.PrivilegedAccess.PrimaryGroup]}",
        PrivilegePathStepKind.ImplicitGroup => step.Name switch
        {
            PrivilegedGroupCatalog.EveryoneSid => Localizer[L.PrivilegedAccess.Everyone],
            PrivilegedGroupCatalog.AuthenticatedUsersSid => Localizer[L.PrivilegedAccess.AuthenticatedUsers],
            _ => step.Name,
        },
        _ => step.Name,
    };
}

/// <summary>One finding in the details pane.</summary>
public sealed class PrivilegeFindingViewModel(PrivilegeFinding finding, PrivilegedAccessFormatter formatter)
{
    public PrivilegeFinding Finding { get; } = finding;

    public PrivilegeLevel Level => Finding.Level;

    public string LevelText { get; } = formatter.Level(finding.Level);

    public string SourceText { get; } = formatter.Source(finding.Source);

    public string Title { get; } = formatter.Title(finding);

    /// <summary>"Skąd": the path (empty for findings about the account itself).</summary>
    public string PathText { get; } = finding.Path.Count > 1 ? formatter.Path(finding) : string.Empty;

    public string ScopeText { get; } = formatter.Scope(finding);

    public string GrantsText { get; } = formatter.Grants(finding);

    public string ValuesText { get; } = formatter.Values(finding);

    public string NoteText { get; } = finding.Note ?? string.Empty;

    public bool HasPath => PathText.Length > 0;

    /// <summary>"Usuń z grupy…" applies: the path starts with a group the account is a direct member of.</summary>
    public bool CanRemoveFromGroup => Finding.DirectGroupDn is not null;
}

/// <summary>One account of the privileged accounts list.</summary>
public sealed class PrivilegedAccountRowViewModel : ObservableObject
{
    private PrivilegedAccessFormatter _formatter;

    internal PrivilegedAccountRowViewModel(AccountPrivileges facts, PrivilegedAccessFormatter formatter)
    {
        Facts = facts;
        _formatter = formatter;
    }

    public AccountPrivileges Facts { get; private set; }

    public string Dn => Facts.DistinguishedName;

    public string Name => Facts.Account.GetString(Attr.DisplayName) is { Length: > 0 } display ? display : Facts.Account.Name;

    public PrivilegeLevel Level => Facts.Level;

    public bool IsDisabled => Facts.IsDisabled;

    public bool IsPeerOutlier => Facts.IsPeerOutlier;

    public ObjectIcon Icon => ObjectIcons.For(Facts.Account.Kind, Facts.IsDisabled, locked: false);

    /// <summary>Text of a column (<see cref="PrivilegedAccessColumns"/>); bound as <c>[ColumnId]</c>.</summary>
    public string this[string column] => column switch
    {
        PrivilegedAccessColumns.Name => Facts.IsDisabled ? $"{Name} {_formatter.Localizer[L.PrivilegedAccess.Disabled]}" : Name,
        PrivilegedAccessColumns.Login => Facts.Account.GetString(Attr.SamAccountName) ?? string.Empty,
        PrivilegedAccessColumns.Type => _formatter.Type(Facts.Type),
        PrivilegedAccessColumns.Level => _formatter.Level(Facts.Level),
        PrivilegedAccessColumns.Sources => string.Join(", ", Facts.Sources.Order().Select(_formatter.Source)),
        PrivilegedAccessColumns.Findings => Facts.Findings.Count.ToString("N0", _formatter.Localizer.Culture),
        PrivilegedAccessColumns.Peers => Facts.IsPeerOutlier ? _formatter.Localizer[L.PrivilegedAccess.Unusual] : string.Empty,
        PrivilegedAccessColumns.Location => Core.Ldap.DistinguishedName.TryParse(Dn, out var dn) && dn.Parent is { IsRoot: false } parent
            ? AddressResolver.ToDisplayPath(parent.ToString())
            : string.Empty,
        PrivilegedAccessColumns.Details => string.Join(" | ", Facts.Findings.Select(_formatter.Line)),
        PrivilegedAccessColumns.DistinguishedName => Dn,
        _ => string.Empty,
    };

    /// <summary>Sort key: the level and the counts by value, the rest by text.</summary>
    internal IComparable SortKey(string column) => column switch
    {
        PrivilegedAccessColumns.Level => (int)Facts.Level,
        PrivilegedAccessColumns.Findings => Facts.Findings.Count,
        PrivilegedAccessColumns.Peers => Facts.IsPeerOutlier ? 1 : 0,
        _ => this[column],
    };

    internal void Update(AccountPrivileges facts)
    {
        Facts = facts;
        RaiseAll();
    }

    internal void UseFormatter(PrivilegedAccessFormatter formatter)
    {
        _formatter = formatter;
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged("Item[]");
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(Facts));
        OnPropertyChanged(nameof(IsPeerOutlier));
    }
}

/// <summary>A level tile: the number of accounts whose level it is; a toggle that filters the list.</summary>
public sealed partial class PrivilegeLevelTileViewModel(PrivilegeLevel level) : ObservableObject
{
    public PrivilegeLevel Level { get; } = level;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Hint { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccounts))]
    public partial int Count { get; internal set; }

    [ObservableProperty]
    public partial string CountText { get; internal set; } = "0";

    /// <summary>Accounts of this level are listed.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; } = true;

    public bool HasAccounts => Count > 0;
}

/// <summary>A source in the filter of the list.</summary>
public sealed partial class PrivilegeSourceOptionViewModel(PrivilegeSource source) : ObservableObject
{
    public PrivilegeSource Source { get; } = source;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Count { get; internal set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; } = true;
}

/// <summary>A catalogue group with the operator's level (settings).</summary>
public sealed partial class PrivilegedGroupLevelViewModel : ObservableObject
{
    internal PrivilegedGroupLevelViewModel(PrivilegedGroupDefinition group, IReadOnlyList<Option<PrivilegeLevel>> levels, PrivilegeLevel level, ILocalizer localizer)
    {
        Group = group;
        Levels = levels;
        Level = levels.First(l => l.Value == level);
        Name = localizer[PrivilegedAccessText.GroupNameKey(group.Id)];
        Description = localizer[PrivilegedAccessText.GroupKey(group.Id)];
        Identity = group.BuiltinSid ?? (group.Rid is { } rid ? $"RID {rid.ToString(CultureInfo.InvariantCulture)}" : group.SamAccountName ?? string.Empty);
    }

    public event EventHandler? Changed;

    public PrivilegedGroupDefinition Group { get; }

    public IReadOnlyList<Option<PrivilegeLevel>> Levels { get; }

    public string Name { get; }

    public string Description { get; }

    /// <summary>The SID, RID or sAMAccountName the group is recognised by.</summary>
    public string Identity { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    public partial Option<PrivilegeLevel> Level { get; set; }

    /// <summary>The operator changed the default level.</summary>
    public bool IsChanged => Level.Value != Group.DefaultLevel;

    partial void OnLevelChanged(Option<PrivilegeLevel> value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>A group or account marked as privileged (settings: SID/DN, level, note).</summary>
public sealed partial class PrivilegedMarkViewModel : ObservableObject
{
    internal PrivilegedMarkViewModel(Settings.PrivilegedMarkSetting mark, IReadOnlyList<Option<PrivilegeLevel>> levels)
    {
        Sid = mark.Sid;
        Dn = mark.Dn;
        Levels = levels;
        Level = levels.First(l => l.Value == PrivilegeLevels.Normalize(mark.Level));
        Note = mark.Note ?? string.Empty;
        Name = Core.Ldap.DistinguishedName.TryParse(Dn?.Replace(Views.SavedView.DomainPlaceholder, "DC=x", StringComparison.OrdinalIgnoreCase), out var dn) && !dn.IsRoot
            ? dn.Name
            : Sid ?? string.Empty;
    }

    public event EventHandler? Changed;

    public string? Sid { get; }

    /// <summary>Portable DN (<c>{domain}</c>).</summary>
    public string? Dn { get; }

    /// <summary>The RDN value of the DN (or the SID).</summary>
    public string Name { get; }

    public IReadOnlyList<Option<PrivilegeLevel>> Levels { get; }

    [ObservableProperty]
    public partial Option<PrivilegeLevel> Level { get; set; }

    [ObservableProperty]
    public partial string Note { get; set; }

    public Settings.PrivilegedMarkSetting ToSetting() => new()
    {
        Sid = Sid,
        Dn = Dn,
        Level = Level.Value,
        Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
    };

    partial void OnLevelChanged(Option<PrivilegeLevel> value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnNoteChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>A saved filter used as "mark as privileged" (settings: filter, target, level, note).</summary>
public sealed partial class PrivilegedRuleViewModel : ObservableObject
{
    internal PrivilegedRuleViewModel(
        Settings.PrivilegedRuleSetting rule,
        IReadOnlyList<Option<string>> filters,
        IReadOnlyList<Option<PrivilegeRuleTarget>> targets,
        IReadOnlyList<Option<PrivilegeLevel>> levels)
    {
        Filters = filters;
        Targets = targets;
        Levels = levels;
        Filter = filters.FirstOrDefault(f => f.Value == rule.FilterId);
        MissingFilterId = Filter is null ? rule.FilterId : null;
        Target = targets.First(t => t.Value == rule.Target);
        Level = levels.First(l => l.Value == PrivilegeLevels.Normalize(rule.Level));
        Note = rule.Note ?? string.Empty;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<Option<string>> Filters { get; }

    public IReadOnlyList<Option<PrivilegeRuleTarget>> Targets { get; }

    public IReadOnlyList<Option<PrivilegeLevel>> Levels { get; }

    /// <summary>The rule's filter id when the filter no longer exists (kept until the operator changes it).</summary>
    public string? MissingFilterId { get; }

    [ObservableProperty]
    public partial Option<string>? Filter { get; set; }

    [ObservableProperty]
    public partial Option<PrivilegeRuleTarget> Target { get; set; }

    [ObservableProperty]
    public partial Option<PrivilegeLevel> Level { get; set; }

    [ObservableProperty]
    public partial string Note { get; set; }

    public Settings.PrivilegedRuleSetting? ToSetting() => (Filter?.Value ?? MissingFilterId) is { Length: > 0 } id
        ? new Settings.PrivilegedRuleSetting { FilterId = id, Target = Target.Value, Level = Level.Value, Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim() }
        : null;

    partial void OnFilterChanged(Option<string>? value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnTargetChanged(Option<PrivilegeRuleTarget> value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnLevelChanged(Option<PrivilegeLevel> value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnNoteChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Builds the account review's extra filter (saved filters) for both tabs.</summary>
internal static class ReviewFilters
{
    /// <summary>The plan of the saved filters with these ids (unknown ids are skipped).</summary>
    public static Filters.SavedFilterPlan? Plan(Filters.FilterLibrary library, IEnumerable<string> ids, FilterContext context)
    {
        var definitions = ids.Select(library.Find).OfType<Filters.SavedFilter>().Select(f => f.Definition).ToList();
        return definitions.Count == 0 ? null : Filters.SavedFilterPlan.Create(definitions, context);
    }
}
