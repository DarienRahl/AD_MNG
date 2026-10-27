using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Navigation;

/// <summary>
/// Base of the virtual Group Policy nodes under the domain (design note docs/dev/k-computers-and-gpo.md 5.2): they are not
/// directory containers, <see cref="ContainerNodeViewModel"/> keeps them at the top when it merges its children, and
/// selecting one opens its document in the shell.
/// </summary>
public abstract partial class GroupPolicyNodeViewModel : TreeNodeViewModel
{
    private Task? _loadTask;

    private protected GroupPolicyNodeViewModel(NavigationTreeViewModel tree, TreeNodeViewModel parent, string name, ObjectIcon icon, GroupPolicyViewModelFactory groupPolicy, bool hasChildren)
        : base(tree, parent, name, icon)
    {
        Tree = tree;
        GroupPolicy = groupPolicy;
        if (hasChildren)
        {
            Children.Add(new PlaceholderNodeViewModel(this, tree.Localizer[L.Common.Loading]));
        }
    }

    public GroupPolicyViewModelFactory GroupPolicy { get; }

    /// <summary>Localized error of the last child load.</summary>
    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    public bool ChildrenLoaded { get; private set; }

    private protected NavigationTreeViewModel Tree { get; }

    public override Task EnsureChildrenLoadedAsync() => HasLazyChildren ? _loadTask ??= LoadAsync() : Task.CompletedTask;

    /// <summary>Re-reads the children (a GPO was created, renamed or deleted).</summary>
    [RelayCommand]
    public Task RefreshAsync()
    {
        if (!HasLazyChildren)
        {
            return Task.CompletedTask;
        }

        _loadTask = LoadAsync();
        return _loadTask;
    }

    /// <summary>Re-localizes the node's name.</summary>
    internal abstract void Relocalize();

    private protected virtual bool HasLazyChildren => false;

    /// <summary>Reads the children (on the window's current session).</summary>
    private protected virtual Task<IReadOnlyList<TreeNodeViewModel>> ReadChildrenAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TreeNodeViewModel>>([]);

