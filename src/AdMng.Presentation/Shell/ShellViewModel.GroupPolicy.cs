using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Ldap;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// Group Policy in the main window (K2, design note docs/dev/k-computers-and-gpo.md 5.2): the documents "Obiekty zasad
/// grupy", one GPO, "Zasady grupy" of a domain, OU or site and "Filtry WMI" open as tabs (one per target; an open one is
/// selected), the Group Policy nodes of the tree open them, "Zasady grupy" for the current container (object list
/// toolbar and menu, palette <c>gpo.containerPolicies</c>) and "Widok → Zasady grupy w drzewie". Available when the
/// window's tree has the Group Policy services (<see cref="NavigationTreeViewModel.GroupPolicy"/>).
/// </summary>
public sealed partial class ShellViewModel
{
    private IRelayCommand[]? _groupPolicyStateCommands;

    /// <summary>The Group Policy documents can be opened in this window.</summary>
    public bool IsGroupPolicyAvailable => Tree.GroupPolicy is not null;

    /// <summary>"Widok → Zasady grupy w drzewie" (shared setting, default on).</summary>
    public bool IsGroupPolicyNodesShown => Settings.Current.GroupPolicy.ShowInTree;

    /// <summary>The open Group Policy tabs.</summary>
    public IEnumerable<GroupPolicyDocumentBase> GroupPolicyDocuments => Documents.OfType<GroupPolicyDocumentBase>();

    private IRelayCommand[] GroupPolicyStateCommands => _groupPolicyStateCommands ??=
    [
        OpenGroupPolicyObjectsCommand, OpenWmiFiltersCommand, OpenContainerPoliciesCommand, NewLinkedGpoCommand, LinkExistingGpoCommand,
        ToggleBlockInheritanceCommand,
    ];

    /// <summary>
    /// Opens the Group Policy document a request names, or selects it when it is open (a GPO document switches to the
    /// requested tab). Null when the window has no Group Policy services.
    /// </summary>
    public GroupPolicyDocumentBase? OpenGroupPolicyDocument(GroupPolicyDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Tree.GroupPolicy is not { } factory || _disposed)
        {
            return null;
        }

        var document = GroupPolicyDocuments.FirstOrDefault(d => GroupPolicyViewModelFactory.Matches(d, request));
        if (document is null)
        {
            document = factory.Create(request);
            Documents.Add(document);
            document.LoadAsync().Forget();
        }
        else if (request.Tab is { } tab && document is GroupPolicyObjectDocumentViewModel gpo)
        {
            gpo.SelectedTab = tab;
        }

