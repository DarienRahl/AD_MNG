using System.Collections;
using System.Collections.ObjectModel;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Feedback;
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
/// <remarks>
/// The dialog has one status line (<see cref="Status"/>; track V, stage V2b, design note
/// docs/dev/v-interface-renewal.md "V2b-2 as built"). "Find Now" and the check of the typed names (Check Names, and OK
/// with names in the box) are its operations - one at a time, each command refuses while another runs - and can be
/// stopped in the line. The line says how they ended: how many objects the list shows (the summary), that more match
/// than are listed, which names were not resolved, that the operation was stopped or why it failed, with Retry. A
/// check that resolved every name says nothing - the chosen objects appear - and neither does OK that closes the
/// dialog. As the state that lasts: the session of the dialog ended.
/// </remarks>
public sealed partial class ObjectPickerViewModel : WindowViewModelBase, IHasPageStatus
{
    /// <summary>Maximum objects listed by Find Now (ADUC shows a hint to refine the search above that).</summary>
    public const int MaxResults = 500;

    private const int MaxMatchesPerName = 50;

    /// <summary>How many names that were not resolved the line explains at most (all of them stay in the box).</summary>
    private const int MaxReasons = 3;

    private readonly IDirectorySession _session;
    private readonly SerialCancellation _searches = new();
    private IList? _selectedResults;

    /// <summary>The list of results was filled at least once: the line has its count as the summary.</summary>
    private bool _listed;

