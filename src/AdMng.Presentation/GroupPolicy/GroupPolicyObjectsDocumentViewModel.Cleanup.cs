using AdMng.Presentation.GroupPolicy.Cleanup;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>"Porządki…" on the toolbar of "Obiekty zasad grupy" (K7, design note 21.6).</summary>
public sealed partial class GroupPolicyObjectsDocumentViewModel
{
    /// <summary>Opens "Porządki w GPO" of the domain (or selects its tab).</summary>
    [RelayCommand]
    private void Cleanup() => Actions.OpenCleanup();
}
