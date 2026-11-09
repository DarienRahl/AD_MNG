using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.GroupPolicy.Search;

// K7 contract (docs/dev/k-computers-and-gpo.md 21.3, 21.11) – implementation: W1.

/// <summary>How far one GPO of the index was read.</summary>
public enum GpoIndexStatus
{
    Read,

    /// <summary>A budget left content files unread, or a settings sink overflowed: some settings are missing.</summary>
    Incomplete,

    NotFound,
    AccessDenied,
    TooLarge,
    Failed,

    /// <summary>No SYSVOL connection, or the run budget was used up.</summary>
    NotRead,
}

/// <summary>Stages of <see cref="GpoSettingsIndexBuilder.BuildAsync"/>.</summary>
public enum GpoIndexStage { Directory, Versions, Folders, Principals, Units, Completed }

/// <summary>Progress of an index build (counts only).</summary>
public readonly record struct GpoIndexProgress(GpoIndexStage Stage, int Done = 0, int Total = 0);

/// <summary>One occurrence of a unit in one GPO.</summary>
public readonly record struct GpoUnitRef(Guid GpoId, GpoSettingUnit Unit)
{
    /// <summary>For diagnostics: the GPO GUID and the unit's kind only.</summary>
    public override string ToString() => SysvolPaths.FolderName(GpoId) + " " + Unit?.Kind;
}

/// <summary>One GPO of the index: directory data, the parsed content and its units (no raw bytes; in memory only).</summary>
public sealed record GpoIndexEntry
{
    public required GpoInfo Gpo { get; init; }

    public required GpoIndexStatus Status { get; init; }

    /// <summary>Links from the domain head, OUs and sites of this domain.</summary>
    public IReadOnlyList<GpoLinkInfo> Links { get; init; } = [];

    public IReadOnlyList<GpoSetting> Settings { get; init; } = [];

    public RegistryPolicyDocument? MachineRegistry { get; init; }

    public RegistryPolicyDocument? UserRegistry { get; init; }

    public ConfiguredPolicies ComputerPolicies { get; init; } = ConfiguredPolicies.None;

    public ConfiguredPolicies UserPolicies { get; init; } = ConfiguredPolicies.None;

    public IReadOnlyList<GpoSettingUnit> Units { get; init; } = [];

    /// <summary>Content files a budget kept out.</summary>
    public IReadOnlyList<string> NotReadFiles { get; init; } = [];

    /// <summary>Files whose settings overflowed (<see cref="GpoSettingsRead.OverflowedFiles"/>).</summary>
    public IReadOnlyList<string> OverflowedFiles { get; init; } = [];

    /// <summary>Inventory files below Machine/ or User/ no parser reads (<see cref="GpoFolderReadResult.OtherFiles"/>).</summary>
    public IReadOnlyList<string> OtherFiles { get; init; } = [];

    /// <summary>Objects below the GPO's CN=Machine / CN=User (settings kept in the directory).</summary>
    public int DirectorySettings { get; init; }

    public bool IsLinked => Links.Count > 0;

    /// <summary>For diagnostics: GUID, status and counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{SysvolPaths.FolderName(Gpo.Id)} {Status} ({Units.Count} units, {Links.Count} links)");
}

/// <summary>
/// The settings of every GPO of the domain the session can read, grouped into units (docs/dev/k-computers-and-gpo.md
/// 21.3). Built per connection window and kept in its memory only (CLAUDE.md rule 1); never written anywhere.
/// </summary>
public sealed class GpoSettingsIndex
{
    /// <summary>The DC the index was read from (rule 4).</summary>
    public required string Server { get; init; }

    public required string DomainDn { get; init; }

    public required string DomainDnsName { get; init; }

    public string? DomainSid { get; init; }

    public long Generation { get; init; }

    public DateTimeOffset BuiltAt { get; init; }

    /// <summary>The ADMX set the units were built with (copy, remove and merge plan with the same set).</summary>
    public PolicyDefinitionSet? Definitions { get; init; }

    /// <summary>False: no SYSVOL connection, every entry is <see cref="GpoIndexStatus.NotRead"/>.</summary>
    public bool SysvolRead { get; init; }

    public required IReadOnlyList<GpoIndexEntry> Entries { get; init; }

    /// <summary>The linking containers (domain head, OUs, sites).</summary>
    public IReadOnlyList<GroupPolicyContainerInfo> Containers { get; init; } = [];

    /// <summary>Other domains of the forest (their links to these GPOs are not visible from this DC).</summary>
    public IReadOnlyList<string> OtherDomains { get; init; } = [];

    /// <summary>The principal names resolved before the units were built (<see cref="GpoUnitContext.SidsByName"/>).</summary>
    public IReadOnlyDictionary<string, string> SidsByName { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public GpoIndexEntry? Find(Guid gpoId) => throw new NotImplementedException("K7 W1: GpoSettingsIndex.Find");

    /// <summary>Every <see cref="GpoSettingUnit.BaseId"/> of the index (case-insensitive).</summary>
    public IReadOnlyCollection<string> BaseIds => throw new NotImplementedException("K7 W1: GpoSettingsIndex.BaseIds");

    /// <summary>Every occurrence of a base id, in every GPO.</summary>
    public IReadOnlyList<GpoUnitRef> Occurrences(string baseId) => throw new NotImplementedException("K7 W1: GpoSettingsIndex.Occurrences");

    /// <summary>The units that name a principal (SID, upper case).</summary>
    public IReadOnlyList<GpoUnitRef> WithPrincipal(string sid) => throw new NotImplementedException("K7 W1: GpoSettingsIndex.WithPrincipal");

    /// <summary>Re-builds units and configured policies from the kept Registry.pol documents and <see cref="SidsByName"/>, without I/O.</summary>
    public GpoSettingsIndex WithDefinitions(PolicyDefinitionSet? definitions) => throw new NotImplementedException("K7 W1: GpoSettingsIndex.WithDefinitions");

    /// <summary>For diagnostics: counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Entries.Count} GPOs, generation {Generation}");
}

/// <summary>What <see cref="GpoSettingsIndexBuilder.BuildAsync"/> builds.</summary>
public sealed record GpoSettingsIndexRequest
{
    public PolicyDefinitionSet? Definitions { get; init; }

    /// <summary>Incremental refresh: entries whose GPO versions did not change are reused (same server, same set).</summary>
    public GpoSettingsIndex? Previous { get; init; }

    public GpoFolderReadOptions Folders { get; init; } = GpoFolderReadOptions.Settings;

    public long Generation { get; init; }

    /// <summary>For diagnostics: whether it is incremental.</summary>
    public override string ToString() => $"generation {Generation}, incremental: {Previous is not null}";
}

/// <summary>Builds the settings index on the session's DC and its SYSVOL (21.3).</summary>
public static class GpoSettingsIndexBuilder
{
    /// <summary>
    /// Directory reads, GPT.INI versions, the streaming content-only folder read, one principal batch, then the units.
    /// Without <paramref name="sysvol"/> every entry is <see cref="GpoIndexStatus.NotRead"/>.
    /// </summary>
    public static Task<GpoSettingsIndex> BuildAsync(IDirectorySession session, ISysvolWriteConnection? sysvol,
        GpoSettingsIndexRequest request, IProgress<GpoIndexProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotImplementedException("K7 W1: GpoSettingsIndexBuilder.BuildAsync");
}
