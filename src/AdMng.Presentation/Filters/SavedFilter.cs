using AdMng.Core.Filters;
using AdMng.Core.Model;

namespace AdMng.Presentation.Filters;

/// <summary>
/// A filter of the builder saved by name (README U4, "Własne filtry"): usable as the extra filter of the account review,
/// as a chip on the object lists (also inside saved views) and as a "mark as privileged" rule. Text only - names,
/// attribute names, operators, typed values and group DNs in the portable <c>{domain}</c> form; never directory data.
/// </summary>
public sealed record SavedFilter
{
    public string Id { get; init; } = NewId();

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public FilterDefinition Definition { get; init; } = new();

    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// Several saved filters applied together (AND): what the server evaluates and what the client checks afterwards
/// (constructed attributes).
/// </summary>
/// <param name="ServerFilter">The AND of the server parts; null when no filter narrows down on the server.</param>
/// <param name="ClientChecks">Definitions the client must evaluate on every object (with <see cref="Context"/>).</param>
/// <param name="Attributes">Attributes the client checks read.</param>
/// <param name="Context">Reference time, naming context and schema of the checks.</param>
public sealed record SavedFilterPlan(string? ServerFilter, IReadOnlyList<FilterDefinition> ClientChecks, IReadOnlyList<string> Attributes, FilterContext Context)
{
    /// <summary>The objects need a client check.</summary>
    public bool NeedsClientCheck => ClientChecks.Count > 0;

    /// <summary>The plan of the definitions; throws <see cref="FilterDefinitionException"/> for an invalid one.</summary>
    public static SavedFilterPlan Create(IEnumerable<FilterDefinition> definitions, FilterContext context)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(context);
        var servers = new List<string>();
        var clients = new List<FilterDefinition>();
        var attributes = new List<string>();
        foreach (var definition in definitions)
        {
            var plan = FilterCompiler.Plan(definition, context);
            if (plan.ServerFilter is { } server)
            {
                servers.Add(server);
            }

            if (plan.NeedsClientCheck)
            {
                clients.Add(definition);
                attributes.AddRange(plan.Attributes);
            }
        }

        return new SavedFilterPlan(
            servers.Count == 0 ? null : Core.Ldap.LdapFilter.And(servers),
            clients,
            [.. attributes.Distinct(StringComparer.OrdinalIgnoreCase)],
            context);
    }

    /// <summary>Whether the object passes the client checks.</summary>
    /// <param name="obj">The object (with <see cref="Attributes"/>).</param>
    /// <param name="isMemberOf">Nested membership, when the caller knows it (null = direct memberOf).</param>
    public bool Matches(DirectoryObject obj, Func<string, bool>? isMemberOf = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return ClientChecks.All(d => FilterEvaluator.Matches(d, obj, Context, isMemberOf));
    }
}

/// <summary>A saved filter as a toggleable chip above an object list.</summary>
public sealed partial class SavedFilterChipViewModel(string id, string title, string? description) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    /// <summary>The filter's description (tooltip), empty without one.</summary>
    public string Description { get; } = description ?? string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool IsActive { get; set; }
}
