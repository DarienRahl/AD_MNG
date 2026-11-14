namespace AdMng.Presentation.GroupPolicy.Search;

/// <summary>
/// K7 contract (docs/dev/k-computers-and-gpo.md 21.4) – the document itself: W4. Opens "Wyszukiwarka ustawień GPO"
/// (one tab per connection window).
/// </summary>
public static class GpoSettingsSearchEntry
{
    /// <summary>The request of the search document.</summary>
    public static GroupPolicyDocumentRequest Request { get; } = new(GroupPolicyDocumentKind.SettingsSearch);

    /// <summary>
    /// Opens the search (or selects its tab) with <paramref name="searchText"/> as the query ("Znajdź we wszystkich GPO").
    /// False when no shell listens. Until W4 registers the document the factory refuses the kind.
    /// </summary>
    public static bool OpenSettingsSearch(this GroupPolicyActions actions, string? searchText = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return actions.Services.OpenDocument(searchText is { Length: > 0 } ? Request with { SearchText = searchText } : Request);
    }
}
