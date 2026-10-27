using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>The tabs of a GPO document (GPMC's tabs plus "Stan").</summary>
public enum GpoDocumentTab
{
    /// <summary>"Zakres": links, security filtering, WMI filtering.</summary>
    Scope,

    /// <summary>"Szczegóły": domain, owner, dates, versions, GUID, GPO status.</summary>
    Details,

    /// <summary>"Ustawienia": the normalized settings (read-only).</summary>
    Settings,

    /// <summary>"Delegowanie": trustees with GPMC levels.</summary>
    Delegation,

    /// <summary>"Stan": AD/SYSVOL consistency.</summary>
    Status,
}

/// <summary>A link of the GPO (Zakres tab).</summary>
public sealed class GpoLinkRowViewModel
{
    public required GpoLinkInfo Link { get; init; }

    public string ContainerName => Link.ContainerName;

    /// <summary>"contoso.test/Komputery".</summary>
    public required string PathText { get; init; }

    public required string KindText { get; init; }

    public int Order => Link.Order;

    public bool Enforced => Link.Enforced;

    public bool Enabled => Link.Enabled;

    public required string EnforcedText { get; init; }

    public required string EnabledText { get; init; }
}

/// <summary>A trustee of the GPO's permissions (security filtering, delegation).</summary>
public sealed class GpoTrusteeRowViewModel
{
    public required string Sid { get; init; }

    public required string Name { get; init; }

    public required GpoPermissionLevel Level { get; init; }

    public required string LevelText { get; init; }

    public bool Inherited { get; init; }

    public required string InheritedText { get; init; }

    /// <summary>A custom ACE set: only removable as a whole.</summary>
    public bool IsCustom => Level == GpoPermissionLevel.Custom;
}

/// <summary>A normalized setting (Ustawienia tab).</summary>
public sealed record GpoSettingRowViewModel(string PathText, string Name, string Value, string Source);

/// <summary>A consistency check (Stan tab).</summary>
public sealed record GpoCheckRowViewModel(string Text, string Detail, bool IsOk);

/// <summary>An option of a combo (WMI filter, GPO status, permission level).</summary>
/// <typeparam name="T">The value.</typeparam>
public sealed record GpoOption<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// One GPO (design note 5.2): Zakres (links, security filtering with Add/Remove, WMI filter), Szczegóły (domain, owner,
/// dates, versions of AD and SYSVOL, GUID, GPO status), Ustawienia (the normalized settings of the shared parsers –
/// "Komputer / Zabezpieczenia / …", masked values never shown), Delegowanie (GPMC levels: Add, Change level, Remove) and Stan
/// (folder present, versions, folder permissions consistent with the container's, extension names against the files,
/// leftovers of interrupted writes; "Wyrównaj wersje", "Napraw uprawnienia folderu"). Every change goes through
/// <see cref="GroupPolicyActions"/> (plan → preview → execute).
/// </summary>
public sealed partial class GroupPolicyObjectDocumentViewModel : GroupPolicyDocumentBase
{
    private readonly string _initialName;
    private GpoInfo? _gpo;

    public GroupPolicyObjectDocumentViewModel(GroupPolicyServices services, GroupPolicyActions actions, Guid gpoId, string? name, GpoDocumentTab tab = GpoDocumentTab.Scope)
        : base(services, actions)
    {
        GpoId = gpoId;
        _initialName = name ?? GroupPolicyText.Guid(gpoId);
        SelectedTab = tab;
        StatusOptions = [];
        LevelOptions = [];
        BuildOptions();
    }

    public Guid GpoId { get; }

    /// <summary>The GPO as last read (null before the first load or when it no longer exists).</summary>
    public GpoInfo? Gpo => _gpo;

    public override string Title => _gpo?.DisplayName ?? _initialName;

    public override ObjectIcon Icon => ObjectIcon.GroupPolicy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTabIndex))]
    public partial GpoDocumentTab SelectedTab { get; set; }

    /// <summary><see cref="SelectedTab"/> as the index of the view's tab control.</summary>
    public int SelectedTabIndex
    {
        get => (int)SelectedTab;
        set => SelectedTab = Enum.IsDefined((GpoDocumentTab)value) ? (GpoDocumentTab)value : GpoDocumentTab.Scope;
    }

    /// <summary>The GPO no longer exists in AD (deleted meanwhile).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteOrphanedFolderCommand))]
    public partial bool IsMissing { get; private set; }

    /// <summary>The GPO is gone from AD but its SYSVOL folder is still there ("Usuń pozostały folder").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteOrphanedFolderCommand))]
    public partial bool HasOrphanedFolder { get; private set; }

    // ---- Zakres -------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<GpoLinkRowViewModel> Links { get; private set; } = [];

    [ObservableProperty]
    public partial GpoLinkRowViewModel? SelectedLink { get; set; }

    /// <summary>Who applies the GPO (trustees with Apply).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoTrusteeRowViewModel> SecurityFilter { get; private set; } = [];

    [ObservableProperty]
    public partial GpoTrusteeRowViewModel? SelectedFilterTrustee { get; set; }

    /// <summary>"(brak)" and the WMI filters of the domain.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoOption<WmiFilterInfo?>> WmiFilters { get; private set; } = [];

    [ObservableProperty]
    public partial GpoOption<WmiFilterInfo?>? SelectedWmiFilter { get; set; }

    // ---- Szczegóły ----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string DomainText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string OwnerText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CreatedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ChangedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string UserVersionText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ComputerVersionText { get; private set; } = string.Empty;

    public string GuidText => GroupPolicyText.Guid(GpoId);

    public IReadOnlyList<GpoOption<GpoStatus>> StatusOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<GpoStatus>? SelectedStatus { get; set; }

    // ---- Ustawienia ---------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<GpoSettingRowViewModel> Settings { get; private set; } = [];

    /// <summary>Why the settings are not shown (SYSVOL unavailable, GPO too large …).</summary>
    [ObservableProperty]
    public partial string? SettingsNote { get; private set; }

    // ---- Delegowanie --------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<GpoTrusteeRowViewModel> Delegation { get; private set; } = [];

    [ObservableProperty]
    public partial GpoTrusteeRowViewModel? SelectedTrustee { get; set; }

    /// <summary>Read, Edit settings, Edit settings + delete + modify security (Apply is set on the Zakres tab).</summary>
    public IReadOnlyList<GpoOption<GpoPermissionLevel>> LevelOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<GpoPermissionLevel>? SelectedLevel { get; set; }

    // ---- Stan ---------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<GpoCheckRowViewModel> Checks { get; private set; } = [];

    /// <summary>AD and GPT.INI versions differ.</summary>
    [ObservableProperty]
    public partial bool HasVersionMismatch { get; private set; }

    /// <summary>The folder DACL differs from the mapping of the container's DACL.</summary>
    [ObservableProperty]
    public partial bool HasFolderAclMismatch { get; private set; }

    partial void OnSelectedLinkChanged(GpoLinkRowViewModel? value) => RefreshCommands();

    partial void OnSelectedFilterTrusteeChanged(GpoTrusteeRowViewModel? value) => RefreshCommands();

    partial void OnSelectedWmiFilterChanged(GpoOption<WmiFilterInfo?>? value) => RefreshCommands();

    partial void OnSelectedStatusChanged(GpoOption<GpoStatus>? value) => RefreshCommands();

    partial void OnSelectedTrusteeChanged(GpoTrusteeRowViewModel? value) => RefreshCommands();

    partial void OnSelectedLevelChanged(GpoOption<GpoPermissionLevel>? value) => RefreshCommands();

    protected override async Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
    {
        var reader = Gp.Reader;
        var gpo = await Task.Run(() => reader.ReadPolicyAsync(session, GpoId, cancellationToken), cancellationToken).ConfigureAwait(true);
        if (gpo is null)
        {
            _gpo = null;
            IsMissing = true;
            HasOrphanedFolder = sysvol is not null && await sysvol.GpoFolderExistsAsync(GpoId, cancellationToken).ConfigureAwait(true);
            Clear();
            LoadedText = Localizer[HasOrphanedFolder ? L.GroupPolicy.MissingWithFolder : L.GroupPolicy.Missing];
            Error = LoadedText;
            OnPropertyChanged(nameof(Title));
            return;
        }

        IsMissing = false;
        HasOrphanedFolder = false;
        if (sysvol is not null)
        {
            gpo = await reader.WithSysvolVersionAsync(sysvol, gpo, cancellationToken).ConfigureAwait(true);
        }

        var links = await Task.Run(() => reader.ReadLinksOfAsync(session, GpoId, cancellationToken), cancellationToken).ConfigureAwait(true);
        var filters = await Task.Run(() => reader.ReadWmiFiltersAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        var permissions = GpoPermissionLevels.Read(gpo.SecurityDescriptor);
        var names = await TrusteeNames.ResolveAsync(session, permissions.Select(p => p.Sid).Append(gpo.OwnerSid ?? string.Empty), Localizer, cancellationToken)
            .ConfigureAwait(true);

        GpoFolderContent? content = null;
        string? contentNote = null;
        byte[]? folderSecurity = null;
        var folderExists = false;
        if (sysvol is not null)
        {
            folderExists = await sysvol.GpoFolderExistsAsync(GpoId, cancellationToken).ConfigureAwait(true);
            if (folderExists)
            {
                try
                {
                    content = await GroupPolicyReader.ReadFolderAsync(sysvol, GpoId, cancellationToken).ConfigureAwait(true);
                    folderSecurity = await sysvol.GetGpoFolderSecurityAsync(GpoId, cancellationToken).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is GpoTooLargeException or SysvolWriteException)
                {
                    contentNote = GroupPolicyText.Error(Localizer, Gp.Errors, ex);
                }
            }
            else
            {
                contentNote = Localizer[L.GroupPolicy.FolderMissing];
            }
        }
        else
        {
            contentNote = SysvolWarning;
        }

        var settings = content is null ? [] : await Task.Run(() => GroupPolicyReader.Settings(content), cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();

        _gpo = gpo;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Gpo));

        // Zakres.
        var selectedLink = SelectedLink?.Link.ContainerDn;
        Links = [.. links.OrderBy(l => l.Kind).ThenBy(l => l.ContainerName, StringComparer.CurrentCultureIgnoreCase).Select(l => new GpoLinkRowViewModel
        {
            Link = l,
            PathText = l.Kind == GroupPolicySomKind.Site ? l.ContainerName : AddressResolver.ToDisplayPath(l.ContainerDn),
            KindText = GroupPolicyText.SomKind(Localizer, l.Kind),
            EnforcedText = GroupPolicyText.YesNo(Localizer, l.Enforced),
            EnabledText = GroupPolicyText.YesNo(Localizer, l.Enabled),
        })];
        SelectedLink = Links.FirstOrDefault(l => string.Equals(l.Link.ContainerDn, selectedLink, StringComparison.OrdinalIgnoreCase));
        var rows = permissions.Select(p => new GpoTrusteeRowViewModel
        {
            Sid = p.Sid,
            Name = TrusteeNames.NameOf(names, p.Sid),
            Level = p.Level,
            LevelText = GroupPolicyText.Level(Localizer, p.Level),
            Inherited = p.Inherited,
            InheritedText = GroupPolicyText.YesNo(Localizer, p.Inherited),
        }).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        SecurityFilter = [.. rows.Where(r => r.Level == GpoPermissionLevel.Apply && !r.Inherited)];
        SelectedFilterTrustee = null;
        WmiFilters = [new GpoOption<WmiFilterInfo?>(null, Localizer[L.GroupPolicy.NoWmiFilter]), .. filters.Select(f => new GpoOption<WmiFilterInfo?>(f, f.Name))];
        SelectedWmiFilter = WmiFilters.FirstOrDefault(o => o.Value is { } f && string.Equals(f.Id, gpo.WmiFilterId, StringComparison.OrdinalIgnoreCase))
            ?? (gpo.WmiFilterId is null ? WmiFilters[0] : new GpoOption<WmiFilterInfo?>(null, gpo.WmiFilterId));

        // Szczegóły.
        var zone = TimeZoneInfo.Local;
        DomainText = session.Info.DomainDnsName;
        OwnerText = TrusteeNames.NameOf(names, gpo.OwnerSid);
        CreatedText = GroupPolicyText.Date(Localizer, gpo.Created, zone);
        ChangedText = GroupPolicyText.Date(Localizer, gpo.Changed, zone);
        UserVersionText = Localizer.Format(L.GroupPolicy.VersionPair, Half(gpo.DirectoryVersion?.User), sysvol is null ? "–" : Half(gpo.SysvolVersion?.User));
        ComputerVersionText = Localizer.Format(L.GroupPolicy.VersionPair, Half(gpo.DirectoryVersion?.Computer), sysvol is null ? "–" : Half(gpo.SysvolVersion?.Computer));
        SelectedStatus = StatusOptions.FirstOrDefault(o => o.Value == gpo.Status);

        // Ustawienia.
        Settings = [.. settings.Select(s => new GpoSettingRowViewModel(GroupPolicyText.SettingPath(Localizer, s), GroupPolicyText.SettingName(s),
            GroupPolicyText.SettingValue(Localizer, s), s.Source ?? string.Empty))];
        SettingsNote = contentNote ?? (Settings.Count == 0 ? Localizer[L.GroupPolicy.NoSettings] : null);

        // Delegowanie.
        var selectedSid = SelectedTrustee?.Sid;
        Delegation = rows;
        SelectedTrustee = rows.FirstOrDefault(r => string.Equals(r.Sid, selectedSid, StringComparison.OrdinalIgnoreCase));

        // Stan.
        Checks = BuildChecks(gpo, sysvol is not null, folderExists, content, folderSecurity);
        LoadedText = Localizer.Format(L.GroupPolicy.ObjectLoaded, Links.Count, Settings.Count);
    }

    protected override void OnNotConnected() => Clear();

    protected override void RefreshCommands()
    {
        OpenLinkContainerCommand.NotifyCanExecuteChanged();
        ToggleEnforcedCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        UnlinkCommand.NotifyCanExecuteChanged();
        AddSecurityFilterCommand.NotifyCanExecuteChanged();
        RemoveSecurityFilterCommand.NotifyCanExecuteChanged();
        ApplyWmiFilterCommand.NotifyCanExecuteChanged();
        ApplyStatusCommand.NotifyCanExecuteChanged();
        AddDelegationCommand.NotifyCanExecuteChanged();
        ChangeLevelCommand.NotifyCanExecuteChanged();
        RemoveDelegationCommand.NotifyCanExecuteChanged();
        AlignVersionsCommand.NotifyCanExecuteChanged();
        RepairFolderSecurityCommand.NotifyCanExecuteChanged();
        DeleteOrphanedFolderCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        BackupCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        HistoryCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        BuildOptions();
        base.OnCultureChanged();
    }

    // ---- Zakres -------------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasSelectedLink))]
    private void OpenLinkContainer()
    {
        if (SelectedLink is { } link)
        {
            Actions.OpenScope(link.Link.ContainerDn, link.ContainerName);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditLink))]
    private Task ToggleEnforcedAsync() => SelectedLink is { } link
        ? RunAsync(() => Actions.SetLinkOptionsAsync(link.Link.ContainerDn, GpoId, Title, !link.Enforced, null))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEditLink))]
    private Task ToggleEnabledAsync() => SelectedLink is { } link
        ? RunAsync(() => Actions.SetLinkOptionsAsync(link.Link.ContainerDn, GpoId, Title, null, !link.Enabled))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEditLink))]
    private Task UnlinkAsync() => SelectedLink is { } link
        ? RunAsync(() => Actions.UnlinkAsync(link.Link.ContainerDn, GpoId, Title))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEditSecurity))]
    private Task AddSecurityFilterAsync() => _gpo is { } gpo ? RunAsync(() => Actions.AddSecurityFilterAsync(gpo)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRemoveSecurityFilter))]
    private Task RemoveSecurityFilterAsync() => _gpo is { } gpo && SelectedFilterTrustee is { } trustee
        ? RunAsync(() => Actions.RemoveSecurityFilterAsync(gpo, trustee.Sid, trustee.Name))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanApplyWmiFilter))]
    private Task ApplyWmiFilterAsync() => _gpo is { } gpo && SelectedWmiFilter is { } option
        ? RunAsync(() => Actions.SetWmiFilterAsync(gpo, option.Value))
        : Task.CompletedTask;

    // ---- Szczegóły ----------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanApplyStatus))]
    private Task ApplyStatusAsync() => _gpo is { } gpo && SelectedStatus is { } option
        ? RunAsync(() => Actions.SetStatusAsync(gpo, option.Value))
        : Task.CompletedTask;

    // ---- Delegowanie --------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanAddDelegation))]
    private Task AddDelegationAsync() => _gpo is { } gpo && SelectedLevel is { } level
        ? RunAsync(() => Actions.AddDelegationAsync(gpo, level.Value))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanChangeLevel))]
    private Task ChangeLevelAsync() => _gpo is { } gpo && SelectedTrustee is { } trustee && SelectedLevel is { } level
        ? RunAsync(() => Actions.SetDelegationAsync(gpo, trustee.Sid, trustee.Name, level.Value))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRemoveDelegation))]
    private Task RemoveDelegationAsync() => _gpo is { } gpo && SelectedTrustee is { } trustee
        ? RunAsync(() => Actions.RemoveDelegationAsync(gpo, trustee.Sid, trustee.Name))
        : Task.CompletedTask;

    // ---- Stan ---------------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanAlign))]
    private Task AlignVersionsAsync() => _gpo is { } gpo ? RunAsync(() => Actions.AlignVersionsAsync(gpo)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private Task RepairFolderSecurityAsync() => _gpo is { } gpo ? RunAsync(() => Actions.RepairFolderSecurityAsync(gpo)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanDeleteOrphan))]
    private Task DeleteOrphanedFolderAsync() => RunAsync(() => Actions.DeleteOrphanedFolderAsync(GpoId, _initialName));

    // ---- GPO ----------------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task RenameAsync() => _gpo is { } gpo ? RunAsync(() => Actions.RenameAsync(gpo)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSysvolEdit))]
    private Task BackupAsync() => _gpo is { } gpo ? RunAsync(() => Actions.BackupAsync([gpo])) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task CompareAsync() => _gpo is { } gpo ? Actions.CompareAsync(gpo) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => _gpo is { } gpo ? RunAsync(() => Actions.DeleteAsync(gpo)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanHistory))]
    private Task HistoryAsync() => _gpo is { } gpo ? Actions.ShowHistoryAsync(gpo) : Task.CompletedTask;

    private bool CanEdit() => !IsLoading && HasSession && _gpo is not null;

    private bool CanSysvolEdit() => IsSysvolWritable && _gpo is not null;

    private bool HasSelectedLink() => SelectedLink is not null;

    private bool CanEditLink() => CanEdit() && SelectedLink is not null;

    private bool CanEditSecurity() => CanSysvolEdit();

    private bool CanRemoveSecurityFilter() => CanSysvolEdit() && SelectedFilterTrustee is not null;

    private bool CanApplyWmiFilter() => CanEdit() && SelectedWmiFilter is { } option
        && !string.Equals(option.Value?.Id, _gpo!.WmiFilterId, StringComparison.OrdinalIgnoreCase)
        && (option.Value is not null || _gpo.WmiFilterId is not null);

    private bool CanApplyStatus() => CanEdit() && SelectedStatus is { } option && option.Value != _gpo!.Status;

    private bool CanAddDelegation() => CanSysvolEdit() && SelectedLevel is not null;

    private bool CanChangeLevel() => CanSysvolEdit() && SelectedTrustee is { Inherited: false } trustee && SelectedLevel is { } level && level.Value != trustee.Level;

    private bool CanRemoveDelegation() => CanSysvolEdit() && SelectedTrustee is { Inherited: false };

    private bool CanAlign() => CanSysvolEdit() && HasVersionMismatch;

    private bool CanRepair() => CanSysvolEdit() && HasFolderAclMismatch;

    private bool CanDeleteOrphan() => IsMissing && HasOrphanedFolder && IsSysvolWritable;

    private bool CanDelete() => CanSysvolEdit() && _gpo is { IsDefault: false };

    private bool CanHistory() => _gpo is not null && Actions.CanShowHistory;

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    private void BuildOptions()
    {
        StatusOptions = [.. Enum.GetValues<GpoStatus>().Select(s => new GpoOption<GpoStatus>(s, GroupPolicyText.Status(Localizer, s)))];
        LevelOptions = [.. new[] { GpoPermissionLevel.Read, GpoPermissionLevel.Edit, GpoPermissionLevel.EditDeleteModifySecurity }
            .Select(l => new GpoOption<GpoPermissionLevel>(l, GroupPolicyText.Level(Localizer, l)))];
        OnPropertyChanged(nameof(StatusOptions));
        OnPropertyChanged(nameof(LevelOptions));
        SelectedLevel = LevelOptions.FirstOrDefault(o => o.Value == (SelectedLevel?.Value ?? GpoPermissionLevel.Edit));
    }

    private void Clear()
    {
        Links = [];
        SecurityFilter = [];
        WmiFilters = [];
        Settings = [];
        Delegation = [];
        Checks = [];
        HasVersionMismatch = false;
        HasFolderAclMismatch = false;
    }

    private IReadOnlyList<GpoCheckRowViewModel> BuildChecks(GpoInfo gpo, bool sysvolOpen, bool folderExists, GpoFolderContent? content, byte[]? folderSecurity)
    {
        var checks = new List<GpoCheckRowViewModel>();
        if (!sysvolOpen)
        {
            HasVersionMismatch = false;
            HasFolderAclMismatch = false;
            checks.Add(new GpoCheckRowViewModel(Localizer[L.GroupPolicy.CheckSysvol], SysvolWarning ?? string.Empty, false));
            return checks;
        }

        checks.Add(new GpoCheckRowViewModel(Localizer[L.GroupPolicy.CheckFolder],
            Localizer[folderExists ? L.GroupPolicy.CheckFolderOk : L.GroupPolicy.FolderMissing], folderExists));

        // Versions (design note 3.4): GPT.INI behind AD = SYSVOL replication pending; ahead = a failed write or AD lag.
        var directory = gpo.DirectoryVersion ?? GpoVersion.Zero;
        HasVersionMismatch = gpo.SysvolVersion is not { } sysvol || sysvol != directory;
        var versionDetail = gpo.SysvolVersion is not { } gpt
            ? Localizer[L.GroupPolicy.GptIniMissing]
            : gpt == directory ? Localizer.Format(L.GroupPolicy.CheckVersionsOk, GroupPolicyText.Version(Localizer, directory))
            : directory.IsBehind(gpt) && !gpt.IsBehind(directory) ? Localizer.Format(L.GroupPolicy.CheckVersionsAhead, GroupPolicyText.Version(Localizer, directory), GroupPolicyText.Version(Localizer, gpt))
            : Localizer.Format(L.GroupPolicy.CheckVersionsBehind, GroupPolicyText.Version(Localizer, directory), GroupPolicyText.Version(Localizer, gpt));
        checks.Add(new GpoCheckRowViewModel(Localizer[L.GroupPolicy.CheckVersions], versionDetail, !HasVersionMismatch));

        // Folder permissions against the container's DACL (the mapping samba-tool gpo / GPMC use).
        HasFolderAclMismatch = folderExists && folderSecurity is not null && !GpoFolderAcl.IsConsistent(folderSecurity, gpo.SecurityDescriptor);
        checks.Add(new GpoCheckRowViewModel(Localizer[L.GroupPolicy.CheckFolderAcl],
            Localizer[!folderExists || folderSecurity is null ? L.GroupPolicy.CheckUnknown : HasFolderAclMismatch ? L.GroupPolicy.CheckFolderAclMismatch : L.GroupPolicy.CheckFolderAclOk],
            !HasFolderAclMismatch));

        if (content is not null)
        {
            var machineFiles = content.Files.Keys.Any(k => k.StartsWith("Machine/", StringComparison.OrdinalIgnoreCase) && !IsMarker(k));
            var userFiles = content.Files.Keys.Any(k => k.StartsWith("User/", StringComparison.OrdinalIgnoreCase) && !IsMarker(k));
            var extensionsOk = machineFiles == !string.IsNullOrWhiteSpace(gpo.MachineExtensionNames) && userFiles == !string.IsNullOrWhiteSpace(gpo.UserExtensionNames);
            checks.Add(new GpoCheckRowViewModel(Localizer[L.GroupPolicy.CheckExtensions],
                Localizer[extensionsOk ? L.GroupPolicy.CheckExtensionsOk : L.GroupPolicy.CheckExtensionsMismatch], extensionsOk));
            checks.Add(new GpoCheckRowViewModel(Localizer[L.GroupPolicy.CheckLeftovers],
                content.Leftovers.Count == 0 ? Localizer[L.GroupPolicy.CheckLeftoversNone] : Localizer.Format(L.GroupPolicy.CheckLeftoversFound, content.Leftovers.Count),
                content.Leftovers.Count == 0));
        }

        return checks;
    }

    /// <summary>Files that exist in every GPO and carry no settings of a client-side extension.</summary>
    private static bool IsMarker(string path) => GpoFileClassifier.Classify(path) is { IsContent: false };

    private static string Half(int? value) => value?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "–";
}
