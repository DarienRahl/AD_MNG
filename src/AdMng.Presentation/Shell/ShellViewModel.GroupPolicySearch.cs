using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// "Wyszukaj ustawienie we wszystkich GPO…" (K7, docs/dev/k-computers-and-gpo.md 21.4): the menu Akcja → Zasady grupy, the
/// palette entry <c>gpo.searchSettings</c> and the tree's menu open "Wyszukiwarka ustawień GPO" of the window's domain, or
/// select its tab when it is open - all through <see cref="OpenGroupPolicyDocument"/>. A request with a query
/// (<c>GroupPolicyDocumentRequest.SearchText</c>, "Znajdź we wszystkich GPO") hands it to the open tab.
/// </summary>
public sealed partial class ShellViewModel
{
    private IEnumerable<CommandDescriptor> GroupPolicySearchCommandDescriptors() =>
    [
        new("gpo.searchSettings", L.GpoSearch.Command, L.CommandCategories.Tools, string.Empty, OpenGroupPolicySearchCommand),
    ];

    /// <summary>Opens (or selects) "Wyszukiwarka ustawień GPO".</summary>
    [RelayCommand(CanExecute = nameof(CanUseGroupPolicy))]
    private void OpenGroupPolicySearch() => OpenGroupPolicyDocument(GpoSettingsSearchEntry.Request);
}
