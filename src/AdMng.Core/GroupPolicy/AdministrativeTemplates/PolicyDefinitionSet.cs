using System.Globalization;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>What is wrong with a file of a policy definition store.</summary>
public enum PolicyDefinitionIssueKind
{
    /// <summary>An ADMX file cannot be read or parsed; its policies are missing.</summary>
    FileUnreadable,

    /// <summary>No ADML of the requested languages (or the file's fallback): names and texts show as their ids.</summary>
    LanguageMissing,

    /// <summary>The ADML file cannot be read or parsed: names and texts show as their ids.</summary>
    LanguageUnreadable,

    /// <summary>The ADML is older than the ADMX needs (<c>minRequiredRevision</c>); some texts may be missing.</summary>
    LanguageOutdated,

    /// <summary>Two ADMX files declare the same target namespace: the second is ignored (as the Microsoft editor does).</summary>
    NamespaceCollision,

    /// <summary>A category or policy name repeats within its namespace: the first definition wins.</summary>
    Duplicate,

    /// <summary>A parent category is not defined by any file of the store: the category or policy shows at the root.</summary>
    UnknownCategory,

    /// <summary>Categories refer to each other in a loop: the loop is cut at the root.</summary>
    CategoryLoop,

    /// <summary>The store holds more files than are read.</summary>
    TooManyFiles,

    /// <summary>
    /// Categories nest deeper than <see cref="PolicyDefinitionSet.MaxCategoryDepth"/> (real stores nest about five levels):
    /// the category is shown at the root.
    /// </summary>
    CategoryTooDeep,
}

/// <summary>A problem of one file of the store (shown in the editor; never fatal for the other files).</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="FileName">The file (ADMX name, or <c>culture/name.adml</c>).</param>
/// <param name="Detail">The name involved (a namespace, a category reference), or null.</param>
public sealed record PolicyDefinitionIssue(PolicyDefinitionIssueKind Kind, string FileName, string? Detail = null);

/// <summary>A category of the editor tree (resolved texts, children and policies sorted by name).</summary>
public sealed class PolicyCategoryNode
{
    private readonly List<PolicyCategoryNode> _children = [];
    private readonly List<PolicyDefinition> _policies = [];

    internal PolicyCategoryNode(AdmxCategory category, string displayName, string? explainText, string fileName)
    {
        Category = category;
        DisplayName = displayName;
        ExplainText = explainText;
        FileName = fileName;
    }

    public AdmxCategory Category { get; }

    /// <summary><c>namespace:name</c>.</summary>
    public string Id => Category.Id;

    public string DisplayName { get; }

    public string? ExplainText { get; }

    /// <summary>The ADMX file that defines the category.</summary>
    public string FileName { get; }

    public PolicyCategoryNode? Parent { get; internal set; }

    public IReadOnlyList<PolicyCategoryNode> Children => _children;

    public IReadOnlyList<PolicyDefinition> Policies => _policies;

    /// <summary>The category or a category below it holds a policy of <paramref name="scope"/>.</summary>
    public bool HasPolicies(PolicyClass scope) =>
        _policies.Any(p => p.Policy.AppliesTo(scope)) || _children.Any(c => c.HasPolicies(scope));

    /// <summary>The names from the root, e.g. "Składniki systemu Windows / Windows Update".</summary>
    public IReadOnlyList<string> PathNames()
    {
        var names = new List<string>();
        for (var node = this; node is not null; node = node.Parent)
        {
            names.Insert(0, node.DisplayName);
        }

        return names;
    }

    internal void AddChild(PolicyCategoryNode child) => _children.Add(child);

    internal void RemoveChild(PolicyCategoryNode child) => _children.Remove(child);

    internal void AddPolicy(PolicyDefinition policy) => _policies.Add(policy);

    internal void Sort(StringComparer comparer)
    {
        _children.Sort((a, b) => comparer.Compare(a.DisplayName, b.DisplayName));
        _policies.Sort((a, b) => comparer.Compare(a.DisplayName, b.DisplayName));
        foreach (var child in _children)
        {
            child.Sort(comparer);
        }
    }

    public override string ToString() => Id;
}

/// <summary>A policy with its texts in the chosen language.</summary>
public sealed class PolicyDefinition
{
    private readonly Func<string?, string> _text;

    internal PolicyDefinition(AdmxPolicy policy, string fileName, Func<string?, string> text, PolicyPresentation? presentation, string? supportedOn)
    {
        Policy = policy;
        FileName = fileName;
        _text = text;
        DisplayName = text(policy.DisplayName);
        ExplainText = policy.ExplainText is null ? string.Empty : text(policy.ExplainText);
        Presentation = presentation;
        SupportedOnText = supportedOn;
    }

    public AdmxPolicy Policy { get; }

    /// <summary><c>namespace:name</c>.</summary>
    public string Id => Policy.Id;

    /// <summary>The ADMX file that defines the policy.</summary>
    public string FileName { get; }

