using System.Collections;
using System.Collections.ObjectModel;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>An object type check box of the picker ("Object Types...").</summary>
public sealed partial class ObjectTypeOption : ObservableObject
{
    internal ObjectTypeOption(ObjectKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    /// <summary>Raised when <see cref="IsChecked"/> changed.</summary>
    public event EventHandler? Changed;

    public ObjectKind Kind { get; }

    public string Text { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;

    partial void OnIsCheckedChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// ADUC "Select Users, Computers, or Groups": object types, location (container or entire domain), names typed with
/// "Check Names" (ambiguous name resolution; several matches are listed for choice) and an advanced search list.
/// <see cref="Result"/> holds the chosen DNs after OK.
/// </summary>
public sealed partial class ObjectPickerViewModel : WindowViewModelBase
{
    /// <summary>Maximum objects listed by Find Now (ADUC shows a hint to refine the search above that).</summary>
    public const int MaxResults = 500;

    private const int MaxMatchesPerName = 50;

    private readonly IDirectorySession _session;
    private readonly SerialCancellation _searches = new();
    private IList? _selectedResults;

    public ObjectPickerViewModel(DialogServices services, IDirectorySession session, IReadOnlyList<ObjectKind> kinds, bool multiSelect)
        : base(services)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(kinds);
        IsMultiSelect = multiSelect;
        IReadOnlyList<ObjectKind> offered = kinds.Count == 0
            ? [ObjectKind.User, ObjectKind.Group, ObjectKind.Computer, ObjectKind.Contact]
            : [.. kinds.Distinct()];
        ObjectTypes = [.. offered.Select(k => new ObjectTypeOption(k, Localizer[DisplayNames.KindPluralKey(k)]))];
        foreach (var option in ObjectTypes)
        {
            option.Changed += OnObjectTypesChanged;
        }

        LocationDn = session.Info.DefaultNamingContext;
    }

    /// <summary>"Select Groups" / "Wybieranie: Grupy".</summary>
    public override string WindowTitle =>
        Localizer.Format(L.Picker.WindowTitle, string.Join(", ", ObjectTypes.Select(o => o.Text)));

    public bool IsMultiSelect { get; }

    public IReadOnlyList<ObjectTypeOption> ObjectTypes { get; }

    /// <summary>Search location (subtree).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationText))]
    public partial string LocationDn { get; set; }

    public string LocationText => AddressResolver.ToDisplayPath(LocationDn);

    /// <summary>Names to resolve, separated by ';' ("Enter the object names to select").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNamesCommand), nameof(OkCommand))]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Resolved objects that will be returned by OK.</summary>
    public ObservableCollection<DirectoryItem> Chosen { get; } = [];

    /// <summary>Find Now results / matches of an ambiguous name.</summary>
    public BulkObservableCollection<DirectoryItem> Results { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedCommand), nameof(OkCommand))]
    public partial DirectoryItem? SelectedResult { get; set; }

