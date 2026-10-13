using System.Collections;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PropertySheets;

/// <summary>Pending state of a membership in the Member Of / Members lists.</summary>
public enum MembershipState
{
    Unchanged,
    PendingAdd,
    PendingRemove,
}

/// <summary>One group (Member Of) or member (Members) with its pending state.</summary>
public sealed partial class MembershipItemViewModel : ObservableObject
{
    internal MembershipItemViewModel(DirectoryItem item, MembershipState state, int? groupType = null, uint? rid = null)
    {
        Item = item;
        State = state;
        GroupType = groupType;
        Rid = rid;
    }

    public DirectoryItem Item { get; private set; }

    public string Dn => Item.Dn;

    public string Name => Item.Name;

    public string Path => Item.Path;

    public ObjectIcon Icon => Item.Icon;

    public string TypeText => Item.TypeText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPendingAdd), nameof(IsPendingRemove))]
    public partial MembershipState State { get; internal set; }

    public bool IsPendingAdd => State == MembershipState.PendingAdd;

    public bool IsPendingRemove => State == MembershipState.PendingRemove;

    /// <summary>The primary group of the account (Member Of page).</summary>
    [ObservableProperty]
    public partial bool IsPrimary { get; internal set; }

    /// <summary>Localized state ("Will be added", "Will be removed", "Primary group") or empty.</summary>
    [ObservableProperty]
    public partial string StatusText { get; internal set; } = string.Empty;

    internal int? GroupType { get; private set; }

    internal uint? Rid { get; private set; }

    internal void Resolve(DirectoryItem item, int? groupType, uint? rid)
    {
        Item = item;
        GroupType = groupType;
        Rid = rid;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// Shared logic of the Member Of and Members pages: list with filter, staged additions/removals (applied with the
/// other changes of the sheet), names resolved in batches, and a lazily loaded "All (nested)" list.
/// </summary>
public abstract partial class MembershipPageViewModel : PropertyPageViewModel
{
    private readonly List<MembershipItemViewModel> _all = [];
    private readonly SerialCancellation _nestedLoads = new();
    private IList? _selectedItems;

    private protected MembershipPageViewModel(FieldContext context, string key, string title)
        : base(context, key, title)
    {
    }

    /// <summary>Visible (filtered) items, sorted by name.</summary>
    public BulkObservableCollection<MembershipItemViewModel> Items { get; } = [];

    /// <summary>Case- and diacritics-insensitive filter on name and folder.</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(OpenCommand))]
    public partial MembershipItemViewModel? SelectedItem { get; set; }

    /// <summary>The list's selected items (set by the view to the DataGrid's SelectedItems for multi-select).</summary>
    public IList? SelectedItems
    {
        get => _selectedItems;
        set
        {
            _selectedItems = value;
            RemoveCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>"12 groups" / "12 members" after the pending changes.</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>All (nested) memberships - read-only, loaded on demand.</summary>
    public BulkObservableCollection<DirectoryItem> NestedItems { get; } = [];

    [ObservableProperty]
    public partial bool IsNestedLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsNestedLoaded { get; private set; }

    [ObservableProperty]
    public partial string? NestedError { get; private set; }

    /// <summary>Object kinds offered by Add.</summary>
    public abstract IReadOnlyList<ObjectKind> AddKinds { get; }

    internal string Dn { get; private set; } = string.Empty;

    internal IEnumerable<MembershipItemViewModel> AllItems => _all;

    /// <summary>Call with the DataGrid's selection after it changed (multi-select remove).</summary>
    public void NotifySelectionChanged() => RemoveCommand.NotifyCanExecuteChanged();

    public override void Revert()
    {
        _all.RemoveAll(i => i.State == MembershipState.PendingAdd);
        foreach (var item in _all)
        {
            item.State = MembershipState.Unchanged;
        }

        RevertCore();
        Refresh();
    }

    internal override void Load(DirectoryObject obj)
    {
        Dn = obj.DistinguishedName;
        _all.Clear();
        Items.ReplaceAll([]);
        NestedItems.ReplaceAll([]);
        IsNestedLoaded = false;
        NestedError = null;
        Refresh();
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var picked = await Context.Services.Dialogs.PickObjectsAsync(Context.Session, AddKinds, multiSelect: true).ConfigureAwait(true);
        var added = new List<MembershipItemViewModel>();
        foreach (var dn in picked)
        {
            if (DistinguishedName.AreEqual(dn, Dn))
            {
                continue;
            }

            var existing = _all.FirstOrDefault(i => DistinguishedName.AreEqual(i.Dn, dn));
            if (existing is not null)
            {
                if (existing.State == MembershipState.PendingRemove)
                {
                    existing.State = MembershipState.Unchanged;
                }

                continue;
            }

            var item = new MembershipItemViewModel(DirectoryItem.Unresolved(dn, Context.Localizer), MembershipState.PendingAdd);
            _all.Add(item);
            added.Add(item);
        }

        Refresh();
        if (added.Count > 0)
        {
            await ResolveAsync(added, CancellationToken.None).ConfigureAwait(true);
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        foreach (var item in Selection().ToList())
        {
            switch (item.State)
            {
                case MembershipState.PendingAdd:
                    _all.Remove(item);
                    break;
                case MembershipState.PendingRemove:
                    item.State = MembershipState.Unchanged;
                    break;
                default:
                    if (CanRemoveItem(item))
                    {
                        item.State = MembershipState.PendingRemove;
                    }

                    break;
            }
        }

        SelectedItem = null;
        Refresh();
    }

    private bool CanRemove() => Selection().Any(CanRemoveItem);

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync(MembershipItemViewModel? item)
    {
        var target = item ?? SelectedItem;
        return target is null ? Task.CompletedTask : Context.Services.Dialogs.ShowPropertiesAsync(target.Dn);
    }

    private bool CanOpen(MembershipItemViewModel? item) => (item ?? SelectedItem) is not null;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task LoadNestedAsync(CancellationToken cancellationToken)
    {
        var token = _nestedLoads.Next();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
        IsNestedLoading = true;
        NestedError = null;
        try
        {
            var session = Context.Session;
            var search = new DirectorySearch
            {
                BaseDn = session.Info.DefaultNamingContext,
                Scope = SearchScope.Subtree,
                Filter = NestedFilter(Dn),
                Attributes = DirectoryItem.Attributes,
            };
            var localizer = Context.Localizer;
            var items = await Task.Run(
                async () =>
                {
                    var list = new List<DirectoryItem>();
                    await foreach (var obj in session.SearchAsync(search, linked.Token).ConfigureAwait(false))
                    {
                        list.Add(DirectoryItem.From(obj, localizer));
                    }

                    return list;
                },
                linked.Token).ConfigureAwait(true);
            var comparer = StringComparer.Create(Context.Culture, ignoreCase: true);
            NestedItems.ReplaceAll(items.OrderBy(i => i.Name, comparer));
            IsNestedLoaded = true;
        }
        catch (OperationCanceledException)
        {
            // Superseded or cancelled by the operator.
        }
        catch (DirectoryException ex)
        {
            NestedError = Context.Services.Errors.Translate(ex);
        }
        finally
        {
            IsNestedLoading = false;
        }
    }

    /// <summary>Filter of the "All (nested)" list for the object.</summary>
    protected abstract string NestedFilter(string distinguishedName);

    /// <summary>Whether an existing item may be removed (the primary group cannot).</summary>
    protected virtual bool CanRemoveItem(MembershipItemViewModel item) => true;

    protected virtual void RevertCore()
    {
    }

    /// <summary>Localized "N groups" / "N members" for the count line.</summary>
    protected abstract string FormatCount(int count);

    protected IReadOnlyList<string> PendingAdds => [.. _all.Where(i => i.State == MembershipState.PendingAdd).Select(i => i.Dn)];

    protected IReadOnlyList<string> PendingRemoves => [.. _all.Where(i => i.State == MembershipState.PendingRemove).Select(i => i.Dn)];

    protected override bool ComputeHasChanges() => _all.Any(i => i.State != MembershipState.Unchanged);

    protected override bool ComputeHasErrors() => false;

    /// <summary>Replaces the list (after loading and resolving).</summary>
    protected void SetItems(IEnumerable<MembershipItemViewModel> items)
    {
        _all.Clear();
        _all.AddRange(items);
        Refresh();
    }

    /// <summary>Reads names, kinds and SIDs of the given items in batches.</summary>
    protected async Task ResolveAsync(IReadOnlyList<MembershipItemViewModel> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            var session = Context.Session;
            var dns = items.Select(i => i.Dn).ToList();
            var found = await Task.Run(() => DirectoryLookup.ReadManyAsync(session, dns, DirectoryItem.Attributes, cancellationToken), cancellationToken)
                .ConfigureAwait(true);
            foreach (var item in items)
            {
                if (found.TryGetValue(DnKey.For(item.Dn), out var obj))
                {
                    item.Resolve(DirectoryItem.From(obj, Context.Localizer), obj.GetInt32(Attr.GroupType), AdConvert.GetRid(obj.ObjectSid));
                }
            }
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException)
        {
            // Unresolved items keep their RDN value as name.
        }
    }

    /// <summary>Re-applies sorting, filter, status texts and counts.</summary>
    protected void Refresh()
    {
        var comparer = StringComparer.Create(Context.Culture, ignoreCase: true);
        foreach (var item in _all)
        {
            item.StatusText = item.State switch
            {
                MembershipState.PendingAdd => Context.Localizer[L.Membership.PendingAdd],
                MembershipState.PendingRemove => Context.Localizer[L.Membership.PendingRemove],
                _ => item.IsPrimary ? Context.Localizer[L.Membership.PrimaryGroup] : string.Empty,
            };
        }

        Items.ReplaceAll(_all
            .Where(i => TextFolding.Contains(i.Name, FilterText) || TextFolding.Contains(i.Path, FilterText))
            .OrderBy(i => i.Name, comparer));
        CountText = FormatCount(_all.Count(i => i.State != MembershipState.PendingRemove));
        RemoveCommand.NotifyCanExecuteChanged();
        UpdateState();
    }

    partial void OnFilterTextChanged(string value) => Refresh();

    private IEnumerable<MembershipItemViewModel> Selection()
    {
        if (_selectedItems is { Count: > 0 } selected)
        {
            return selected.OfType<MembershipItemViewModel>();
        }

        return SelectedItem is null ? [] : [SelectedItem];
    }
}

/// <summary>
/// "Member Of" page: groups the object is a member of (memberOf, completed with ranged retrieval) plus the primary
/// group (primaryGroupID resolved via objectSid = domain SID + RID). Additions and removals are applied as
/// <see cref="AddToGroupsOperation"/> / <see cref="RemoveFromGroupsOperation"/>; "Set Primary Group" writes primaryGroupID.
/// </summary>
public sealed partial class MemberOfPageViewModel : MembershipPageViewModel
{
    private uint? _primaryRid;
    private MembershipItemViewModel? _pendingPrimary;

    internal MemberOfPageViewModel(FieldContext context)
        : base(context, L.Pages.MemberOf, context.Localizer[L.Pages.MemberOf])
    {
    }

    public override IReadOnlyList<ObjectKind> AddKinds { get; } = [ObjectKind.Group];

    /// <summary>The object has a primary group (users and computers).</summary>
    [ObservableProperty]
    public partial bool SupportsPrimaryGroup { get; private set; }

    /// <summary>Name of the (pending) primary group.</summary>
    [ObservableProperty]
    public partial string PrimaryGroupName { get; private set; } = string.Empty;

    internal override IEnumerable<string> ReadAttributes => [Attr.MemberOf, Attr.PrimaryGroupId, Attr.ObjectSid];

    /// <summary>Makes the selected group the primary group (global or universal security groups the object is already a member of).</summary>
    [RelayCommand(CanExecute = nameof(CanSetPrimaryGroup))]
    private void SetPrimaryGroup()
    {
        if (SelectedItem is not { } selected || !CanBePrimary(selected))
        {
            return;
        }

        foreach (var item in AllItems)
        {
            item.IsPrimary = ReferenceEquals(item, selected);
        }

        _pendingPrimary = selected.Rid == _primaryRid ? null : selected;
        PrimaryGroupName = selected.Name;
        Refresh();
        SetPrimaryGroupCommand.NotifyCanExecuteChanged();
    }

    private bool CanSetPrimaryGroup() => SupportsPrimaryGroup && SelectedItem is { } item && CanBePrimary(item);

    partial void OnSupportsPrimaryGroupChanged(bool value) => SetPrimaryGroupCommand.NotifyCanExecuteChanged();

    internal override void Load(DirectoryObject obj)
    {
        base.Load(obj);
        _pendingPrimary = null;
        _primaryRid = obj.GetInt64(Attr.PrimaryGroupId) is { } rid and > 0 and <= uint.MaxValue ? (uint)rid : null;
        SupportsPrimaryGroup = _primaryRid is not null;
        PrimaryGroupName = string.Empty;
    }

    internal override async Task LoadDetailsAsync(DirectoryObject obj, CancellationToken cancellationToken)
    {
        IsLoading = true;
        Error = null;
        try
        {
            var session = Context.Session;
            var groups = await Task.Run(() => DirectoryLookup.AllValuesAsync(session, obj, Attr.MemberOf, cancellationToken), cancellationToken)
                .ConfigureAwait(true);
            var items = groups.Select(dn => new MembershipItemViewModel(DirectoryItem.Unresolved(dn, Context.Localizer), MembershipState.Unchanged)).ToList();
            await ResolveAsync(items, cancellationToken).ConfigureAwait(true);
            if (await ReadPrimaryGroupAsync(obj, cancellationToken).ConfigureAwait(true) is { } primary)
            {
                primary.IsPrimary = true;
                PrimaryGroupName = primary.Name;
                items.RemoveAll(i => DistinguishedName.AreEqual(i.Dn, primary.Dn));
                items.Add(primary);
            }

            SetItems(items);
        }
        catch (DirectoryException ex)
        {
            Error = Context.Services.Errors.Translate(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal override IEnumerable<BatchOperation> GetOperations()
    {
        var operations = new List<BatchOperation>();
        if (_pendingPrimary?.Rid is { } rid)
        {
            operations.Add(new SetValuesOperation(Attr.PrimaryGroupId, [rid.ToString(CultureInfo.InvariantCulture)]));
        }

        if (PendingAdds is { Count: > 0 } adds)
        {
            operations.Add(new AddToGroupsOperation(adds));
        }

        if (PendingRemoves is { Count: > 0 } removes)
        {
            operations.Add(new RemoveFromGroupsOperation(removes));
        }

        return operations;
    }

    protected override string NestedFilter(string distinguishedName) =>
        LdapFilter.And(ObjectKind.Group.ToLdapFilter(), LdapFilter.InChain(Attr.Member, distinguishedName));

    protected override bool CanRemoveItem(MembershipItemViewModel item) => !item.IsPrimary;

    protected override void RevertCore()
    {
        _pendingPrimary = null;
        foreach (var item in AllItems)
        {
            item.IsPrimary = item.Rid is not null && item.Rid == _primaryRid;
        }

        PrimaryGroupName = AllItems.FirstOrDefault(i => i.IsPrimary)?.Name ?? string.Empty;
    }

    protected override bool ComputeHasChanges() => base.ComputeHasChanges() || _pendingPrimary is not null;

    protected override string FormatCount(int count) => Context.Localizer.Format(L.Membership.GroupCount, count);

    private static bool CanBePrimary(MembershipItemViewModel item) =>
        !item.IsPrimary
        && item.State == MembershipState.Unchanged
        && item.Rid is not null
        && item.GroupType is { } groupType
        && GroupTypes.IsSecurity(groupType)
        && GroupTypes.GetScope(groupType) is GroupScope.Global or GroupScope.Universal;

    private async Task<MembershipItemViewModel?> ReadPrimaryGroupAsync(DirectoryObject obj, CancellationToken cancellationToken)
    {
        if (_primaryRid is not { } rid || obj.ObjectSid is not { } sid)
        {
            return null;
        }

        var last = sid.LastIndexOf('-');
        if (last <= 0)
        {
            return null;
        }

        var groupSid = $"{sid[..last]}-{rid.ToString(CultureInfo.InvariantCulture)}";
        byte[] bytes;
        try
        {
            bytes = AdConvert.SidToBytes(groupSid);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return null;
        }

        var session = Context.Session;
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = $"({Attr.ObjectSid}={LdapFilter.EscapeBinary(bytes)})",
            Attributes = DirectoryItem.Attributes,
            SizeLimit = 1,
        };
        var localizer = Context.Localizer;
        var group = await Task.Run(
            async () =>
            {
                await foreach (var found in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
                {
                    return found;
                }

                return null;
            },
            cancellationToken).ConfigureAwait(true);
        return group is null
            ? null
            : new MembershipItemViewModel(DirectoryItem.From(group, localizer), MembershipState.Unchanged, group.GetInt32(Attr.GroupType), rid);
    }
}

/// <summary>
/// "Members" page of a group: all values of member (ranged retrieval), names resolved in batches of 100,
/// additions/removals applied as <see cref="AddValuesOperation"/> / <see cref="RemoveValuesOperation"/> on member.
/// </summary>
public sealed partial class MembersPageViewModel : MembershipPageViewModel
{
    internal MembersPageViewModel(FieldContext context)
        : base(context, L.Pages.Members, context.Localizer[L.Pages.Members])
    {
    }

    public override IReadOnlyList<ObjectKind> AddKinds { get; } =
        [ObjectKind.User, ObjectKind.Group, ObjectKind.Computer, ObjectKind.Contact];

    internal override IEnumerable<string> ReadAttributes => [];

    internal override async Task LoadDetailsAsync(DirectoryObject obj, CancellationToken cancellationToken)
    {
        IsLoading = true;
        Error = null;
        try
        {
            var session = Context.Session;
            var dn = obj.DistinguishedName;
            var members = await Task.Run(
                async () =>
                {
                    var list = new List<string>();
                    await foreach (var value in session.GetAllValuesAsync(dn, Attr.Member, cancellationToken).ConfigureAwait(false))
                    {
                        list.Add(value);
                    }

                    return list;
                },
                cancellationToken).ConfigureAwait(true);
            var items = members.Select(m => new MembershipItemViewModel(DirectoryItem.Unresolved(m, Context.Localizer), MembershipState.Unchanged)).ToList();
            await ResolveAsync(items, cancellationToken).ConfigureAwait(true);
            SetItems(items);
        }
        catch (DirectoryException ex)
        {
            Error = Context.Services.Errors.Translate(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal override IEnumerable<BatchOperation> GetOperations()
    {
        var operations = new List<BatchOperation>();
        if (PendingAdds is { Count: > 0 } adds)
        {
            // AddValues renders templates: DNs may contain braces (e.g. CN={GUID}), so they are escaped.
            operations.Add(new AddValuesOperation(Attr.Member, [.. adds.Select(TemplateText.Escape)]));
        }

        if (PendingRemoves is { Count: > 0 } removes)
        {
            operations.Add(new RemoveValuesOperation(Attr.Member, removes));
        }

        return operations;
    }

    protected override string NestedFilter(string distinguishedName) => LdapFilter.InChain(Attr.MemberOf, distinguishedName);

    protected override string FormatCount(int count) => Context.Localizer.Format(L.Membership.MemberCount, count);
}
