using System.Collections.ObjectModel;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Views;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Navigation;

/// <summary>
/// Left pane of the main window: a "Views" section (built-in quick filters, saved views, pinned containers) and the
/// domain tree with lazily loaded containers (ADUC console tree).
/// </summary>
public sealed partial class NavigationTreeViewModel : ObservableObject, IDisposable
{
    private readonly ViewLibrary _views;
    private readonly IDirectoryChangeNotifier _changes;
    private readonly Dictionary<string, ContainerNodeViewModel> _index = new(StringComparer.Ordinal);
    private bool _suppressActivation;
    private int _viewsInactiveDays;

    public NavigationTreeViewModel(
        ISessionManager sessions,
        ISettingsStore settings,
        ILocalizer localizer,
        IUiDispatcher dispatcher,
        ErrorTranslator errors,
        ViewLibrary views,
        IDirectoryChangeNotifier changes)
    {
        Sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _views = views ?? throw new ArgumentNullException(nameof(views));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        ViewsNode = new FolderNodeViewModel(this, null, localizer[L.Tree.Views], ObjectIcon.Query) { IsExpanded = true };
        _views.Changed += OnViewsChanged;
        _changes.Changed += OnDirectoryChanged;
        Localizer.CultureChanged += OnCultureChanged;
        Settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised when the operator selects a node (not for programmatic selection via <see cref="ExpandToAsync"/>).</summary>
    public event EventHandler<TreeNodeViewModel>? NodeActivated;

    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];

    /// <summary>"Views" root node.</summary>
    public FolderNodeViewModel ViewsNode { get; }

    /// <summary>The domain root node (null while disconnected).</summary>
    [ObservableProperty]
    public partial ContainerNodeViewModel? DomainNode { get; private set; }

    [ObservableProperty]
    public partial TreeNodeViewModel? SelectedNode { get; private set; }

    internal ISessionManager Sessions { get; }

    internal ISettingsStore Settings { get; }

    internal ILocalizer Localizer { get; }

    internal IUiDispatcher Dispatcher { get; }

    internal ErrorTranslator Errors { get; }

    /// <summary>(Re)builds the tree for the current session and expands the domain node.</summary>
    public Task InitializeAsync()
    {
        _index.Clear();
        Roots.Clear();
        DomainNode = null;
        SelectedNode = null;
        RebuildViews();
        Roots.Add(ViewsNode);
        var session = Sessions.Current;
        if (session is null)
        {
            return Task.CompletedTask;
        }

        var domain = new ContainerNodeViewModel(this, null, session.Info.DefaultNamingContext, session.Info.DomainDnsName, ObjectKind.Domain);
        Register(domain);
        DomainNode = domain;
        Roots.Add(domain);
        domain.IsExpanded = true;
        return domain.EnsureChildrenLoadedAsync();
    }

    /// <summary>Rebuilds the Views section (after views, pins, language or inactive days changed).</summary>
    public void RebuildViews()
    {
        var inactiveDays = Settings.Current.InactiveDays;
        _viewsInactiveDays = inactiveDays;
        var selected = SelectedNode;
        var quick = new FolderNodeViewModel(this, ViewsNode, Localizer[L.Tree.QuickFilters], ObjectIcon.QuickFilter);
        quick.Children.ReplaceAll(BuiltInViews.All.Select(v => new ViewNodeViewModel(this, quick, v, v.Title(Localizer, inactiveDays))));
        var nodes = new List<TreeNodeViewModel> { quick };
        nodes.AddRange(_views.SavedViews.Select(v => new ViewNodeViewModel(this, ViewsNode, v)));
        nodes.AddRange(_views.PinnedContainers.Select(p =>
            new ContainerNodeViewModel(this, ViewsNode, p.DistinguishedName, p.Name, KindFromDn(p.DistinguishedName), isPinnedEntry: true)
            {
                Icon = ObjectIcon.Pinned,
            }));
        var wasExpanded = ViewsNode.Children.OfType<FolderNodeViewModel>().FirstOrDefault()?.IsExpanded ?? false;
        quick.IsExpanded = wasExpanded;
        ViewsNode.Children.ReplaceAll(nodes);

        // Keep the selection on the rebuilt counterpart of a selected view / pinned container (no re-navigation).
        TreeNodeViewModel? replacement = selected switch
        {
            ViewNodeViewModel view => nodes.SelectMany(n => n is FolderNodeViewModel f ? f.Children : [n])
                .OfType<ViewNodeViewModel>().FirstOrDefault(n => string.Equals(n.ViewId, view.ViewId, StringComparison.Ordinal)),
            ContainerNodeViewModel { IsPinnedEntry: true } pin => nodes.OfType<ContainerNodeViewModel>()
                .FirstOrDefault(n => DistinguishedName.AreEqual(n.Dn, pin.Dn)),
            _ => null,
        };
        if (replacement is not null)
        {
            SelectSilently(replacement);
        }
    }