    public string DisplayName { get; }

    public string ExplainText { get; }

    /// <summary>The policy's dialog; null when it has none (or its ADML lacks it: the editor then lists the elements).</summary>
    public PolicyPresentation? Presentation { get; }

    /// <summary>"Co najmniej Windows 10" – the supportedOn definition's text; null when not stated or unknown.</summary>
    public string? SupportedOnText { get; }

    public PolicyCategoryNode? Category { get; internal set; }

    /// <summary>The text of a <c>$(string.id)</c> reference of this policy's file (an enum item's name).</summary>
    public string Text(string? reference) => _text(reference);

    public override string ToString() => Id;
}

/// <summary>
/// The policy definitions of a store in one language (design note docs/dev/k-computers-and-gpo.md 6, K3): the parsed ADMX
/// files with their ADML (first of the requested cultures, then the file's fallback culture, then en-US), namespaces and
/// cross-file references resolved (<c>windows:WindowsComponents</c>), texts resolved, categories and policies sorted by name
/// in the culture of the texts. Problems of single files are listed in <see cref="Issues"/>; nothing of one file stops the
/// others. Immutable once built; read on a background thread.
/// </summary>
public sealed class PolicyDefinitionSet
{
    /// <summary>Deepest category nesting kept (Windows, Edge, Office and browser templates nest about five levels).</summary>
    public const int MaxCategoryDepth = 32;

    private readonly Dictionary<string, PolicyDefinition> _policies;

    private PolicyDefinitionSet(IReadOnlyList<PolicyCategoryNode> roots, Dictionary<string, PolicyDefinition> policies,
        IReadOnlyList<PolicyDefinition> uncategorized, IReadOnlyList<PolicyDefinitionIssue> issues, int fileCount)
    {
        Roots = roots;
        _policies = policies;
        Uncategorized = uncategorized;
        Issues = issues;
        FileCount = fileCount;
    }

    /// <summary>An empty set (no store).</summary>
    public static PolicyDefinitionSet Empty { get; } = new([], new Dictionary<string, PolicyDefinition>(StringComparer.OrdinalIgnoreCase), [], [], 0);

    /// <summary>The root categories ("Składniki systemu Windows", "Microsoft Edge" …).</summary>
    public IReadOnlyList<PolicyCategoryNode> Roots { get; }

    /// <summary>Every policy (any order).</summary>
    public IEnumerable<PolicyDefinition> Policies => _policies.Values;

    /// <summary>Policies whose category is unknown (shown at the root).</summary>
    public IReadOnlyList<PolicyDefinition> Uncategorized { get; }

    public IReadOnlyList<PolicyDefinitionIssue> Issues { get; }

    /// <summary>ADMX files read.</summary>
    public int FileCount { get; }

    public int PolicyCount => _policies.Count;

    public PolicyDefinition? Find(string policyId) => policyId is not null && _policies.TryGetValue(policyId, out var policy) ? policy : null;

    /// <summary>The policies that apply to <paramref name="scope"/> (computer or user configuration).</summary>
    public IEnumerable<PolicyDefinition> PoliciesOf(PolicyClass scope) => _policies.Values.Where(p => p.Policy.AppliesTo(scope));

    /// <summary>Builds the set from parsed files: each ADMX with the ADML chosen for it (null: none).</summary>
    /// <param name="files">The ADMX files with their ADML.</param>
    /// <param name="loadIssues">Problems found while reading the files (kept first).</param>
    /// <param name="culture">The culture of the texts (sorting).</param>
    public static PolicyDefinitionSet Build(IReadOnlyList<(AdmxFile Admx, AdmlFile? Adml)> files, IReadOnlyList<PolicyDefinitionIssue> loadIssues, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(loadIssues);
        ArgumentNullException.ThrowIfNull(culture);
        var issues = new List<PolicyDefinitionIssue>(loadIssues);

        // One file per target namespace (the Microsoft editor refuses the second): files in name order, first wins.
        var byNamespace = new Dictionary<string, (AdmxFile Admx, AdmlFile? Adml)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.OrderBy(f => f.Admx.FileName, StringComparer.OrdinalIgnoreCase))
        {
            if (!byNamespace.TryAdd(file.Admx.TargetNamespace, file))
            {
                issues.Add(new PolicyDefinitionIssue(PolicyDefinitionIssueKind.NamespaceCollision, file.Admx.FileName, file.Admx.TargetNamespace));
            }
        }

