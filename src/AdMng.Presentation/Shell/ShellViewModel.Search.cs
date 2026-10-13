using System.Collections.ObjectModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>Global quick search (search as you type with ANR, Ctrl+E).</summary>
public sealed partial class ShellViewModel
{
    /// <summary>Maximum number of suggestions in the drop-down.</summary>
    public const int SuggestionLimit = 25;

    /// <summary>Minimum number of characters before the directory is queried.</summary>
    public const int MinimumSearchLength = 2;

    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    private static readonly ObjectKind[] SearchKinds =
        [ObjectKind.User, ObjectKind.Computer, ObjectKind.Group, ObjectKind.Contact, ObjectKind.OrganizationalUnit];

    private static readonly string[] SuggestionAttributes =
        [Attr.Name, Attr.ObjectClass, Attr.Description, Attr.UserAccountControl, Attr.LockoutTime, Attr.GroupType];

    private readonly Debouncer _searchDebouncer;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public ObservableCollection<SearchSuggestionViewModel> SearchSuggestions { get; } = [];

    [ObservableProperty]
    public partial SearchSuggestionViewModel? SelectedSuggestion { get; set; }

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    public partial bool IsSuggestionsOpen { get; set; }

    /// <summary>Debounce delay of the quick search (tests set it with a manual time provider).</summary>
    public TimeSpan SearchDebounce
    {
        get => _searchDebouncer.Delay;
        set => _searchDebouncer.Delay = value;
    }

    /// <summary>The running suggestion query (tests await it).</summary>
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;

    /// <summary>LDAP filter of the quick search: ANR restricted to users, computers, groups, contacts and OUs.</summary>
    public static string BuildSearchFilter(string text) =>
        LdapFilter.And(LdapFilter.Anr(text.Trim()), LdapFilter.Or(SearchKinds.Select(k => k.ToLdapFilter())));

    /// <summary>
    /// Enter in the search box: opens the suggestion the operator highlighted (arrow keys / mouse), otherwise all
    /// results in a new tab. Suggestions are never pre-highlighted, so a plain Enter always shows all results.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SubmitSearchAsync() =>
        SelectedSuggestion is { } suggestion && IsSuggestionsOpen ? OpenSuggestionAsync(suggestion) : ShowAllSearchResultsAsync();

    /// <summary>"Show all results": a Search tab without size limit (streamed).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ShowAllSearchResultsAsync()
    {
        var session = Sessions.Current;
        var text = SearchText.Trim();
        if (session is null || text.Length == 0)
        {
            return Task.CompletedTask;
        }

        _searchDebouncer.Cancel();
        IsSearching = false;
        IsSuggestionsOpen = false;
        var location = ListLocation.ForSearch(text, session.Info.DefaultNamingContext, Localizer.Format(L.Search.TabTitle, text), BuildSearchFilter(text));
        return OpenLocationInNewTabAsync(location);
    }

    /// <summary>Opens a suggestion: containers are navigated into, other objects are located (parent opened, object selected).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenSuggestionAsync(SearchSuggestionViewModel? suggestion)
    {
        if (suggestion is null)
        {
            return;
        }

        IsSuggestionsOpen = false;
        if (suggestion.IsContainer)
        {
            await NavigateAsync(ListLocation.ForContainer(suggestion.Dn, suggestion.Name, suggestion.Icon)).ConfigureAwait(true);
        }
        else
        {
            await LocateAsync(suggestion.Dn).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task ShowSuggestionPropertiesAsync(SearchSuggestionViewModel? suggestion)
    {
        IsSuggestionsOpen = false;
        return suggestion is null ? Task.CompletedTask : _dialogs.ShowPropertiesAsync(suggestion.Dn);
    }

    partial void OnSearchTextChanged(string value)
    {
        PendingSearch = _searchDebouncer.RunAsync(token => QuerySuggestionsAsync(value, token));
        PendingSearch.Forget();
    }

    private async Task QuerySuggestionsAsync(string text, CancellationToken token)
    {
        var session = Sessions.Current;
        var query = text.Trim();
        if (session is null || query.Length < MinimumSearchLength)
        {
            SearchSuggestions.Clear();
            SelectedSuggestion = null;
            IsSuggestionsOpen = false;
            IsSearching = false;
            return;
        }

        IsSearching = true;
        try
        {
            var search = new DirectorySearch
            {
                BaseDn = session.Info.DefaultNamingContext,
                Scope = SearchScope.Subtree,
                Filter = BuildSearchFilter(query),
                Attributes = SuggestionAttributes,
                SizeLimit = SuggestionLimit,
                PageSize = SuggestionLimit,
            };
            var found = new List<DirectoryObject>(SuggestionLimit);
            try
            {
                await Task.Run(
                    async () =>
                    {
                        await foreach (var obj in session.SearchAsync(search, token).ConfigureAwait(false))
                        {
                            found.Add(obj);
                            if (found.Count >= SuggestionLimit)
                            {
                                break;
                            }
                        }
                    },
                    token).ConfigureAwait(true);
            }
            catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.SizeLimitExceeded)
            {
                // More than SuggestionLimit matches: the DC ends the search with "size limit exceeded" after
                // returning the first entries - show those (the "show all" tab has no limit).
            }

            token.ThrowIfCancellationRequested();

            SearchSuggestions.Clear();
            foreach (var obj in found)
            {
                SearchSuggestions.Add(new SearchSuggestionViewModel(
                    obj.DistinguishedName,
                    obj.Name,
                    obj.Kind,
                    ObjectIcons.For(obj),
                    Localizer.TypeName(obj),
                    obj.Dn.Parent is { IsRoot: false } parent ? AddressResolver.ToDisplayPath(parent.ToString()) : string.Empty,
                    obj.GetString(Attr.Description)));
            }

            SelectedSuggestion = null;
            IsSuggestionsOpen = true;
        }
        catch (DirectoryException ex)
        {
            _notifications.Show(Services.NotificationSeverity.Warning, Errors.Translate(ex));
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsSearching = false;
            }
        }
    }
}
