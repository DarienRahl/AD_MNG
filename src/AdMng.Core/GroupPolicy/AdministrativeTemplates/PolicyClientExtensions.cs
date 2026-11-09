namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// K7 (docs/dev/k-computers-and-gpo.md 21.5): the ADMX <c>clientExtension</c> GUIDs the configured policies of one
/// Registry.pol half need in <c>gPC*ExtensionNames</c> – the logic of the K3 editor's <c>ClientExtensions</c>, moved to
/// Core (the editor keeps its copy in K7). Pure.
/// </summary>
public static class PolicyClientExtensions
{
    /// <summary>
    /// The extensions that the policies of <paramref name="scope"/> configured in <paramref name="document"/> (enabled,
    /// disabled or inconsistent) name on the policy or on one of its elements. The Registry extension itself is not
    /// excluded here; the caller adds it with the administrative templates tool anyway.
    /// </summary>
    public static IReadOnlyCollection<Guid> Of(RegistryPolicyDocument document, PolicyDefinitionSet set, PolicyClass scope,
        PolicyOwnershipIndex ownership)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(ownership);
        var result = new HashSet<Guid>();
        if (document.Entries.Count == 0)
        {
            return result;
        }

        foreach (var definition in set.Policies.Where(d => d.Policy.AppliesTo(scope)
            && (d.Policy.ClientExtension is not null || d.Policy.Elements.Any(e => e.ClientExtension is not null))))
        {
            var setting = PolicyEngine.Read(definition.Policy, document.View, ownership.OwnedNames);
            if (setting.State == PolicyState.NotConfigured)
            {
                continue;
            }

            if (definition.Policy.ClientExtension is { } extension)
            {
                result.Add(extension);
            }

            result.UnionWith(definition.Policy.Elements.Select(e => e.ClientExtension).OfType<Guid>());
        }

        return result;
    }
}
