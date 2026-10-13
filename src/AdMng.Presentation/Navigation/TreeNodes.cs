using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Navigation;

/// <summary>Base of all navigation tree nodes (bind TreeViewItem.IsExpanded/IsSelected two-way).</summary>
public abstract partial class TreeNodeViewModel : ObservableObject
{
    protected TreeNodeViewModel(NavigationTreeViewModel? owner, TreeNodeViewModel? parent, string name, ObjectIcon icon)
    {
        Owner = owner;
        Parent = parent;
        Name = name;
        Icon = icon;
    }

    public NavigationTreeViewModel? Owner { get; }

    public TreeNodeViewModel? Parent { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial ObjectIcon Icon { get; set; }

    public BulkObservableCollection<TreeNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; protected set; }

    /// <summary>Loads children if the node has lazy children (no-op otherwise).</summary>
    public virtual Task EnsureChildrenLoadedAsync() => Task.CompletedTask;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureChildrenLoadedAsync().Forget();
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            Owner?.OnNodeSelected(this);
        }
    }
}

/// <summary>"Loading…" child shown until a container is expanded (gives the node an expander).</summary>
public sealed class PlaceholderNodeViewModel(TreeNodeViewModel parent, string text)
    : TreeNodeViewModel(null, parent, text, ObjectIcon.Unknown);

/// <summary>A grouping node: "Views", "Quick filters".</summary>
public sealed class FolderNodeViewModel(NavigationTreeViewModel? owner, TreeNodeViewModel? parent, string name, ObjectIcon icon = ObjectIcon.Folder)
    : TreeNodeViewModel(owner, parent, name, icon);

/// <summary>A built-in or saved view; selecting it opens the view in the current tab.</summary>
public sealed class ViewNodeViewModel : TreeNodeViewModel
{
    internal ViewNodeViewModel(NavigationTreeViewModel owner, TreeNodeViewModel parent, BuiltInViewDefinition builtIn, string title)
        : base(owner, parent, title, ObjectIcon.QuickFilter)
    {
        BuiltIn = builtIn;
        ViewId = builtIn.Id;
    }

    internal ViewNodeViewModel(NavigationTreeViewModel owner, TreeNodeViewModel parent, SavedView view)
        : base(owner, parent, view.Name, view.Icon)
    {
        SavedView = view;
        ViewId = view.Id;
    }

    public string ViewId { get; }

    public BuiltInViewDefinition? BuiltIn { get; }

    public SavedView? SavedView { get; }

    public bool IsBuiltIn => BuiltIn is not null;
}

/// <summary>
/// A container (domain, OU, container, Builtin...) with lazily loaded child containers. Children are read with a
/// one-level search when the node is expanded and merged on refresh (expanded sub-trees survive).
/// </summary>
public sealed partial class ContainerNodeViewModel : TreeNodeViewModel
{
    /// <summary>Attributes read for child containers.</summary>
    public static IReadOnlyList<string> ChildAttributes { get; } = [Attr.Name, Attr.ObjectClass, Attr.ShowInAdvancedViewOnly, Attr.Description];

    private readonly NavigationTreeViewModel _tree;
    private Task? _loadTask;

    internal ContainerNodeViewModel(
        NavigationTreeViewModel tree,
        TreeNodeViewModel? parent,
        string distinguishedName,
        string name,
        ObjectKind kind,
        string? description = null,
        bool isPinnedEntry = false)
        : base(tree, parent, name, ObjectIcons.For(kind, false, false))
    {
        _tree = tree;
        Dn = distinguishedName;
        Kind = kind;
        Description = description;
        IsPinnedEntry = isPinnedEntry;
        Children.Add(new PlaceholderNodeViewModel(this, tree.Localizer[L.Common.Loading]));
    }

    public string Dn { get; }

    public ObjectKind Kind { get; private set; }

    [ObservableProperty]
    public partial string? Description { get; set; }

    /// <summary>True for nodes under "Views > pinned containers" (shortcuts, not part of the domain tree index).</summary>
    public bool IsPinnedEntry { get; }

    public bool ChildrenLoaded { get; private set; }

    /// <summary>Localized error of the last child load (e.g. access denied).</summary>
    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    public override Task EnsureChildrenLoadedAsync() => _loadTask ??= LoadChildrenAsync();

    /// <summary>Re-reads the child containers (merge, keeps expanded sub-trees).</summary>
    [RelayCommand]
    public Task RefreshAsync()
    {
        _loadTask = LoadChildrenAsync();
        return _loadTask;
    }

    internal void Update(string name, ObjectKind kind, string? description)
    {
        Name = name;
        Kind = kind;
        Icon = ObjectIcons.For(kind, false, false);
        Description = description;
    }

    private async Task LoadChildrenAsync()
    {
        var session = _tree.Sessions.Current;
        if (session is null)
        {
            return;
        }

        IsLoading = true;
        LoadError = null;
        try
        {
            var search = new DirectorySearch
            {
                BaseDn = Dn,
                Scope = SearchScope.OneLevel,
                Filter = ObjectKinds.ContainerFilter,
                Attributes = ChildAttributes,
                PageSize = 1000,
            };
            var children = await Task.Run(async () =>
            {
                var list = new List<DirectoryObject>();
                await foreach (var obj in session.SearchAsync(search, CancellationToken.None).ConfigureAwait(false))
                {
                    list.Add(obj);
                }

                return list;
            }).ConfigureAwait(true);

            var advanced = _tree.Settings.Current.ShowAdvancedFeatures;
            var comparer = StringComparer.Create(_tree.Localizer.Culture, ignoreCase: true);
            var visible = children
                .Where(o => advanced || o.GetBoolean(Attr.ShowInAdvancedViewOnly) != true)
                .OrderBy(o => o.Name, comparer)
                .ToList();
            await _tree.Dispatcher.RunAsync(() => MergeChildren(visible)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _loadTask = null;
            await _tree.Dispatcher.RunAsync(() =>
            {
                LoadError = _tree.Errors.Translate(ex);
                foreach (var child in Children.OfType<ContainerNodeViewModel>())
                {
                    // Detached nodes must not stay findable (FindNode / locate / change refresh would use them).
                    _tree.Unregister(child);
                }

                Children.ReplaceAll([]);
                ChildrenLoaded = false;
            }).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void MergeChildren(IReadOnlyList<DirectoryObject> objects)
    {
        var existing = Children.OfType<ContainerNodeViewModel>().ToDictionary(c => DnKey.For(c.Dn), StringComparer.Ordinal);
        var next = new List<TreeNodeViewModel>(objects.Count);
        foreach (var obj in objects)
        {
            var key = DnKey.For(obj.DistinguishedName);
            if (existing.Remove(key, out var node))
            {
                node.Update(obj.Name, obj.Kind, obj.GetString(Attr.Description));
            }
            else
            {
                node = new ContainerNodeViewModel(_tree, this, obj.DistinguishedName, obj.Name, obj.Kind, obj.GetString(Attr.Description), IsPinnedEntry);
                if (!IsPinnedEntry)
                {
                    _tree.Register(node);
                }
            }

            next.Add(node);
        }

        foreach (var removed in existing.Values)
        {
            _tree.Unregister(removed);
        }

        Children.ReplaceAll(next);
        ChildrenLoaded = true;
    }
}
