using System.Globalization;
using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>Why a GPO folder is or is not in a snapshot.</summary>
public enum GpoFolderReadStatus
{
    /// <summary>Every file was read (within the limits of <see cref="GpoTransaction"/>).</summary>
    Read,

    /// <summary>The <c>{GUID}</c> folder does not exist on SYSVOL of the session's DC.</summary>
    NotFound,

    /// <summary>The folder or a file of it cannot be read with this session.</summary>
    AccessDenied,

    /// <summary>The folder exceeds the limits of an in-memory copy.</summary>
    TooLarge,

    /// <summary>Any other failure.</summary>
    Failed,
}

/// <summary>A GPO folder as the diagnostics read it (in memory only, CLAUDE.md rule 1).</summary>
public sealed record GpoFolderState
{
    public required GpoFolderReadStatus Status { get; init; }

    /// <summary>The files (<see cref="GpoFolderReadStatus.Read"/> only).</summary>
    public GpoFolderContent? Content { get; init; }

    /// <summary>The folder's security descriptor (DACL only); null when it was not read or cannot be read.</summary>
    public byte[]? Security { get; init; }

    /// <summary>The folder ACL was asked for (GPOs in scope; the others are read for the conflicts only).</summary>
    public bool SecurityRead { get; init; }

    /// <summary>A folder that was read.</summary>
    public static GpoFolderState Of(GpoFolderContent content, byte[]? security = null, bool securityRead = false) =>
        new() { Status = GpoFolderReadStatus.Read, Content = content ?? throw new ArgumentNullException(nameof(content)), Security = security, SecurityRead = securityRead };

    /// <summary>For diagnostics: the status and counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Status} ({Content?.Files.Count ?? 0} files)");
}

/// <summary>Users and computers whose direct parent is one container.</summary>
/// <param name="Users">User accounts (inetOrgPerson included).</param>
/// <param name="Computers">Computer accounts (managed service accounts included).</param>
public readonly record struct GpoObjectCount(int Users, int Computers);

/// <summary>
/// Everything <see cref="GroupPolicyDiagnostics.Analyze"/> needs, read by <see cref="GroupPolicyDiagnosticsReader"/> from the
/// session's DC and its SYSVOL (CLAUDE.md rule 4) and kept in memory only for the window that shows the findings (rule 1).
/// Tests build it directly.
/// </summary>
public sealed record GpoDiagnosticsSnapshot
{
    public required GpoDiagnosticsDepth Depth { get; init; }

    /// <summary>The GPO the check is about; null for the whole domain.</summary>
    public Guid? GpoId { get; init; }

    public required string DomainDn { get; init; }

    public required string DomainDnsName { get; init; }

    /// <summary>The domain SID (null when unreadable: the rules about domain groups then skip them).</summary>
    public string? DomainSid { get; init; }

    /// <summary>The Domain Controllers OU (the well-known container; null: <c>OU=Domain Controllers,&lt;domain&gt;</c>).</summary>
    public string? DomainControllersDn { get; init; }

    /// <summary>Every GPO of the domain (also for one GPO: the links and names need them), with GPT.INI versions when read.</summary>
    public required IReadOnlyList<GpoInfo> Gpos { get; init; }

    /// <summary>The domain head, the OUs with <c>gPLink</c> or <c>gPOptions</c> and the sites with <c>gPLink</c>.</summary>
    public required IReadOnlyList<GroupPolicyContainerInfo> Containers { get; init; }

    public IReadOnlyList<WmiFilterInfo> WmiFilters { get; init; } = [];

    /// <summary>The GPT.INI versions of the GPOs in scope were read (<see cref="GpoInfo.SysvolVersion"/>, <see cref="GpoInfo.SysvolStatus"/>).</summary>
    public bool SysvolVersionsRead { get; init; }

    /// <summary>The folders were read (Full with a SYSVOL connection that lists folders).</summary>
    public bool FoldersRead { get; init; }

    /// <summary>
    /// The folders by GPO: the GPOs in scope (with their ACL) and, for one GPO, the other GPOs reaching its containers
    /// (content only, for the conflicts).
    /// </summary>
    public IReadOnlyDictionary<Guid, GpoFolderState> Folders { get; init; } = new Dictionary<Guid, GpoFolderState>();

    /// <summary>Users and computers by their direct parent's DN (Full); null when not counted.</summary>
    public IReadOnlyDictionary<string, GpoObjectCount>? ObjectCounts { get; init; }

    /// <summary>
    /// The domain SIDs found in the permissions, rights and groups that exist in the directory (Full); null when not
    /// resolved. SIDs of the domain with a RID below 1000 (built-in accounts and groups) always count as existing.
    /// </summary>
    public IReadOnlySet<string>? ExistingSids { get; init; }

    /// <summary>For diagnostics: counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Depth}: {Gpos.Count} GPOs, {Containers.Count} containers, {WmiFilters.Count} WMI filters, {Folders.Count} folders");
}
