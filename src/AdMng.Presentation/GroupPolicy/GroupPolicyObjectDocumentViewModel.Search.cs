using AdMng.Presentation.GroupPolicy.Search;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>"Znajdź we wszystkich GPO" on the GPO document's "Ustawienia" tab (K7, design note 21.4).</summary>
public sealed partial class GroupPolicyObjectDocumentViewModel
{
    /// <summary>
    /// The query "Znajdź we wszystkich GPO" opens the search with: the tab's search text, else the selected setting's name
    /// (null: none – the search opens empty).
    /// </summary>
    public string? SearchAllText => SearchText.Trim() is { Length: > 0 } text ? text : SelectedSetting?.Name;

    /// <summary>Opens "Wyszukiwarka ustawień GPO" (or selects its tab) with <see cref="SearchAllText"/> as the query.</summary>
    [RelayCommand]
    private void SearchAllGpos() => Actions.OpenSettingsSearch(SearchAllText);
}