        SelectedTab = document;
        return document;
    }

    private IEnumerable<CommandDescriptor> GroupPolicyCommandDescriptors()
    {
        const string tools = L.CommandCategories.Tools;
        const string obj = L.CommandCategories.Object;
        const string view = L.CommandCategories.View;
        return
        [
            new("gpo.objects", L.GroupPolicy.ObjectsCommand, tools, string.Empty, OpenGroupPolicyObjectsCommand),
            new("gpo.wmiFilters", L.GroupPolicy.WmiFiltersCommand, tools, string.Empty, OpenWmiFiltersCommand),
            new("gpo.containerPolicies", L.GroupPolicy.ContainerPoliciesCommand, obj, string.Empty, OpenContainerPoliciesCommand),
            new("gpo.newLinked", L.GroupPolicy.NewLinkedCommand, obj, string.Empty, NewLinkedGpoCommand),
            new("gpo.linkExisting", L.GroupPolicy.LinkExistingCommand, obj, string.Empty, LinkExistingGpoCommand),
            new("gpo.blockInheritance", L.GroupPolicy.BlockInheritanceCommand, obj, string.Empty, ToggleBlockInheritanceCommand),
            new("view.groupPolicyNodes", L.GroupPolicy.ToggleTreeCommand, view, string.Empty, ToggleGroupPolicyNodesCommand),
        ];
    }

    /// <summary>Subscribes to the document requests of the window's Group Policy operations (constructor).</summary>
    private void AttachGroupPolicy()
    {
        if (Tree.GroupPolicy is { } factory)
        {
            factory.Services.DocumentRequested += OnGroupPolicyDocumentRequested;
        }
    }

    /// <summary>Ends the subscription (Dispose).</summary>
    private void DetachGroupPolicy()
    {
        if (Tree.GroupPolicy is { } factory)
        {
            factory.Services.DocumentRequested -= OnGroupPolicyDocumentRequested;
        }
    }

    /// <summary>A Group Policy node was selected in the tree: its document opens (true when the node was one of them).</summary>
    private bool TryActivateGroupPolicyNode(TreeNodeViewModel node)
    {
        GroupPolicyDocumentRequest? request = node switch
        {
            GroupPolicyObjectsNodeViewModel => new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects),
            GpoNodeViewModel gpo => new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Object) { GpoId = gpo.GpoId, Name = gpo.Name },
            WmiFiltersNodeViewModel => new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.WmiFilters),
            SiteNodeViewModel site => new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Scope) { ContainerDn = site.Dn, Name = site.Name },
            _ => null,
        };
        if (request is not null)
        {
            OpenGroupPolicyDocument(request);
        }

        return node is GroupPolicyNodeViewModel;
    }

    private void OnGroupPolicyDocumentRequested(object? sender, GroupPolicyDocumentRequest request) =>
        _ = _lists.Dispatcher.RunAsync(() => OpenGroupPolicyDocument(request));

    [RelayCommand(CanExecute = nameof(CanUseGroupPolicy))]
    private void OpenGroupPolicyObjects() => OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects));

    [RelayCommand(CanExecute = nameof(CanUseGroupPolicy))]
    private void OpenWmiFilters() => OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.WmiFilters));

    /// <summary>"Zasady grupy" of the domain, OU or site given as parameter (DN), else of the current container.</summary>
    [RelayCommand(CanExecute = nameof(CanTargetContainer))]
    private void OpenContainerPolicies(object? parameter)
    {
        if (GroupPolicyTarget(parameter) is { } target)
        {
            OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Scope) { ContainerDn = target.Dn, Name = target.Name });
        }
    }

    /// <summary>"Utwórz GPO w tej domenie i połącz je tutaj…".</summary>
    [RelayCommand(CanExecute = nameof(CanTargetContainer))]
    private Task NewLinkedGpoAsync(object? parameter) => GroupPolicyTarget(parameter) is { } target && Tree.GroupPolicy is { } factory
        ? factory.Actions.NewAsync(target.Dn, target.Name)
        : Task.CompletedTask;

    /// <summary>"Połącz istniejący GPO…".</summary>
    [RelayCommand(CanExecute = nameof(CanTargetContainer))]
    private Task LinkExistingGpoAsync(object? parameter) => GroupPolicyTarget(parameter) is { } target && Tree.GroupPolicy is { } factory
        ? factory.Actions.LinkExistingAsync(target.Dn, target.Name)
        : Task.CompletedTask;

    /// <summary>"Zablokuj dziedziczenie" of a domain head or OU: switches the current state (read now) through the preview.</summary>
    [RelayCommand(CanExecute = nameof(CanBlockInheritance))]
    private async Task ToggleBlockInheritanceAsync(object? parameter)
    {
        if (GroupPolicyTarget(parameter) is not { IsSite: false } target || Tree.GroupPolicy is not { } factory || Sessions.Current is not { } session)
        {
            return;
        }

        GroupPolicyContainerInfo? container;
        try
        {
            container = await Task.Run(() => factory.Services.Reader.ReadContainerAsync(session, target.Dn, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (DirectoryException ex)
        {
            await _dialogs.ShowErrorAsync(Localizer[L.GroupPolicy.BlockInheritanceCommand], Errors.Translate(ex)).ConfigureAwait(true);
            return;
        }

        if (container is not null)
        {
            await factory.Actions.SetBlockInheritanceAsync(container.Dn, target.Name, !container.BlocksInheritance).ConfigureAwait(true);
        }
    }

    /// <summary>"Widok → Zasady grupy w drzewie".</summary>
    [RelayCommand(CanExecute = nameof(CanUseGroupPolicyAlways))]
    private async Task ToggleGroupPolicyNodesAsync()
    {
        await Settings.UpdateAsync(s => s.GroupPolicy.ShowInTree = !s.GroupPolicy.ShowInTree).ConfigureAwait(true);
        OnPropertyChanged(nameof(IsGroupPolicyNodesShown));
    }

    private bool CanUseGroupPolicy() => Tree.GroupPolicy is not null && Sessions.Current is not null;

    private bool CanUseGroupPolicyAlways() => Tree.GroupPolicy is not null;

    private bool CanTargetContainer(object? parameter) => CanUseGroupPolicy() && GroupPolicyTarget(parameter) is not null;

    private bool CanBlockInheritance(object? parameter) => CanUseGroupPolicy() && GroupPolicyTarget(parameter) is { IsSite: false };

    /// <summary>
    /// The container a Group Policy command acts on: the parameter (a DN, a container or site node), else the current list's
    /// container, else the selected tree container - when it is the domain head, an OU or a site.
    /// </summary>
    private GroupPolicyTargetInfo? GroupPolicyTarget(object? parameter)
    {
        if (Sessions.Current is not { } session)
        {
            return null;
        }

        var (dn, name) = parameter switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => (text, (string?)null),
            ContainerNodeViewModel node => (node.Dn, node.Name),
            SiteNodeViewModel site => (site.Dn, site.Name),
            ObjectRowViewModel row when row.IsContainer => (row.Dn, row.Name),
            _ => SelectedDocument?.Location is { Kind: ListLocationKind.Container } location
                ? (location.BaseDn, location.Title)
                : Tree.SelectedNode is ContainerNodeViewModel selected ? (selected.Dn, selected.Name) : ((string?)null, (string?)null),
        };
        if (dn is null || !DistinguishedName.TryParse(dn, out var parsed) || parsed.IsRoot)
        {
            return null;
        }

        if (DistinguishedName.AreEqual(dn, session.Info.DefaultNamingContext))
        {
            return new GroupPolicyTargetInfo(dn, session.Info.DomainDnsName, false);
        }

        if (string.Equals(parsed.Leaf.Type, "OU", StringComparison.OrdinalIgnoreCase)
            && (parsed.IsDescendantOf(DistinguishedName.Parse(session.Info.DefaultNamingContext))))
        {
            return new GroupPolicyTargetInfo(dn, name ?? parsed.Name, false);
        }

        return parsed.Parent is { } parent && DistinguishedName.AreEqual(parent.ToString(), GroupPolicyReader.SitesDn(session))
            ? new GroupPolicyTargetInfo(dn, name ?? parsed.Name, true)
            : null;
    }

    /// <summary>A domain head, OU or site a Group Policy command acts on.</summary>
    private sealed record GroupPolicyTargetInfo(string Dn, string Name, bool IsSite);
}
