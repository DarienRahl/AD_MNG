using AdMng.Presentation.GroupPolicy.Cleanup;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// "Porządki w GPO…" (K7, docs/dev/k-computers-and-gpo.md 21.6): the menu Akcja → Zasady grupy, the palette entry
/// <c>gpo.cleanup</c> and the tree's menu open the cleanup advisor of the window's domain, or select its tab when it is
/// open - all through <see cref="OpenGroupPolicyDocument"/>.
/// </summary>
public sealed partial class ShellViewModel
{
    private IEnumerable<CommandDescriptor> GroupPolicyCleanupCommandDescriptors() =>
    [
        new("gpo.cleanup", L.GpoCleanup.Command, L.CommandCategories.Tools, string.Empty, OpenGroupPolicyCleanupCommand),
    ];

    /// <summary>Opens (or selects) "Porządki w GPO".</summary>
    [RelayCommand(CanExecute = nameof(CanUseGroupPolicy))]
    private void OpenGroupPolicyCleanup() => OpenGroupPolicyDocument(GpoCleanupEntry.Request);
}
