using AdMng.Core.Computers.Remote;
using AdMng.Core.Csv;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.Modeling;
using AdMng.Presentation.GroupPolicy.SecuritySettings;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>
/// Creates the Group Policy documents of a connection window (scoped, design note 7): every document gets the window's
/// <see cref="GroupPolicyServices"/> and <see cref="GroupPolicyActions"/>. Closing a tab disposes its document.
/// </summary>
public sealed class GroupPolicyViewModelFactory(GroupPolicyServices services, GroupPolicyActions actions, PolicyDefinitionsService? definitions = null,
    PolicyTemplatePackageLoader? loader = null, IHostAddressResolver? resolver = null, ICsvCodec? csv = null)
{
    public GroupPolicyServices Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    public GroupPolicyActions Actions { get; } = actions ?? throw new ArgumentNullException(nameof(actions));

    /// <summary>The ADMX templates of the window (K3).</summary>
    public PolicyDefinitionsService Definitions { get; } = definitions ?? new PolicyDefinitionsService();

    /// <summary>The packages of "Zaktualizuj szablony" (K3).</summary>
    public PolicyTemplatePackageLoader Loader { get; } = loader ?? new PolicyTemplatePackageLoader();

    /// <summary>"Zaktualizuj szablony ADMX" of the window: the update window, then (when the store changed) the editors read the templates again.</summary>
    public async Task<bool> UpdateTemplatesAsync()
    {
        var update = new CentralStoreUpdateViewModel(Services, Definitions, Loader);
        return await Services.Dialogs.ShowCentralStoreUpdateAsync(update).ConfigureAwait(true);
    }

    /// <summary>
    /// The document a request names (not loaded yet: call <see cref="GroupPolicyDocumentBase.LoadAsync"/>). An editor gets the
    /// request's <see cref="GroupPolicyDocumentRequest.Focus"/> ("Edytuj to ustawienie"), applied when its load ends.
    /// </summary>
    public GroupPolicyDocumentBase Create(GroupPolicyDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = CreateDocument(request);
        if (request.Focus is { } focus && document is IGroupPolicySettingEditor editor)
        {
            editor.FocusSetting(focus);
        }

        return document;
    }

    private GroupPolicyDocumentBase CreateDocument(GroupPolicyDocumentRequest request)
    {
        return request.Kind switch
        {
            GroupPolicyDocumentKind.Object when request.GpoId is { } id =>
                new GroupPolicyObjectDocumentViewModel(Services, Actions, id, request.Name, request.Tab ?? GpoDocumentTab.Scope),
            GroupPolicyDocumentKind.Scope when request.ContainerDn is { Length: > 0 } dn =>
                new GroupPolicyScopeDocumentViewModel(Services, Actions, dn, request.Name),
            GroupPolicyDocumentKind.WmiFilters => new WmiFiltersDocumentViewModel(Services, Actions),
            GroupPolicyDocumentKind.AdministrativeTemplates when request.GpoId is { } templatesId =>
                new AdministrativeTemplatesEditorViewModel(Services, Actions, Definitions, templatesId, request.Name) { UpdateTemplates = UpdateTemplatesAsync },
            GroupPolicyDocumentKind.SecuritySettings when request.GpoId is { } securityId =>
                new SecuritySettingsEditorViewModel(Services, Actions, securityId, request.Name),
            GroupPolicyDocumentKind.Objects => new GroupPolicyObjectsDocumentViewModel(Services, Actions) { UpdateTemplates = UpdateTemplatesAsync },
            GroupPolicyDocumentKind.Modeling => new GroupPolicyModelingDocumentViewModel(Services, Actions, Definitions, request, resolver, csv),
            _ => throw new ArgumentException("The request names no GPO or container.", nameof(request)),
        };
    }

    /// <summary>True when <paramref name="document"/> shows what <paramref name="request"/> asks for (the shell selects it).</summary>
    public static bool Matches(GroupPolicyDocumentBase document, GroupPolicyDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        return (document, request.Kind) switch
        {
            (GroupPolicyObjectsDocumentViewModel, GroupPolicyDocumentKind.Objects) => true,
            (WmiFiltersDocumentViewModel, GroupPolicyDocumentKind.WmiFilters) => true,
            (GroupPolicyObjectDocumentViewModel gpo, GroupPolicyDocumentKind.Object) => gpo.GpoId == request.GpoId,
            (AdministrativeTemplatesEditorViewModel editor, GroupPolicyDocumentKind.AdministrativeTemplates) => editor.GpoId == request.GpoId,
            (SecuritySettingsEditorViewModel editor, GroupPolicyDocumentKind.SecuritySettings) => editor.GpoId == request.GpoId,
            (GroupPolicyModelingDocumentViewModel model, GroupPolicyDocumentKind.Modeling) => model.Matches(request),
            (GroupPolicyScopeDocumentViewModel scope, GroupPolicyDocumentKind.Scope) =>
                request.ContainerDn is { } dn && Core.Ldap.DistinguishedName.AreEqual(scope.ContainerDn, dn),
            _ => false,
        };
    }
}
