using AdMng.Presentation.GroupPolicy.Diagnostics;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// "Diagnostyka zasad grupy…" (K6, docs/dev/k-computers-and-gpo.md 20.2): the menu Akcja → Zasady grupy, the palette entry
/// <c>gpo.diagnose</c> ("Sprawdź zasady grupy") and the tree's menu open the document of the window's domain, or select its
/// tab when it is open - all through <see cref="OpenGroupPolicyDocument"/>.
/// </summary>
public sealed partial class ShellViewModel
{
    private IEnumerable<CommandDescriptor> GroupPolicyDiagnosticsCommandDescriptors() =>
    [
        new("gpo.diagnose", L.GpoDiagnostics.Command, L.CommandCategories.Tools, string.Empty, OpenGroupPolicyDiagnosticsCommand),
    ];

    /// <summary>Opens (or selects) "Diagnostyka zasad grupy".</summary>
    [RelayCommand(CanExecute = nameof(CanUseGroupPolicy))]
    private void OpenGroupPolicyDiagnostics() => OpenGroupPolicyDocument(GroupPolicyDiagnosticsEntry.Request);
}
