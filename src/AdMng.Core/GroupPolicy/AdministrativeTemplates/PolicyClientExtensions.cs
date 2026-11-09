namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// K7 contract (docs/dev/k-computers-and-gpo.md 21.5) – implementation: W2. The ADMX <c>clientExtension</c> GUIDs the
/// configured policies of one Registry.pol half need in <c>gPC*ExtensionNames</c> (the logic of the K3 editor's
/// <c>ClientExtensions</c>, moved to Core; the editor keeps its copy in K7).
/// </summary>
public static class PolicyClientExtensions
{
    public static IReadOnlyCollection<Guid> Of(RegistryPolicyDocument document, PolicyDefinitionSet set, PolicyClass scope,
        PolicyOwnershipIndex ownership) =>
        throw new NotImplementedException("K7 W2: PolicyClientExtensions.Of");
}
