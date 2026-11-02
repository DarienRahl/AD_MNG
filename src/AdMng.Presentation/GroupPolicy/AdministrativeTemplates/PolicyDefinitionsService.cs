using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

/// <summary>Where the ADMX templates of the editor come from.</summary>
public enum PolicyDefinitionOrigin
{
    /// <summary>The central store on SYSVOL of the session's DC.</summary>
    CentralStore,

    /// <summary>No central store: the templates of the operator's computer (the Microsoft editor's fallback).</summary>
    LocalStore,

    /// <summary>Neither: the editor shows Registry.pol values only.</summary>
    None,
}

/// <summary>Loaded templates: the set, where it came from and how many files it has.</summary>
/// <param name="Set">The definitions.</param>
/// <param name="Origin">Central store, local store or none.</param>
/// <param name="Location">The DC (central store) or the folder (local store).</param>
/// <param name="FileCount">ADMX files read.</param>
public sealed record PolicyDefinitionsResult(PolicyDefinitionSet Set, PolicyDefinitionOrigin Origin, string Location, int FileCount);

/// <summary>The templates of the operator's computer (<c>%WINDIR%\PolicyDefinitions</c>); replaced in tests.</summary>
public interface ILocalPolicyDefinitions
{
    /// <summary>The folder (null when the platform has none).</summary>
    string? Folder { get; }

    /// <summary>The store (null when the platform has none).</summary>
    IPolicyDefinitionSource? Source { get; }
}

/// <summary><c>%WINDIR%\PolicyDefinitions</c> of the operator's computer (none on other platforms).</summary>
public sealed class WindowsLocalPolicyDefinitions : ILocalPolicyDefinitions
{
    public WindowsLocalPolicyDefinitions()
    {
        var windows = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : string.Empty;
        Folder = windows.Length == 0 ? null : Path.Combine(windows, SysvolPaths.PolicyDefinitionsFolder);
        Source = Folder is null ? null : new FolderPolicyDefinitionSource(Folder, Folder);
    }

    public string? Folder { get; }

    public IPolicyDefinitionSource? Source { get; }
}

/// <summary>
/// The ADMX templates of a connection window (scoped, K3): the central store of the session's DC when it exists (CLAUDE.md
/// rule 4), otherwise the templates of the operator's computer, as the Microsoft editor does. Parsed once per DC and
/// language and kept in memory for the window (templates are vendor files, not directory data; nothing is written).
/// </summary>
public sealed class PolicyDefinitionsService(ILocalPolicyDefinitions? local = null)
{
    private readonly ILocalPolicyDefinitions _local = local ?? new WindowsLocalPolicyDefinitions();
    private readonly Dictionary<string, PolicyDefinitionsResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>The templates of <paramref name="session"/>'s DC in <paramref name="culture"/> (cached unless <paramref name="reload"/>).</summary>
    /// <param name="session">The window's session.</param>
    /// <param name="sysvol">SYSVOL of the session's DC (null: unavailable – the local store is used).</param>
    /// <param name="culture">The language of the texts.</param>
    /// <param name="reload">Read the files again.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    public async Task<PolicyDefinitionsResult> GetAsync(IDirectorySession session, ISysvolConnection? sysvol, CultureInfo culture, bool reload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(culture);
        var key = session.Info.Server + "|" + culture.Name + "|" + (sysvol is null ? "local" : "sysvol");
        lock (_gate)
        {
            if (!reload && _cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var cultures = PolicyDefinitionLoader.CulturesFor(culture);
        PolicyDefinitionsResult result;
        var (central, exists) = sysvol is null
            ? (PolicyDefinitionSet.Empty, false)
            : await Task.Run(() => PolicyDefinitionLoader.LoadAsync(new SysvolPolicyDefinitionSource(sysvol, session.Info.Server), cultures, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            result = new PolicyDefinitionsResult(central, PolicyDefinitionOrigin.CentralStore, session.Info.Server, central.FileCount);
        }
        else if (_local.Source is { } source)
        {
            var (localSet, localExists) = await Task.Run(() => PolicyDefinitionLoader.LoadAsync(source, cultures, cancellationToken), cancellationToken).ConfigureAwait(false);
            result = localExists
                ? new PolicyDefinitionsResult(localSet, PolicyDefinitionOrigin.LocalStore, _local.Folder ?? string.Empty, localSet.FileCount)
                : new PolicyDefinitionsResult(PolicyDefinitionSet.Empty, PolicyDefinitionOrigin.None, string.Empty, 0);
        }
        else
        {
            result = new PolicyDefinitionsResult(PolicyDefinitionSet.Empty, PolicyDefinitionOrigin.None, string.Empty, 0);
        }

        lock (_gate)
        {
            _cache[key] = result;
        }

        return result;
    }

    /// <summary>Forgets every loaded store (after the central store was updated).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }
}