    private async Task LoadAsync()
    {
        if (Tree.Sessions.Current is null)
        {
            return;
        }

        IsLoading = true;
        LoadError = null;
        try
        {
            var children = await ReadChildrenAsync(GroupPolicy.Services.Lifetime).ConfigureAwait(true);
            await Tree.Dispatcher.RunAsync(() =>
            {
                Children.ReplaceAll(children);
                ChildrenLoaded = true;
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            _loadTask = null;
            await Tree.Dispatcher.RunAsync(() =>
            {
                LoadError = GroupPolicyText.Error(Tree.Localizer, Tree.Errors, ex);
                Children.ReplaceAll([]);
                ChildrenLoaded = false;
            }).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>"Obiekty zasad grupy": the GPOs of the domain, sorted by name, loaded when expanded.</summary>
public sealed class GroupPolicyObjectsNodeViewModel : GroupPolicyNodeViewModel
{
    internal GroupPolicyObjectsNodeViewModel(NavigationTreeViewModel tree, TreeNodeViewModel parent, GroupPolicyViewModelFactory groupPolicy)
        : base(tree, parent, tree.Localizer[L.GroupPolicy.ObjectsTitle], ObjectIcon.GroupPolicy, groupPolicy, hasChildren: true)
    {
    }

    public override string? HelpText => Tree.Localizer[L.GroupPolicy.ObjectsNodeHelp];

    internal override void Relocalize() => Name = Tree.Localizer[L.GroupPolicy.ObjectsTitle];

    private protected override bool HasLazyChildren => true;

    private protected override async Task<IReadOnlyList<TreeNodeViewModel>> ReadChildrenAsync(CancellationToken cancellationToken)
    {
        var session = Tree.Sessions.Current;
        if (session is null)
        {
            return [];
        }

        var reader = GroupPolicy.Services.Reader;
        var gpos = await Task.Run(() => reader.ReadPoliciesAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        var comparer = StringComparer.Create(Tree.Localizer.Culture, ignoreCase: true);
        var existing = Children.OfType<GpoNodeViewModel>().ToDictionary(n => n.GpoId);
        return [.. gpos.OrderBy(g => g.DisplayName, comparer).Select(g =>
        {
            if (existing.TryGetValue(g.Id, out var node))
            {
                node.Update(g);
                return (TreeNodeViewModel)node;
            }

            return new GpoNodeViewModel(Tree, this, GroupPolicy, g);
        })];
    }
}

/// <summary>A GPO under "Obiekty zasad grupy" (its menu: Stan GPO, Kopia zapasowa…, Kopiuj, Zmień nazwę, Usuń, Porównaj z…, Historia).</summary>
public sealed class GpoNodeViewModel : GroupPolicyNodeViewModel
{
    internal GpoNodeViewModel(NavigationTreeViewModel tree, TreeNodeViewModel parent, GroupPolicyViewModelFactory groupPolicy, GpoInfo gpo)
        : base(tree, parent, gpo.DisplayName, ObjectIcon.GroupPolicy, groupPolicy, hasChildren: false)
    {
        Gpo = gpo;
    }

    /// <summary>The GPO as read with the parent's children.</summary>
    public GpoInfo Gpo { get; private set; }

    public Guid GpoId => Gpo.Id;

    public override string? HelpText => GroupPolicyText.Status(Tree.Localizer, Gpo.Status);

    internal void Update(GpoInfo gpo)
    {
        Gpo = gpo;
        Name = gpo.DisplayName;
        OnPropertyChanged(nameof(HelpText));
    }

    internal override void Relocalize() => OnPropertyChanged(nameof(HelpText));
}

/// <summary>"Filtry WMI": selecting it opens the WMI filters document.</summary>
public sealed class WmiFiltersNodeViewModel : GroupPolicyNodeViewModel
{
    internal WmiFiltersNodeViewModel(NavigationTreeViewModel tree, TreeNodeViewModel parent, GroupPolicyViewModelFactory groupPolicy)
        : base(tree, parent, tree.Localizer[L.GroupPolicy.WmiFiltersTitle], ObjectIcon.WmiFilter, groupPolicy, hasChildren: false)
    {
    }

    internal override void Relocalize() => Name = Tree.Localizer[L.GroupPolicy.WmiFiltersTitle];
}

/// <summary>"Lokacje": the sites of the forest (their "Zasady grupy" links), loaded when expanded.</summary>
public sealed class SitesNodeViewModel : GroupPolicyNodeViewModel
{
    internal SitesNodeViewModel(NavigationTreeViewModel tree, TreeNodeViewModel parent, GroupPolicyViewModelFactory groupPolicy)
        : base(tree, parent, tree.Localizer[L.GroupPolicy.SitesTitle], ObjectIcon.Site, groupPolicy, hasChildren: true)
    {
    }

    internal override void Relocalize() => Name = Tree.Localizer[L.GroupPolicy.SitesTitle];

    private protected override bool HasLazyChildren => true;

    private protected override async Task<IReadOnlyList<TreeNodeViewModel>> ReadChildrenAsync(CancellationToken cancellationToken)
    {
        var session = Tree.Sessions.Current;
        if (session is null)
        {
            return [];
        }

        var reader = GroupPolicy.Services.Reader;
        var sites = await Task.Run(() => reader.ReadSitesAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        return [.. sites.Select(s => (TreeNodeViewModel)new SiteNodeViewModel(Tree, this, GroupPolicy, s.Dn, s.Name))];
    }
}

/// <summary>A site: selecting it opens its "Zasady grupy" (linked GPOs, no block inheritance).</summary>
public sealed class SiteNodeViewModel : GroupPolicyNodeViewModel
{
    internal SiteNodeViewModel(NavigationTreeViewModel tree, TreeNodeViewModel parent, GroupPolicyViewModelFactory groupPolicy, string distinguishedName, string name)
        : base(tree, parent, name, ObjectIcon.Site, groupPolicy, hasChildren: false)
    {
        Dn = distinguishedName;
    }

    public string Dn { get; }

    internal override void Relocalize()
    {
    }
}
