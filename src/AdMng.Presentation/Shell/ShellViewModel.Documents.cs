using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Pki;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// Commands of the active document (K6, docs/dev/k-computers-and-gpo.md 20.3): Ctrl+S "Zapisz zmiany…" of an editor
/// (<see cref="ISaveableDocument"/>), Ctrl+F into a document's own search (<see cref="ISearchableDocument"/>, see
/// <c>FindAsync</c>), and the palette's "Edytuj szablony administracyjne" / "Edytuj ustawienia zabezpieczeń" of the GPO the
/// window is on (<see cref="ActiveGpo"/>).
/// </summary>
public sealed partial class ShellViewModel
{
    private IEnumerable<CommandDescriptor> DocumentCommandDescriptors()
    {
        const string document = L.GroupPolicyEditing.DocumentCategory;
        return
        [
            new("document.save", L.GroupPolicyEditing.SaveCommand, document, "Ctrl+S", SaveDocumentCommand),
            new("gpo.editTemplates", L.AdministrativeTemplates.EditCommand, document, string.Empty, EditGpoTemplatesCommand),
            new("gpo.editSecurity", L.SecuritySettings.EditCommand, document, string.Empty, EditGpoSecuritySettingsCommand),
        ];
    }

    /// <summary>Ctrl+S: "Zapisz zmiany…" of the active editor (nothing when it has nothing to save).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveDocument))]
    private Task SaveDocumentAsync() =>
        SelectedTab is ISaveableDocument document && document.SaveCommand.CanExecute(null) ? document.SaveCommand.ExecuteAsync(null) : Task.CompletedTask;

    private bool CanSaveDocument() => SelectedTab is ISaveableDocument document && document.SaveCommand.CanExecute(null);

    /// <summary>"Edytuj szablony administracyjne" of the GPO the window is on (palette).</summary>
    [RelayCommand(CanExecute = nameof(CanEditActiveGpo))]
    private void EditGpoTemplates()
    {
        if (ActiveGpo() is { } gpo && Tree.GroupPolicy is { } factory)
        {
            factory.Actions.EditAdministrativeTemplates(gpo.Id, gpo.Name);
        }
    }

    /// <summary>"Edytuj ustawienia zabezpieczeń" of the GPO the window is on (palette).</summary>
    [RelayCommand(CanExecute = nameof(CanEditActiveGpo))]
    private void EditGpoSecuritySettings()
    {
        if (ActiveGpo() is { } gpo && Tree.GroupPolicy is { } factory)
        {
            factory.Actions.EditSecuritySettings(gpo.Id, gpo.Name);
        }
    }

    private bool CanEditActiveGpo() => CanUseGroupPolicy() && ActiveGpo() is not null;

    /// <summary>
    /// The GPO the window is on: the active GPO document or editor, the one GPO selected in "Obiekty zasad grupy", the selected
    /// link of "Zasady grupy", else the GPO selected in the tree; null when none.
    /// </summary>
    public (Guid Id, string Name)? ActiveGpo() => SelectedTab switch
    {
        GroupPolicyObjectDocumentViewModel gpo => (gpo.GpoId, gpo.Title),
        GroupPolicy.Search.GpoSettingsSearchDocumentViewModel { SelectedRow: { } found } => (found.GpoId, found.GpoName),
        AdMng.Presentation.GroupPolicy.Cleanup.GpoCleanupDocumentViewModel { SelectedRow: { GpoExists: true } row } => (row.GpoId, row.GpoName),
        AdministrativeTemplatesEditorViewModel templates => (templates.GpoId, templates.GpoName),
        SecuritySettingsEditorViewModel security => (security.GpoId, security.GpoName),
        GroupPolicyObjectsDocumentViewModel { Selection: [var row] } => (row.Gpo.Id, row.Name),
        GroupPolicyScopeDocumentViewModel { SelectedLink.Gpo: { } linked } => (linked.Id, linked.DisplayName),
        _ => Tree.SelectedNode is GpoNodeViewModel node ? (node.GpoId, node.Name) : null,
    };

    /// <summary>
    /// The PKI document the window is on (P1, docs/dev/p-certificate-services.md 2.5): the active PKI tab, null when none.
    /// W5 adds the arms its palette commands need (the selected template or CA of the active document).
    /// </summary>
    public PkiDocumentBase? ActivePki() => SelectedTab switch
    {
        PkiDocumentBase pki => pki,
        _ => null,
    };
}