        // supportedOn texts: namespace:name → text (resolved in the defining file's language).
        var supportedOn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (admx, adml) in byNamespace.Values)
        {
            foreach (var definition in admx.SupportedOn)
            {
                supportedOn.TryAdd(admx.TargetNamespace + ":" + definition.Name, Resolve(adml, definition.DisplayName));
            }
        }

        // Categories.
        var categories = new Dictionary<string, (PolicyCategoryNode Node, AdmxFile File)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (admx, adml) in byNamespace.Values)
        {
            foreach (var category in admx.Categories)
            {
                var node = new PolicyCategoryNode(category, Resolve(adml, category.DisplayName),
                    category.ExplainText is null ? null : Resolve(adml, category.ExplainText), admx.FileName);
                if (!categories.TryAdd(category.Id, (node, admx)))
                {
                    issues.Add(new PolicyDefinitionIssue(PolicyDefinitionIssueKind.Duplicate, admx.FileName, category.Name));
                }
            }
        }

        var roots = new List<PolicyCategoryNode>();
        foreach (var (node, file) in categories.Values)
        {
            var parentId = Qualified(file, node.Category.ParentCategory);
            if (node.Category.ParentCategory is null)
            {
                roots.Add(node);
            }
            else if (parentId is not null && categories.TryGetValue(parentId, out var parent) && !WouldLoop(node, parent.Node))
            {
                node.Parent = parent.Node;
                parent.Node.AddChild(node);
            }
            else
            {
                issues.Add(new PolicyDefinitionIssue(
                    parentId is not null && categories.ContainsKey(parentId) ? PolicyDefinitionIssueKind.CategoryLoop : PolicyDefinitionIssueKind.UnknownCategory,
                    file.FileName, node.Category.ParentCategory));
                roots.Add(node);
            }
        }

        // Depth: categories chained through files could nest without limit (and the tree is walked recursively); below
        // MaxCategoryDepth a category starts a new root.
        var queue = new Queue<(PolicyCategoryNode Node, int Depth)>(roots.Select(r => (r, 1)));
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            foreach (var child in node.Children.ToList())
            {
                if (depth + 1 > MaxCategoryDepth)
                {
                    node.RemoveChild(child);
                    child.Parent = null;
                    roots.Add(child);
                    issues.Add(new PolicyDefinitionIssue(PolicyDefinitionIssueKind.CategoryTooDeep, child.FileName, child.Category.Name));
                    queue.Enqueue((child, 1));
                }
                else
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        // Policies.
        var policies = new Dictionary<string, PolicyDefinition>(StringComparer.OrdinalIgnoreCase);
        var uncategorized = new List<PolicyDefinition>();
        foreach (var (admx, adml) in byNamespace.Values)
        {
            string Text(string? reference) => Resolve(adml, reference);
            foreach (var policy in admx.Policies)
            {
                var presentation = policy.Presentation is { } id && adml is not null && adml.Presentations.TryGetValue(id, out var found) ? found : null;
                var supported = Qualified(admx, policy.SupportedOn) is { } supportedId && supportedOn.TryGetValue(supportedId, out var text) ? text : null;
                var definition = new PolicyDefinition(policy, admx.FileName, Text, presentation, supported);
                if (!policies.TryAdd(policy.Id, definition))
                {
                    issues.Add(new PolicyDefinitionIssue(PolicyDefinitionIssueKind.Duplicate, admx.FileName, policy.Name));
                    continue;
                }

                if (Qualified(admx, policy.ParentCategory) is { } categoryId && categories.TryGetValue(categoryId, out var category))
                {
                    definition.Category = category.Node;
                    category.Node.AddPolicy(definition);
                }
                else
                {
                    issues.Add(new PolicyDefinitionIssue(PolicyDefinitionIssueKind.UnknownCategory, admx.FileName, policy.ParentCategory ?? policy.Name));
                    uncategorized.Add(definition);
                }
            }
        }

        var comparer = StringComparer.Create(culture, ignoreCase: true);
        roots.Sort((a, b) => comparer.Compare(a.DisplayName, b.DisplayName));
        foreach (var root in roots)
        {
            root.Sort(comparer);
        }

        uncategorized.Sort((a, b) => comparer.Compare(a.DisplayName, b.DisplayName));
        return new PolicyDefinitionSet(roots, policies, uncategorized, issues, byNamespace.Count);
    }

    /// <summary>The text of a <c>$(string.id)</c> reference in <paramref name="adml"/>; the id itself when it is not there.</summary>
    public static string Resolve(AdmlFile? adml, string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return string.Empty;
        }

        const string Prefix = "$(string.";
        var trimmed = reference.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith(')'))
        {
            return reference;
        }

        var id = trimmed[Prefix.Length..^1];
        return adml is not null && adml.Strings.TryGetValue(id, out var text) ? text : id;
    }

    /// <summary><c>namespace:name</c> of a reference made in <paramref name="file"/>; null when its prefix is unknown.</summary>
    private static string? Qualified(AdmxFile file, string? reference) =>
        file.Resolve(reference) is { } resolved ? resolved.Namespace + ":" + resolved.Name : null;

    /// <summary>Linking <paramref name="node"/> below <paramref name="parent"/> closes a loop.</summary>
    private static bool WouldLoop(PolicyCategoryNode node, PolicyCategoryNode parent)
    {
        for (var current = parent; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, node))
            {
                return true;
            }
        }

        return false;
    }
}
