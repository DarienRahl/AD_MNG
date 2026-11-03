using AdMng.Presentation.GroupPolicy.Diagnostics;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>"Diagnostyka…" on the toolbar of "Obiekty zasad grupy" (K6, design note 20.2).</summary>
public sealed partial class GroupPolicyObjectsDocumentViewModel
{
    /// <summary>Opens "Diagnostyka zasad grupy" of the domain (or selects its tab).</summary>
    [RelayCommand]
    private void Diagnose() => Actions.OpenDiagnostics();
}
