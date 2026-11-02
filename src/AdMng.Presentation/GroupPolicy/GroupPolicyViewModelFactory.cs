using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>
/// Creates the Group Policy documents of a connection window (scoped, design note 7): every document gets the window's
/// <see cref="GroupPolicyServices"/> and <see cref="GroupPolicyActions"/>. Closing a tab disposes its document.
/// </summary>
public sealed class GroupPolicyViewModelFactory(GroupPolicyServices services, GroupPolicyActions actions, PolicyDefinitionsService? definitions = null)
{
    public GroupPolicyServices Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    public GroupPolicyActions Actions { get; } = actions ?? throw new ArgumentNullException(nameof(actions));

    /// <summary>The ADMX templates of the window (K3).</summary>
    public PolicyDefinitionsService Definitions { get; } = definitions ?? new PolicyDefinitionsService();

    /// <summary>The document a request names (not loaded yet: call <see cref="GroupPolicyDocumentBase.LoadAsync"/>).</summary>
    public GroupPolicyDocumentBase Create(GroupPolicyDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Kind switch
        {
            GroupPolicyDocumentKind.Object when request.GpoId is { } id =>
                new GroupPolicyObjectDocumentViewModel(Services, Actions, id, request.Name, request.Tab ?? GpoDocumentTab.Scope),
            GroupPolicyDocumentKind.Scope when request.ContainerDn is { Length: > 0 } dn =>
                new GroupPolicyScopeDocumentViewModel(Services, Actions, dn, request.Name),
            GroupPolicyDocumentKind.WmiFilters => new WmiFiltersDocumentViewModel(Services, Actions),
            GroupPolicyDocumentKind.AdministrativeTemplates when request.GpoId is { } templatesId =>
                new AdministrativeTemplatesEditorViewModel(Services, Actions, Definitions, templatesId, request.Name),
            GroupPolicyDocumentKind.Objects => new GroupPolicyObjectsDocumentViewModel(Services, Actions),
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
            (GroupPolicyScopeDocumentViewModel scope, GroupPolicyDocumentKind.Scope) =>
                request.ContainerDn is { } dn && Core.Ldap.DistinguishedName.AreEqual(scope.ContainerDn, dn),
            _ => false,
        };
    }
}