    /// <summary>The listed matches of ambiguous names → the typed name each one answers (ADUC "Multiple Names Found").</summary>
    private Dictionary<DirectoryItem, string> _ambiguousMatches = new(ReferenceEqualityComparer.Instance);

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
        Status = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        UpdateLasting();
    }

    /// <summary>"Select Groups" / "Wybieranie: Grupy".</summary>
    public override string WindowTitle =>
        Localizer.Format(L.Picker.WindowTitle, string.Join(", ", ObjectTypes.Select(o => o.Text)));

    /// <summary>
    /// The status of the dialog - what its one status line shows: the running search or check of names (with Stop), how
    /// it ended, at rest how many objects the list of results shows, and that the session of the dialog ended.
    /// </summary>
    public PageStatusHolder Status { get; }

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

    /// <summary>
    /// Localized status ("3 objects found", "Name not found: x", "Several objects match: x") as plain text: the status
    /// line says it (kept for callers that read it).
    /// </summary>
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

    /// <summary>
    /// Lists the objects of the checked types in the location (ANR filtered by the typed text when present). The search
    /// is an operation of the dialog: its line says "Searching in …" with Stop; the list it fills is the result, so the
    /// line then shows its count - and says, as an attention, when more objects match than are listed. A search that
    /// was stopped or failed leaves the list as it was.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task FindNowAsync()
    {
        if (IsSessionEnded)
        {
            // The line says so for good.
            return;
        }

        var latest = _searches.Next();
        IsSearching = true;
        StatusText = Localizer[L.Common.Loading];
        var location = LocationText;
        using var searching = Status.Begin(
            Localizer.Format(L.ObjectPickerStatus.Searching, location), new PageStatusLog(L.ObjectPickerStatus.Searching));

        // The operator's Stop and "the latest wins" (closing the dialog, the end of its session) both end the search.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(latest, searching.Token);
        var token = linked.Token;
        try
        {
            var text = SearchText.Trim();
            var filter = text.Length > 0 && !text.Contains(';', StringComparison.Ordinal)
                ? LdapFilter.And(KindsFilter(), LdapFilter.Anr(text))
                : KindsFilter();
            var found = await SearchAsync(filter, MaxResults + 1, token).ConfigureAwait(true);
            if (searching.IsEnded)
            {
                return;
            }

            if (searching.IsCancelling)
            {
                // The domain controller answered although Stop was pressed: the list stays as it was.
                SearchStopped(searching);
                return;
            }

            _ambiguousMatches = new(ReferenceEqualityComparer.Instance);
            Results.ReplaceAll(found.Take(MaxResults));
            _listed = true;
            UpdateSummary();
            if (found.Count > MaxResults)
            {
                StatusText = Localizer.Format(L.Picker.TooMany, MaxResults);
                searching.CompleteAttention(
                    Localizer.Format(L.ObjectPickerStatus.TooMany, MaxResults), new PageStatusLog(L.ObjectPickerStatus.TooMany, MaxResults));
            }
            else
            {
                // The list is the result: the line shows its count as the summary.
                StatusText = Localizer.Format(L.Picker.Found, found.Count);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Only a search the operator stopped says so.
            if (searching.IsCancelling)
            {
                SearchStopped(searching);
            }
        }
        catch (DirectoryException ex)
        {
            if (searching.IsEnded)
            {
                return;
            }

            StatusText = Errors.Translate(ex);
            var code = ResultCode.Of(ex);
            searching.CompleteError(
                PageStatusText.Failure(Localizer.Format(L.ObjectPickerStatus.SearchFailed, location), StatusText, Localizer[L.Feedback.RetryNext], code),
                new PageStatusLog(L.ObjectPickerStatus.SearchFailed).WithCode(code),
                new PageStatusAction(Localizer[L.Common.Retry], FindNowCommand));
        }
        finally
        {
            if (_searches.IsCurrent(latest))
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>Resolves the typed names (';'-separated): unique matches are chosen, ambiguous ones listed.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckNames))]
    private Task CheckNamesAsync() => ResolveNamesAsync();

    /// <summary>
    /// The check of the typed names - an operation of the dialog: its line says "Checking the names (3)…" with Stop.
    /// Every name with exactly one match is chosen, the matches of ambiguous names are listed, and the names that were
    /// not resolved stay in the box; the line then says which ones and why (three at most). A check that resolved
    /// everything says nothing. A check that was stopped or failed changes nothing: no object is added.
    /// </summary>
    private async Task<bool> ResolveNamesAsync()
    {
        var terms = SearchText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return true;
        }

        if (IsSessionEnded)
        {
            // The line says so for good.
            return false;
        }

        var latest = _searches.Next();
        IsSearching = true;
        using var checking = Status.Begin(
            Localizer.Format(L.ObjectPickerStatus.Checking, terms.Length), new PageStatusLog(L.ObjectPickerStatus.Checking, terms.Length));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(latest, checking.Token);
        var token = linked.Token;
        var unresolved = new List<string>();
        var messages = new List<string>();
        try
        {
            var resolved = new List<DirectoryItem>();
            var ambiguous = new List<DirectoryItem>();
            var ambiguousMatches = new Dictionary<DirectoryItem, string>(ReferenceEqualityComparer.Instance);
            var notFound = 0;
            var several = 0;
            foreach (var term in terms)
            {
                var matches = await FindByNameAsync(term, token).ConfigureAwait(true);
                switch (matches.Count)
                {
                    case 0:
                        unresolved.Add(term);
                        messages.Add(Localizer.Format(L.Picker.NotFound, term));
                        notFound++;
                        break;
                    case 1:
                        resolved.Add(matches[0]);
                        break;
                    default:
                        unresolved.Add(term);
                        foreach (var match in matches)
                        {
                            ambiguous.Add(match);
                            ambiguousMatches[match] = term;
                        }

                        messages.Add(Localizer.Format(L.Picker.Ambiguous, term));
                        several++;
                        break;
                }
            }

            if (checking.IsEnded)
            {
                return false;
            }

            if (checking.IsCancelling)
            {
                // The domain controller answered although Stop was pressed: nothing is added.
                CheckStopped(checking);
                return false;
            }

            foreach (var item in resolved)
            {
                Choose(item);
            }

            if (ambiguous.Count > 0)
            {
                _ambiguousMatches = ambiguousMatches;
                Results.ReplaceAll(ambiguous);
                _listed = true;
                UpdateSummary();
            }

            SearchText = string.Join("; ", unresolved);
            StatusText = messages.Count == 0 ? null : string.Join(Environment.NewLine, messages);
            if (messages.Count > 0)
            {
                // What was skipped and why, name by name (the sentences the dialog always had); the rest is in the box.
                checking.CompleteAttention(
                    PageStatusText.Failure(Localizer[L.ObjectPickerStatus.NotResolved], string.Join(" ", messages.Take(MaxReasons))),
                    new PageStatusLog(L.ObjectPickerStatus.NotResolved, notFound, several));
            }

            return unresolved.Count == 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Only a check the operator stopped says so.
            if (checking.IsCancelling)
            {
                CheckStopped(checking);
            }

            return false;
        }
        catch (DirectoryException ex)
        {
            if (checking.IsEnded)
            {
                return false;
            }

            StatusText = Errors.Translate(ex);
            var code = ResultCode.Of(ex);
            checking.CompleteError(
                PageStatusText.Failure(Localizer[L.ObjectPickerStatus.CheckFailed], StatusText, Localizer[L.Feedback.RetryNext], code),
                new PageStatusLog(L.ObjectPickerStatus.CheckFailed).WithCode(code),
                new PageStatusAction(Localizer[L.Common.Retry], CheckNamesCommand));
            return false;
        }
        finally
        {
            if (_searches.IsCurrent(latest))
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>The operator stopped the search: the list is as it was, and "Find Now" searches again.</summary>
    private void SearchStopped(OperationScope searching)
    {
        StatusText = Localizer[L.Errors.Cancelled];
        var again = Localizer[L.Find.FindNow];
        searching.CompleteAttention(
            Localizer.Format(L.ObjectPickerStatus.SearchStopped, again),
            new PageStatusLog(L.ObjectPickerStatus.SearchStopped),
            new PageStatusAction(again, FindNowCommand));
    }

    /// <summary>The operator stopped the check of the names: nothing was added, and "Check Names" checks again.</summary>
    private void CheckStopped(OperationScope checking)
    {
        StatusText = Localizer[L.Errors.Cancelled];
        var again = Localizer[L.Picker.CheckNames];
        checking.CompleteAttention(
            Localizer.Format(L.ObjectPickerStatus.CheckStopped, again),
            new PageStatusLog(L.ObjectPickerStatus.CheckStopped),
            new PageStatusAction(again, CheckNamesCommand));
    }

    private bool CanCheckNames() => !IsSearching && !IsSessionEnded && SearchText.Trim().Length > 0;

    private bool CanSearch() => !IsSearching && !IsSessionEnded && ObjectTypes.Any(o => o.IsChecked);

    /// <summary>
    /// Adds the selected results to <see cref="Chosen"/>. A match of an ambiguous name answers that name: the name is
    /// removed from <see cref="SearchText"/>, so OK does not resolve it again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedResults))]
    private void AddSelected()
    {
        var answered = new List<string>();
        foreach (var item in SelectedItems().ToList())
        {
            Choose(item);
            if (_ambiguousMatches.TryGetValue(item, out var term))
            {
                answered.Add(term);
            }
        }

        RemoveTerms(answered);
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
        // Matches selected for ambiguous names answer them (OK in ADUC's "Multiple Names Found").
        if (SelectedItems().Any(_ambiguousMatches.ContainsKey))
        {
            AddSelected();
        }

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

        // The dialog's own sentences in the new language (how a search or a check ended stays as it was said).
        UpdateLasting();
        UpdateSummary();
    }

    protected override void OnSessionEnded()
    {
        // The line says for good that the session ended; a search or a check that still runs ends without a word of its
        // own (the state first, so the line goes straight from "working" to it), and "busy" ends with it at once.
        UpdateLasting();
        _searches.Cancel();
        Status.Operation?.Dispose();
        IsSearching = false;
        FindNowCommand.NotifyCanExecuteChanged();
        CheckNamesCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searches.Cancel();

            // What still runs says nothing any more.
            Status.Operation?.Dispose();
            foreach (var option in ObjectTypes)
            {
                option.Changed -= OnObjectTypesChanged;
            }
        }
    }

    private void OnObjectTypesChanged(object? sender, EventArgs e) => FindNowCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// The state of the dialog that lasts: its session ended - an error that cannot be closed, nothing can be searched
    /// any more. (The dialog is always opened with the session of its caller, so there is no "not connected".)
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(SessionEndedStatus);

    /// <summary>The summary of the list of results: how many objects it shows (nothing before it was filled once).</summary>
    private void UpdateSummary() => Status.SetIdle(
        _listed ? Localizer.Format(L.ObjectPickerStatus.Found, PageStatusText.Objects(Localizer, Results.Count)) : null);

    private bool HasSelectedResults() => SelectedItems().Any();

    private IEnumerable<DirectoryItem> SelectedItems()
    {
        if (_selectedResults is { Count: > 0 } selected)
        {
            return selected.OfType<DirectoryItem>();
        }

        return SelectedResult is null ? [] : [SelectedResult];
    }

    /// <summary>Removes typed names that were answered by choosing one of their matches.</summary>
    private void RemoveTerms(IReadOnlyCollection<string> answered)
    {
        if (answered.Count == 0)
        {
            return;
        }

        var remaining = SearchText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !answered.Contains(t, StringComparer.Ordinal));
        SearchText = string.Join("; ", remaining);
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
