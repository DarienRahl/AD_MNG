using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>A linked GPO of a container ("Połączone obiekty zasad grupy").</summary>
public sealed class ScopeLinkRowViewModel
{
    public required GpLinkEntry Link { get; init; }

    /// <summary>The GPO (null: a link to a GPO that no longer exists).</summary>
    public GpoInfo? Gpo { get; init; }

    public int Order => Link.Order;

    public required string GpoName { get; init; }

    public bool Enforced => Link.Enforced;

    public bool Enabled => Link.Enabled;

    public required string EnforcedText { get; init; }

    public required string EnabledText { get; init; }

    public required string StatusText { get; init; }

    public required string WmiFilterText { get; init; }

    public required string ChangedText { get; init; }

    /// <summary>The linked GPO does not exist (deleted without its links).</summary>
    public bool IsDangling => Gpo is null;
}

/// <summary>A GPO of "Dziedziczenie zasad grupy" (precedence 1 wins).</summary>
public sealed record InheritedGpoRowViewModel(int Precedence, string GpoName, string LocationText, string EnforcedText, string EnabledText, string StatusText, string WmiFilterText);

/// <summary>
/// "Zasady grupy" of a domain head, OU or site (design note 5.2): "Połączone obiekty zasad grupy" (link order, GPO,
/// enforced, link enabled, GPO status, WMI filter, modified; Up/Down, Enforced, Link enabled, Remove link, Link an existing
/// GPO, Create and link), "Zablokuj dziedziczenie" (not for sites) and "Dziedziczenie zasad grupy" (precedence from U4c's
/// <see cref="GroupPolicyInheritance"/>; site links only for sites, as GPMC; read-only).
/// </summary>
public sealed partial class GroupPolicyScopeDocumentViewModel : GroupPolicyDocumentBase
{
    private string _name;