    /// <summary>The results grid's selected items (set by the view for multi-select).</summary>
    public IList? SelectedResults
    {
        get => _selectedResults;
        set
        {
            _selectedResults = value;
            AddSelectedCommand.NotifyCanExecuteChanged();
            OkCommand.NotifyCanExecuteChanged();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindNowCommand), nameof(CheckNamesCommand), nameof(OkCommand))]
    public partial bool IsSearching { get; private set; }

    /// <summary>Localized status ("3 objects found", "Name not found: x", "Several objects match: x").</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>The chosen DNs after OK (empty when cancelled).</summary>
    public IReadOnlyList<string> Result { get; private set; } = [];

    /// <summary>Call after the grid selection changed (multi-select).</summary>
    public void NotifySelectionChanged()
    {
        AddSelectedCommand.NotifyCanExecuteChanged();
        OkCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task BrowseLocationAsync()
    {
        var picked = await Services.Dialogs.PickContainerAsync(_session, LocationDn).ConfigureAwait(true);
        if (picked is not null)
        {
            LocationDn = picked;
        }
    }

    /// <summary>Lists the objects of the checked types in the location (ANR filtered by the typed text when present).</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task FindNowAsync()
    {
        var token = _searches.Next();
        IsSearching = true;
        StatusText = Localizer[L.Common.Loading];
        try
        {
            var text = SearchText.Trim();
            var filter = text.Length > 0 && !text.Contains(';', StringComparison.Ordinal)
                ? LdapFilter.And(KindsFilter(), LdapFilter.Anr(text))
                : KindsFilter();
            var found = await SearchAsync(filter, MaxResults + 1, token).ConfigureAwait(true);
            Results.ReplaceAll(found.Take(MaxResults));
            StatusText = found.Count > MaxResults
                ? Localizer.Format(L.Picker.TooMany, MaxResults)
                : Localizer.Format(L.Picker.Found, found.Count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded.
        }
        catch (DirectoryException ex)
        {
            StatusText = Errors.Translate(ex);
        }
        finally
        {
            if (_searches.IsCurrent(token))
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>Resolves the typed names (';'-separated): unique matches are chosen, ambiguous ones listed.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckNames))]
    private Task CheckNamesAsync() => ResolveNamesAsync();

    private async Task<bool> ResolveNamesAsync()
    {
        var terms = SearchText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return true;
        }

        var token = _searches.Next();
        IsSearching = true;
        var unresolved = new List<string>();
        var messages = new List<string>();
        try
        {
            var ambiguous = new List<DirectoryItem>();
            foreach (var term in terms)
            {
                var matches = await FindByNameAsync(term, token).ConfigureAwait(true);
                switch (matches.Count)
                {
                    case 0:
                        unresolved.Add(term);
                        messages.Add(Localizer.Format(L.Picker.NotFound, term));
                        break;
                    case 1:
                        Choose(matches[0]);
                        break;
                    default:
                        unresolved.Add(term);
                        ambiguous.AddRange(matches);
                        messages.Add(Localizer.Format(L.Picker.Ambiguous, term));
                        break;
                }
            }

            if (ambiguous.Count > 0)
            {
                Results.ReplaceAll(ambiguous);
            }

            SearchText = string.Join("; ", unresolved);
            StatusText = messages.Count == 0 ? null : string.Join(Environment.NewLine, messages);
            return unresolved.Count == 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (DirectoryException ex)
        {
            StatusText = Errors.Translate(ex);
            return false;
        }
        finally
        {
            if (_searches.IsCurrent(token))
            {
                IsSearching = false;
            }
        }
    }

    private bool CanCheckNames() => !IsSearching && SearchText.Trim().Length > 0;

    private bool CanSearch() => !IsSearching && ObjectTypes.Any(o => o.IsChecked);

    /// <summary>Adds the selected results to <see cref="Chosen"/>.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedResults))]
    private void AddSelected()
    {
        foreach (var item in SelectedItems().ToList())
        {
            Choose(item);
        }
    }

    [RelayCommand]
    private void RemoveChosen(DirectoryItem? item)
    {
        if (item is not null)
        {
            Chosen.Remove(item);
            OkCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOk))]
    private async Task OkAsync()
    {
        if (SearchText.Trim().Length > 0 && !await ResolveNamesAsync().ConfigureAwait(true))
        {
            return;
        }

        if (Chosen.Count == 0)
        {
            foreach (var item in SelectedItems().ToList())
            {
                Choose(item);
            }
        }

        if (Chosen.Count == 0)
        {
            return;
        }

        Result = [.. Chosen.Select(c => c.Dn)];
        RequestClose(true);
    }

    private bool CanOk() => !IsSearching && (Chosen.Count > 0 || SearchText.Trim().Length > 0 || HasSelectedResults());

    [RelayCommand]
    private void Cancel()
    {
        Result = [];
        RequestClose(false);
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(LocationText));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searches.Cancel();
            foreach (var option in ObjectTypes)
            {
                option.Changed -= OnObjectTypesChanged;
            }
        }
    }

    private void OnObjectTypesChanged(object? sender, EventArgs e) => FindNowCommand.NotifyCanExecuteChanged();

    private bool HasSelectedResults() => SelectedItems().Any();

    private IEnumerable<DirectoryItem> SelectedItems()
    {
        if (_selectedResults is { Count: > 0 } selected)
        {
            return selected.OfType<DirectoryItem>();
        }

        return SelectedResult is null ? [] : [SelectedResult];
    }

    private void Choose(DirectoryItem item)
    {
        if (!IsMultiSelect)
        {
            Chosen.Clear();
        }

        if (!Chosen.Any(c => DistinguishedName.AreEqual(c.Dn, item.Dn)))
        {
            Chosen.Add(item);
        }

        OkCommand.NotifyCanExecuteChanged();
    }

    private string KindsFilter()
    {
        var kinds = ObjectTypes.Where(o => o.IsChecked).Select(o => o.Kind).ToList();
        return kinds.Count == 0 ? "(!(objectClass=*))" : LdapFilter.Or(kinds.Select(k => k.ToLdapFilter()));
    }

    private async Task<List<DirectoryItem>> FindByNameAsync(string term, CancellationToken cancellationToken)
    {
        if (AddressResolver.IsDistinguishedName(term))
        {
            var session = _session;
            var obj = await Task.Run(() => session.GetAsync(term, DirectoryItem.Attributes, cancellationToken), cancellationToken).ConfigureAwait(true);
            return obj is not null && ObjectTypes.Any(o => o.IsChecked && o.Kind == obj.Kind) ? [DirectoryItem.From(obj, Localizer)] : [];
        }

        return await SearchAsync(LdapFilter.And(KindsFilter(), LdapFilter.Anr(term)), MaxMatchesPerName, cancellationToken).ConfigureAwait(true);
    }

    private async Task<List<DirectoryItem>> SearchAsync(string filter, int sizeLimit, CancellationToken cancellationToken)
    {
        var search = new DirectorySearch
        {
            BaseDn = LocationDn,
            Scope = SearchScope.Subtree,
            Filter = filter,
            Attributes = DirectoryItem.Attributes,
            SizeLimit = sizeLimit,
        };
        var session = _session;
        var localizer = Localizer;
        var items = await Task.Run(
            async () =>
            {
                var list = new List<DirectoryItem>();
                try
                {
                    await foreach (var obj in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
                    {
                        list.Add(DirectoryItem.From(obj, localizer));
                    }
                }
                catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.SizeLimitExceeded)
                {
                    // The list is complete up to the limit.
                }

                return list;
            },
            cancellationToken).ConfigureAwait(true);
        var comparer = StringComparer.Create(Localizer.Culture, ignoreCase: true);
        return [.. items.OrderBy(i => i.Name, comparer)];
    }
}
