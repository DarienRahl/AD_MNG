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

/// <summary>Why a template file of the store could not be read (as opposed to a malformed file).</summary>
public enum PolicyFileReadProblem
{
    /// <summary>The read failed (SMB error, access denied, the file vanished between the listing and the read).</summary>
    ReadFailed,

    /// <summary>The file is larger than the editor reads (<see cref="PolicyDefinitionLoader.MaxFileBytes"/>).</summary>
    TooLarge,
}

/// <summary>Loaded templates: the set, where it came from and how many files it has.</summary>
/// <param name="Set">The definitions.</param>
/// <param name="Origin">Central store, local store or none.</param>
/// <param name="Location">The DC (central store) or the folder (local store).</param>
/// <param name="FileCount">ADMX files read.</param>
public sealed record PolicyDefinitionsResult(PolicyDefinitionSet Set, PolicyDefinitionOrigin Origin, string Location, int FileCount)
{
    /// <summary>
    /// The files of the store (relative paths) that could not be read or are too large: the reason of their
    /// <see cref="PolicyDefinitionIssueKind.FileUnreadable"/> / <see cref="PolicyDefinitionIssueKind.LanguageUnreadable"/>
    /// issue. A file of such an issue that is not listed here is malformed.
    /// </summary>
    public IReadOnlyDictionary<string, PolicyFileReadProblem> FileProblems { get; init; } = new Dictionary<string, PolicyFileReadProblem>();

    /// <summary>A read failed while loading: the set may miss templates by chance (it is never cached).</summary>
    public bool HasReadFailures => FileProblems.Values.Any(p => p == PolicyFileReadProblem.ReadFailed);
}

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
/// language and kept in memory for the window (templates are vendor files, not directory data; nothing is written). A load
/// in which a file could not be read (a transient SMB failure) is returned but not cached – the next load reads the store
/// again –, and a load that started before <see cref="Invalidate"/> never refills the cache.
/// </summary>
public sealed class PolicyDefinitionsService(ILocalPolicyDefinitions? local = null)
{
    private readonly ILocalPolicyDefinitions _local = local ?? new WindowsLocalPolicyDefinitions();
    private readonly Dictionary<string, PolicyDefinitionsResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private long _generation;

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
        long generation;
        lock (_gate)
        {
            generation = _generation;
            if (!reload && _cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var cultures = PolicyDefinitionLoader.CulturesFor(culture);
        PolicyDefinitionsResult result;
        var central = sysvol is null ? null : new ReadTrackingSource(new SysvolPolicyDefinitionSource(sysvol, session.Info.Server));
        var (centralSet, exists) = central is null
            ? (PolicyDefinitionSet.Empty, false)
            : await Task.Run(() => PolicyDefinitionLoader.LoadAsync(central, cultures, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (central is not null && exists)
        {
            result = new PolicyDefinitionsResult(centralSet, PolicyDefinitionOrigin.CentralStore, session.Info.Server, centralSet.FileCount)
            {
                FileProblems = central.Problems,
            };
        }
        else if (_local.Source is { } source)
        {
            var tracked = new ReadTrackingSource(source);
            var (localSet, localExists) = await Task.Run(() => PolicyDefinitionLoader.LoadAsync(tracked, cultures, cancellationToken), cancellationToken).ConfigureAwait(false);
            result = localExists
                ? new PolicyDefinitionsResult(localSet, PolicyDefinitionOrigin.LocalStore, _local.Folder ?? string.Empty, localSet.FileCount) { FileProblems = tracked.Problems }
                : new PolicyDefinitionsResult(PolicyDefinitionSet.Empty, PolicyDefinitionOrigin.None, string.Empty, 0);
        }
        else
        {
            result = new PolicyDefinitionsResult(PolicyDefinitionSet.Empty, PolicyDefinitionOrigin.None, string.Empty, 0);
        }

        lock (_gate)
        {
            // Not kept: a set that misses templates because a read failed (the next load tries again), or one read before
            // the store was updated or reloaded (Invalidate ran meanwhile).
            if (!result.HasReadFailures && generation == _generation)
            {
                _cache[key] = result;
            }
        }

        return result;
    }

    /// <summary>Forgets every loaded store (after the central store was updated, "Wczytaj szablony ponownie").</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _cache.Clear();
        }
    }

    /// <summary>
    /// A store that remembers which of its files could not be read: a read without content of a file listed within the
    /// size limit (a failure), and the files listed larger than the loader reads.
    /// </summary>
    private sealed class ReadTrackingSource(IPolicyDefinitionSource inner) : IPolicyDefinitionSource
    {
        private readonly Dictionary<string, long> _lengths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PolicyFileReadProblem> _problems = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _gate = new();

        public string Description => inner.Description;

        public IReadOnlyDictionary<string, PolicyFileReadProblem> Problems
        {
            get
            {
                lock (_gate)
                {
                    return new Dictionary<string, PolicyFileReadProblem>(_problems, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public async Task<IReadOnlyList<PolicyDefinitionFile>?> ListAsync(CancellationToken cancellationToken)
        {
            var files = await inner.ListAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var file in files ?? [])
                {
                    _lengths[file.RelativePath] = file.Length;
                    if (file.Length > PolicyDefinitionLoader.MaxFileBytes)
                    {
                        _problems[file.RelativePath] = PolicyFileReadProblem.TooLarge;
                    }
                }
            }

            return files;
        }

        public async Task<byte[]?> ReadAsync(string relativePath, long maxBytes, CancellationToken cancellationToken)
        {
            var content = await inner.ReadAsync(relativePath, maxBytes, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                lock (_gate)
                {
                    _problems[relativePath] = _lengths.TryGetValue(relativePath, out var length) && length > maxBytes
                        ? PolicyFileReadProblem.TooLarge
                        : PolicyFileReadProblem.ReadFailed;
                }
            }

            return content;
        }
    }
}
