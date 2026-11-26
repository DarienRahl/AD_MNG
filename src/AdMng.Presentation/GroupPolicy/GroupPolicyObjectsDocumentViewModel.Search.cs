using AdMng.Presentation.GroupPolicy.Search;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>"Wyszukaj ustawienia…" on the toolbar of "Obiekty zasad grupy" (K7, design note 21.4).</summary>
public sealed partial class GroupPolicyObjectsDocumentViewModel
{
    /// <summary>Opens "Wyszukiwarka ustawień GPO" of the domain (or selects its tab).</summary>
    [RelayCommand]
    private void SearchSettings() => Actions.OpenSettingsSearch();
}