    public GroupPolicyScopeDocumentViewModel(GroupPolicyServices services, GroupPolicyActions actions, string containerDn, string? name)
        : base(services, actions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerDn);
        ContainerDn = containerDn;
        _name = name ?? AddressResolver.ToDisplayPath(containerDn);
    }

    public string ContainerDn { get; }

    public override string Title => Localizer.Format(L.GroupPolicy.ScopeTitle, _name);

    public override ObjectIcon Icon => Kind == GroupPolicySomKind.Site ? ObjectIcon.Site : ObjectIcon.GroupPolicy;

    /// <summary>Site, domain or OU (known after the load).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon), nameof(IsSite), nameof(CanBlockInheritance))]
    public partial GroupPolicySomKind Kind { get; private set; } = GroupPolicySomKind.OrganizationalUnit;

    public bool IsSite => Kind == GroupPolicySomKind.Site;

    /// <summary>"contoso.test/Komputery".</summary>
    [ObservableProperty]
    public partial string PathText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<ScopeLinkRowViewModel> Links { get; private set; } = [];

    [ObservableProperty]
    public partial ScopeLinkRowViewModel? SelectedLink { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<InheritedGpoRowViewModel> Inheritance { get; private set; } = [];

    /// <summary>"Zablokuj dziedziczenie" as read (gPOptions bit 1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockInheritanceText))]
    public partial bool BlocksInheritance { get; private set; }

    public bool CanBlockInheritance => !IsSite;

    public string BlockInheritanceText => Localizer[BlocksInheritance ? L.GroupPolicy.InheritanceBlocked : L.GroupPolicy.InheritanceNotBlocked];

    /// <summary>The container no longer exists.</summary>
    [ObservableProperty]
    public partial bool IsMissing { get; private set; }

    partial void OnSelectedLinkChanged(ScopeLinkRowViewModel? value) => RefreshCommands();

    protected override async Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
    {
        var reader = Gp.Reader;
        var container = await Task.Run(() => reader.ReadContainerAsync(session, ContainerDn, cancellationToken), cancellationToken).ConfigureAwait(true);
        if (container is null)
        {
            IsMissing = true;
            Links = [];
            Inheritance = [];
            Error = Localizer[L.GroupPolicy.ContainerMissing];
            return;
        }

        IsMissing = false;
        var gpos = await Task.Run(() => reader.ReadPoliciesAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        var filters = await Task.Run(() => reader.ReadWmiFiltersAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        var containers = container.Kind == GroupPolicySomKind.Site
            ? [container]
            : await Task.Run(() => reader.ReadLinkingContainersAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();

        var byId = gpos.ToDictionary(g => g.Id);
        var filterNames = filters.ToDictionary(f => f.Id, f => f.Name, StringComparer.OrdinalIgnoreCase);
        var zone = TimeZoneInfo.Local;
        string NameOf(Guid id) => byId.TryGetValue(id, out var gpo) ? gpo.DisplayName : Localizer.Format(L.GroupPolicy.DanglingLink, GroupPolicyText.Guid(id));
        string StatusOf(Guid id) => byId.TryGetValue(id, out var gpo) ? GroupPolicyText.Status(Localizer, gpo.Status) : "–";
        string FilterOf(Guid id) => byId.TryGetValue(id, out var gpo) && gpo.WmiFilterId is { } filter ? filterNames.TryGetValue(filter, out var name) ? name : filter : "–";

        Kind = container.Kind;
        if (container.Kind == GroupPolicySomKind.Domain)
        {
            _name = session.Info.DomainDnsName;
        }
        else if (!string.IsNullOrEmpty(container.Name))
        {
            _name = container.Name;
        }

        OnPropertyChanged(nameof(Title));
        PathText = container.Kind == GroupPolicySomKind.Site ? container.Name : AddressResolver.ToDisplayPath(container.Dn);
        BlocksInheritance = container.BlocksInheritance;
        var selected = SelectedLink?.Link.GpoId;
        Links = [.. container.Links.Links.OrderBy(l => l.Order).Select(l => new ScopeLinkRowViewModel
        {
            Link = l,
            Gpo = byId.GetValueOrDefault(l.GpoId),
            GpoName = NameOf(l.GpoId),
            EnforcedText = GroupPolicyText.YesNo(Localizer, l.Enforced),
            EnabledText = GroupPolicyText.YesNo(Localizer, l.Enabled),
            StatusText = StatusOf(l.GpoId),
            WmiFilterText = FilterOf(l.GpoId),
            ChangedText = byId.TryGetValue(l.GpoId, out var gpo) ? GroupPolicyText.Date(Localizer, gpo.Changed, zone) : "–",
        })];
        SelectedLink = Links.FirstOrDefault(l => l.Link.GpoId == selected);

        // Precedence: the last applied wins, so precedence 1 is the last of the application order.
        var applied = GroupPolicyReader.Inheritance(session.Info.DefaultNamingContext, containers, container);
        Inheritance = [.. applied.Reverse().Select((a, index) => new InheritedGpoRowViewModel(
            index + 1,
            NameOf(a.GpoId),
            a.Container.Kind == GroupPolicySomKind.Domain ? session.Info.DomainDnsName
                : a.Container.Kind == GroupPolicySomKind.Site ? container.Name
                : AddressResolver.ToDisplayPath(a.Container.Dn),
            GroupPolicyText.YesNo(Localizer, a.Link.Enforced),
            GroupPolicyText.YesNo(Localizer, !a.Link.Disabled),
            StatusOf(a.GpoId),
            FilterOf(a.GpoId)))];
        LoadedText = Localizer.Format(L.GroupPolicy.ScopeLoaded, Links.Count, Inheritance.Count);
    }

    protected override void OnNotConnected()
    {
        Links = [];
        Inheritance = [];
    }

    protected override void OnCultureChanged()
    {
        OnPropertyChanged(nameof(BlockInheritanceText));
        base.OnCultureChanged();
    }

    protected override void RefreshCommands()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        ToggleEnforcedCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        UnlinkCommand.NotifyCanExecuteChanged();
        LinkExistingCommand.NotifyCanExecuteChanged();
        CreateAndLinkCommand.NotifyCanExecuteChanged();
        ToggleBlockInheritanceCommand.NotifyCanExecuteChanged();
        OpenGpoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private Task MoveUpAsync() => SelectedLink is { } link
        ? RunAsync(() => Actions.MoveLinkAsync(ContainerDn, link.Link.GpoId, link.GpoName, link.Order - 1))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private Task MoveDownAsync() => SelectedLink is { } link
        ? RunAsync(() => Actions.MoveLinkAsync(ContainerDn, link.Link.GpoId, link.GpoName, link.Order + 1))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEditLink))]
    private Task ToggleEnforcedAsync() => SelectedLink is { } link
        ? RunAsync(() => Actions.SetLinkOptionsAsync(ContainerDn, link.Link.GpoId, link.GpoName, !link.Enforced, null))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEditLink))]
    private Task ToggleEnabledAsync() => SelectedLink is { } link
        ? RunAsync(() => Actions.SetLinkOptionsAsync(ContainerDn, link.Link.GpoId, link.GpoName, null, !link.Enabled))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEditLink))]
    private Task UnlinkAsync() => SelectedLink is { } link
        ? RunAsync(() => Actions.UnlinkAsync(ContainerDn, link.Link.GpoId, link.GpoName))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task LinkExistingAsync() => RunAsync(() => Actions.LinkExistingAsync(ContainerDn, _name));

    [RelayCommand(CanExecute = nameof(IsSysvolWritable))]
    private Task CreateAndLinkAsync() => RunAsync(() => Actions.NewAsync(ContainerDn, _name));

    [RelayCommand(CanExecute = nameof(CanToggleBlock))]
    private Task ToggleBlockInheritanceAsync() => RunAsync(() => Actions.SetBlockInheritanceAsync(ContainerDn, _name, !BlocksInheritance));

    [RelayCommand(CanExecute = nameof(CanOpenGpo))]
    private void OpenGpo(ScopeLinkRowViewModel? row)
    {
        if ((row ?? SelectedLink) is { Gpo: { } gpo })
        {
            Actions.OpenGpo(gpo.Id, gpo.DisplayName);
        }
    }

    private bool CanEdit() => !IsLoading && HasSession && !IsMissing;

    private bool CanEditLink() => CanEdit() && SelectedLink is not null;

    private bool CanMoveUp() => CanEditLink() && SelectedLink!.Order > 1;

    private bool CanMoveDown() => CanEditLink() && SelectedLink!.Order < Links.Count;

    private bool CanToggleBlock() => CanEdit() && !IsSite;

    private bool CanOpenGpo(ScopeLinkRowViewModel? row) => (row ?? SelectedLink) is { Gpo: not null };
}
