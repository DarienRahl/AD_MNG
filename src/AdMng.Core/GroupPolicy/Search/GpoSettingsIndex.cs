using System.Collections.Concurrent;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Diagnostics;
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

    /// <summary>
    /// The <c>Collection</c> chains of the preference items (<see cref="GpoSettingUnits.PreferenceScopes"/>), kept so that
    /// <see cref="GpoSettingsIndex.WithDefinitions"/> rebuilds the units without reading the XML again.
    /// </summary>
    public IReadOnlyDictionary<string, GpoPreferenceScope> PreferenceScopes { get; init; } =
        new Dictionary<string, GpoPreferenceScope>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The content was read (<see cref="GpoIndexStatus.Read"/> or <see cref="GpoIndexStatus.Incomplete"/>).</summary>
    public bool HasContent => Status is GpoIndexStatus.Read or GpoIndexStatus.Incomplete;

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
    private readonly Lock _gate = new();
    private Lookups? _lookups;

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

    public GpoIndexEntry? Find(Guid gpoId) => GetLookups().ById.GetValueOrDefault(gpoId);

    /// <summary>Every <see cref="GpoSettingUnit.BaseId"/> of the index (a case-insensitive set: <c>Contains</c> ignores case).</summary>
    public IReadOnlyCollection<string> BaseIds => GetLookups().BaseIds;

    /// <summary>Every occurrence of a base id, in every GPO (in the order of <see cref="Entries"/>, then of the units).</summary>
    public IReadOnlyList<GpoUnitRef> Occurrences(string baseId)
    {
        ArgumentNullException.ThrowIfNull(baseId);
        return GetLookups().ByBaseId.TryGetValue(baseId, out var refs) ? refs : [];
    }

    /// <summary>The units that name a principal (SID, case-insensitive; <see cref="GpoSettingUnit.PrincipalSids"/>).</summary>
    public IReadOnlyList<GpoUnitRef> WithPrincipal(string sid)
    {
        ArgumentNullException.ThrowIfNull(sid);
        return GetLookups().BySid.TryGetValue(sid.Trim().TrimStart('*'), out var refs) ? refs : [];
    }

    /// <summary>Re-builds units and configured policies from the kept Registry.pol documents and <see cref="SidsByName"/>, without I/O.</summary>
    public GpoSettingsIndex WithDefinitions(PolicyDefinitionSet? definitions) => new()
    {
        Server = Server,
        DomainDn = DomainDn,
        DomainDnsName = DomainDnsName,
        DomainSid = DomainSid,
        Generation = Generation,
        BuiltAt = BuiltAt,
        Definitions = definitions,
        SysvolRead = SysvolRead,
        Entries = [.. Entries.Select(e => GpoSettingsIndexBuilder.WithUnits(e, definitions, DomainSid, SidsByName))],
        Containers = Containers,
        OtherDomains = OtherDomains,
        SidsByName = SidsByName,
    };

    private Lookups GetLookups()
    {
        lock (_gate)
        {
            return _lookups ??= new Lookups(Entries);
        }
    }

    /// <summary>The lookup tables, built once on first use (the index never changes).</summary>
    private sealed class Lookups
    {
        public Lookups(IReadOnlyList<GpoIndexEntry> entries)
        {
            var byBaseId = new Dictionary<string, List<GpoUnitRef>>(StringComparer.OrdinalIgnoreCase);
            var bySid = new Dictionary<string, List<GpoUnitRef>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                ById.TryAdd(entry.Gpo.Id, entry);
                foreach (var unit in entry.Units)
                {
                    var reference = new GpoUnitRef(entry.Gpo.Id, unit);
                    Add(byBaseId, unit.BaseId, reference);
                    foreach (var sid in unit.PrincipalSids.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        Add(bySid, sid, reference);
                    }
                }
            }

            ByBaseId = byBaseId.ToDictionary(p => p.Key, p => (IReadOnlyList<GpoUnitRef>)p.Value, StringComparer.OrdinalIgnoreCase);
            BaseIds = new HashSet<string>(byBaseId.Keys, StringComparer.OrdinalIgnoreCase);
            BySid = bySid.ToDictionary(p => p.Key, p => (IReadOnlyList<GpoUnitRef>)p.Value, StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<Guid, GpoIndexEntry> ById { get; } = [];

        public Dictionary<string, IReadOnlyList<GpoUnitRef>> ByBaseId { get; }

        public HashSet<string> BaseIds { get; }

        public Dictionary<string, IReadOnlyList<GpoUnitRef>> BySid { get; }

        private static void Add(Dictionary<string, List<GpoUnitRef>> map, string key, GpoUnitRef reference)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(reference);
        }
    }

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
    public static async Task<GpoSettingsIndex> BuildAsync(IDirectorySession session, ISysvolWriteConnection? sysvol,
        GpoSettingsIndexRequest request, IProgress<GpoIndexProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var reader = new GroupPolicyReader();
        var domainDn = session.Info.DefaultNamingContext;

        // (1) The directory: GPOs, linking containers (domain head, OUs, sites), other domains, the domain SID, directory settings.
        progress?.Report(new GpoIndexProgress(GpoIndexStage.Directory));
        IReadOnlyList<GpoInfo> gpos = await reader.ReadPoliciesAsync(session, cancellationToken).ConfigureAwait(false);
        var containers = await reader.ReadLinkingContainersAsync(session, cancellationToken).ConfigureAwait(false);
        var links = GroupPolicyReader.LinksByGpo(containers);
        var otherDomains = await reader.ReadOtherDomainsAsync(session, cancellationToken).ConfigureAwait(false);
        var head = await session.GetAsync(domainDn, [Attr.ObjectSid], cancellationToken).ConfigureAwait(false);
        var domainSid = head?.ObjectSid;
        var directorySettings = await reader.CountDirectorySettingsAsync(session, cancellationToken).ConfigureAwait(false);
        var previous = request.Previous is { } old && string.Equals(old.Server, session.Info.Server, StringComparison.OrdinalIgnoreCase)
            && ReferenceEquals(old.Definitions, request.Definitions)
            ? old
            : null;

        // (2) + (3) GPT.INI versions, then the content of the GPOs that need a read, one GPO at a time.
        var contents = new ConcurrentDictionary<Guid, ParsedGpo>();
        if (sysvol is not null)
        {
            progress?.Report(new GpoIndexProgress(GpoIndexStage.Versions, 0, gpos.Count));
            gpos = await reader.WithSysvolVersionsAsync(sysvol, gpos, cancellationToken).ConfigureAwait(false);
            var toRead = new List<GpoFolderRequest>();
            foreach (var gpo in gpos)
            {
                if (previous?.Find(gpo.Id) is { HasContent: true } reusable && Unchanged(reusable.Gpo, gpo))
                {
                    contents[gpo.Id] = ParsedGpo.From(reusable);
                }
                else
                {
                    toRead.Add(new GpoFolderRequest(gpo.Id));
                }
            }

            var folderProgress = progress is null ? null : new FolderProgress(progress);
            folderProgress?.Report(new GpoFolderReadProgress(0, toRead.Count));
            await GpoFolderReader.ReadAsync(sysvol, toRead, request.Folders, (result, token) =>
            {
                // Parse and drop the bytes here: no entry keeps a byte[] (at most Parallelism folders in memory). A folder
                // the parsers trip over is that GPO's failure, never the end of the run (only cancellation stops it).
                try
                {
                    contents[result.GpoId] = ParsedGpo.Parse(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    contents[result.GpoId] = ParsedGpo.Failed;
                }

                return ValueTask.CompletedTask;
            }, folderProgress, cancellationToken).ConfigureAwait(false);
        }

        // (4) The principal names in one batch for the domain (names already resolved for a reused entry are kept).
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var content in contents.Values)
        {
            names.UnionWith(GpoSettingUnits.PrincipalNames(content.Settings, domainSid));
        }

        var sidsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (previous is not null && string.Equals(previous.DomainSid, domainSid, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in names.ToList())
            {
                if (previous.SidsByName.TryGetValue(name, out var sid))
                {
                    sidsByName[name] = sid;
                    names.Remove(name);
                }
            }
        }

        progress?.Report(new GpoIndexProgress(GpoIndexStage.Principals, 0, names.Count));
        if (names.Count > 0)
        {
            foreach (var principal in await GpoPrincipalResolver.ResolveAsync(session, names, domainSid, cancellationToken).ConfigureAwait(false))
            {
                if (principal.Sid is { } sid)
                {
                    sidsByName[principal.Written] = sid;
                }
            }
        }

        progress?.Report(new GpoIndexProgress(GpoIndexStage.Principals, names.Count, names.Count));

        // (5) The units: pure, no I/O.
        var entries = new List<GpoIndexEntry>(gpos.Count);
        var done = 0;
        progress?.Report(new GpoIndexProgress(GpoIndexStage.Units, 0, gpos.Count));
        foreach (var gpo in gpos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = contents.TryGetValue(gpo.Id, out var content) ? content.ToEntry(gpo) : new GpoIndexEntry { Gpo = gpo, Status = GpoIndexStatus.NotRead };
            entry = entry with
            {
                Links = links.GetValueOrDefault(gpo.Id) ?? [],
                DirectorySettings = directorySettings.GetValueOrDefault(gpo.Id),
            };
            entries.Add(WithUnits(entry, request.Definitions, domainSid, sidsByName));
            progress?.Report(new GpoIndexProgress(GpoIndexStage.Units, ++done, gpos.Count));
        }

        progress?.Report(new GpoIndexProgress(GpoIndexStage.Completed));
        return new GpoSettingsIndex
        {
            Server = session.Info.Server,
            DomainDn = domainDn,
            DomainDnsName = session.Info.DomainDnsName,
            DomainSid = domainSid,
            Generation = request.Generation,
            BuiltAt = DateTimeOffset.UtcNow,
            Definitions = request.Definitions,
            SysvolRead = sysvol is not null,
            Entries = entries,
            Containers = containers,
            OtherDomains = otherDomains,
            SidsByName = sidsByName,
        };
    }

    /// <summary>The entry with its configured policies and units built from its kept content (no I/O).</summary>
    internal static GpoIndexEntry WithUnits(GpoIndexEntry entry, PolicyDefinitionSet? definitions, string? domainSid, IReadOnlyDictionary<string, string> sidsByName)
    {
        if (!entry.HasContent)
        {
            return entry with { ComputerPolicies = ConfiguredPolicies.None, UserPolicies = ConfiguredPolicies.None, Units = [] };
        }

        var (computer, user) = GpoSettingUnits.ConfiguredOf(entry.MachineRegistry, entry.UserRegistry, definitions);
        var context = new GpoUnitContext(definitions, domainSid) { SidsByName = sidsByName, PreferenceScopes = entry.PreferenceScopes };
        return entry with
        {
            ComputerPolicies = computer,
            UserPolicies = user,
            Units = GpoSettingUnits.Build(entry.Settings, computer, user, context),
        };
    }

    /// <summary>Both versions read and equal: the folder did not change since the previous index.</summary>
    private static bool Unchanged(GpoInfo before, GpoInfo now) =>
        before.DirectoryVersion is { } directory && before.SysvolVersion is { } sysvol
        && now.DirectoryVersion == directory && now.SysvolVersion == sysvol;

    /// <summary>The parsed content of one GPO (no raw bytes).</summary>
    private sealed record ParsedGpo(
        GpoIndexStatus Status,
        IReadOnlyList<GpoSetting> Settings,
        RegistryPolicyDocument? MachineRegistry,
        RegistryPolicyDocument? UserRegistry,
        IReadOnlyDictionary<string, GpoPreferenceScope> PreferenceScopes,
        IReadOnlyList<string> NotReadFiles,
        IReadOnlyList<string> OverflowedFiles,
        IReadOnlyList<string> OtherFiles)
    {
        private static readonly IReadOnlyDictionary<string, GpoPreferenceScope> NoScopes = new Dictionary<string, GpoPreferenceScope>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A folder whose content could not be parsed (status <see cref="GpoIndexStatus.Failed"/>, nothing kept).</summary>
        public static ParsedGpo Failed { get; } = new(GpoIndexStatus.Failed, [], null, null, NoScopes, [], [], []);

        public static ParsedGpo From(GpoIndexEntry entry) => new(entry.Status, entry.Settings, entry.MachineRegistry, entry.UserRegistry,
            entry.PreferenceScopes, entry.NotReadFiles, entry.OverflowedFiles, entry.OtherFiles);

        public static ParsedGpo Parse(GpoFolderReadResult result)
        {
            if (result.Skipped || result.Status != GpoFolderReadStatus.Read || result.Content is not { } folder)
            {
                var status = result.Skipped ? GpoIndexStatus.NotRead : result.Status switch
                {
                    GpoFolderReadStatus.NotFound => GpoIndexStatus.NotFound,
                    GpoFolderReadStatus.AccessDenied => GpoIndexStatus.AccessDenied,
                    GpoFolderReadStatus.TooLarge => GpoIndexStatus.TooLarge,
                    _ => GpoIndexStatus.Failed,
                };
                return new ParsedGpo(status, [], null, null, NoScopes, [], [], []);
            }

            var read = GroupPolicyReader.ReadSettings(folder);
            var complete = read.Complete && result.NotRead.Count == 0;
            return new ParsedGpo(
                complete ? GpoIndexStatus.Read : GpoIndexStatus.Incomplete,
                read.Settings,
                GpoSettingUnits.RegistryOf(folder, "Machine/Registry.pol"),
                GpoSettingUnits.RegistryOf(folder, "User/Registry.pol"),
                GpoSettingUnits.PreferenceScopes(folder),
                result.NotRead,
                read.OverflowedFiles,
                result.OtherFiles);
        }

        public GpoIndexEntry ToEntry(GpoInfo gpo) => new()
        {
            Gpo = gpo,
            Status = Status,
            Settings = Settings,
            MachineRegistry = MachineRegistry,
            UserRegistry = UserRegistry,
            PreferenceScopes = PreferenceScopes,
            NotReadFiles = NotReadFiles,
            OverflowedFiles = OverflowedFiles,
            OtherFiles = OtherFiles,
        };

        /// <summary>For diagnostics: the status and counts only.</summary>
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Status} ({Settings.Count} settings)");
    }

    /// <summary>The folder reader's counts as the index's Folders stage.</summary>
    private sealed class FolderProgress(IProgress<GpoIndexProgress> inner) : IProgress<GpoFolderReadProgress>
    {
        public void Report(GpoFolderReadProgress value) => inner.Report(new GpoIndexProgress(GpoIndexStage.Folders, value.Done, value.Total));
    }
}