    /// <summary>
    /// Expands the domain tree down to <paramref name="distinguishedName"/> (loading levels as needed) and optionally
    /// selects it without raising <see cref="NodeActivated"/>. Returns the deepest node reached (null outside the domain).
    /// </summary>
    public async Task<ContainerNodeViewModel?> ExpandToAsync(string distinguishedName, bool select = true)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        var domain = DomainNode;
        if (domain is null
            || !DistinguishedName.TryParse(distinguishedName, out var target)
            || !DistinguishedName.TryParse(domain.Dn, out var root)
            || !(target.Equals(root) || target.IsDescendantOf(root)))
        {
            return null;
        }

        var chain = new List<DistinguishedName>();
        for (var dn = target; dn is not null && !dn.Equals(root); dn = dn.Parent)
        {
            chain.Insert(0, dn);
        }

        var node = domain;
        foreach (var step in chain)
        {
            node.IsExpanded = true;
            await node.EnsureChildrenLoadedAsync().ConfigureAwait(true);
            var child = node.Children.OfType<ContainerNodeViewModel>()
                .FirstOrDefault(c => DistinguishedName.TryParse(c.Dn, out var cdn) && cdn.Equals(step));
            if (child is null)
            {
                break;
            }

            node = child;
        }

        if (select)
        {
            SelectSilently(node);
        }

        return node;
    }

    /// <summary>Selects a node without raising <see cref="NodeActivated"/> (tree sync after list navigation).</summary>
    public void SelectSilently(TreeNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _suppressActivation = true;
        try
        {
            if (SelectedNode is not null && !ReferenceEquals(SelectedNode, node))
            {
                SelectedNode.IsSelected = false;
            }

            node.IsSelected = true;
            SelectedNode = node;
        }
        finally
        {
            _suppressActivation = false;
        }
    }

    /// <summary>Reloads every loaded container (e.g. after toggling Advanced Features).</summary>
    public async Task ReloadAsync()
    {
        var loaded = _index.Values.Where(n => n.ChildrenLoaded).ToList();
        foreach (var node in loaded)
        {
            await node.RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>A loaded domain tree node by DN.</summary>
    public ContainerNodeViewModel? FindNode(string distinguishedName) =>
        _index.TryGetValue(DnKey.For(distinguishedName), out var node) ? node : null;

    public void Dispose()
    {
        _views.Changed -= OnViewsChanged;
        _changes.Changed -= OnDirectoryChanged;
        Localizer.CultureChanged -= OnCultureChanged;
        Settings.Changed -= OnSettingsChanged;
    }

    internal void Register(ContainerNodeViewModel node) => _index[DnKey.For(node.Dn)] = node;

    internal void Unregister(ContainerNodeViewModel node)
    {
        // Only the registered instance: children of pinned shortcuts share DNs with domain tree nodes.
        var key = DnKey.For(node.Dn);
        if (_index.TryGetValue(key, out var registered) && ReferenceEquals(registered, node))
        {
            _index.Remove(key);
        }

        foreach (var child in node.Children.OfType<ContainerNodeViewModel>())
        {
            Unregister(child);
        }
    }

    internal void OnNodeSelected(TreeNodeViewModel node)
    {
        if (node is PlaceholderNodeViewModel)
        {
            return;
        }

        if (SelectedNode is not null && !ReferenceEquals(SelectedNode, node))
        {
            SelectedNode.IsSelected = false;
        }

        SelectedNode = node;
        if (!_suppressActivation)
        {
            NodeActivated?.Invoke(this, node);
        }
    }

    private static ObjectKind KindFromDn(string dn) =>
        DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot
            ? parsed.Leaf.Type.ToUpperInvariant() switch
            {
                "OU" => ObjectKind.OrganizationalUnit,
                "DC" => ObjectKind.Domain,
                _ => ObjectKind.Container,
            }
            : ObjectKind.Container;

    private void OnViewsChanged(object? sender, EventArgs e) => _ = Dispatcher.RunAsync(RebuildViews);

    /// <summary>The "Inactive users (N days)" titles follow the InactiveDays setting.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e) => _ = Dispatcher.RunAsync(() =>
    {
        if (Settings.Current.InactiveDays != _viewsInactiveDays)
        {
            RebuildViews();
        }
    });

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        ViewsNode.Name = Localizer[L.Tree.Views];
        RebuildViews();
    }

    private void OnDirectoryChanged(object? sender, DirectoryChangesEventArgs e) => _ = Dispatcher.RunAsync(() =>
    {
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in e.Changes)
        {
            if (change.Kind == DirectoryChangeKind.Modified && !change.ObjectKind.IsContainer())
            {
                continue;
            }

            if (change.ObjectKind is not ObjectKind.Unknown && !change.ObjectKind.IsContainer())
            {
                continue;
            }

            foreach (var parent in new[] { change.OldParentDn, change.NewParentDn })
            {
                if (parent is not null && FindNode(parent) is { ChildrenLoaded: true } && parents.Add(DnKey.For(parent)))
                {
                    FindNode(parent)!.RefreshAsync().Forget();
                }
            }
        }
    });
}
