using System.Globalization;
using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.GroupPolicy.Backup;

/// <summary>
/// One GPO backup in memory (design note docs/dev/k-computers-and-gpo.md 5.5): what GPMC keeps in <c>Backup.xml</c> and
/// <c>bkupInfo.xml</c>, the files of <c>DomainSysvol\GPO</c> and AD Manager's sidecar (<c>admng.json</c>). Written only to
/// a folder the operator chose (CLAUDE.md rule 1: an explicit export); read back for restore and import.
/// </summary>
public sealed record GpoBackup
{
    /// <summary>The backup's own id (<c>{GUID}</c> folder name).</summary>
    public required Guid BackupId { get; init; }

    public required Guid GpoId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>DNS name of the GPO's domain.</summary>
    public required string Domain { get; init; }

    /// <summary>The DC the backup was read from.</summary>
    public string? DomainController { get; init; }

    /// <summary>objectGUID of the domain head, when known.</summary>
    public Guid? DomainGuid { get; init; }

    public DateTimeOffset BackupTime { get; init; }

    public string Comment { get; init; } = string.Empty;

    /// <summary><c>flags</c> (GPO status).</summary>
    public int Options { get; init; }

    public GpoVersion Version { get; init; }

    public string? MachineExtensionNames { get; init; }

    public string? UserExtensionNames { get; init; }

    /// <summary><c>gPCWQLFilter</c> as it was.</summary>
    public string? WmiFilterLink { get; init; }

    /// <summary>The container's descriptor (owner + DACL), when it could be read.</summary>
    public byte[]? SecurityDescriptor { get; init; }

    /// <summary>Trustees of the DACL with their names (GPMC's SecurityGroups), for migration and display.</summary>
    public IReadOnlyList<BackupTrustee> Trustees { get; init; } = [];

    /// <summary>Files below the GPO folder (GPT.INI excluded), '/'-separated paths.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; init; } = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders below the GPO folder (empty ones kept).</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>The links of the GPO when it was backed up (sidecar).</summary>
    public IReadOnlyList<BackupLink> Links { get; init; } = [];

    /// <summary>The WMI filter's definition (sidecar).</summary>
    public WmiFilterInfo? WmiFilter { get; init; }

    /// <summary>
    /// Settings of the GPO kept as directory objects below <c>CN=Machine</c>/<c>CN=User</c> (software installation, wireless
    /// and wired network policies) that the backup does NOT hold: counted when AD Manager backs up (sidecar), or the
    /// <c>DSObject</c> entries of a GPMC <c>Backup.xml</c>. Restores and imports warn about them.
    /// </summary>
    public int DirectoryObjectCount { get; init; }

    /// <summary>The backup carries AD Manager's sidecar (hashes verified when read).</summary>
    public bool HasSidecar { get; init; }

    /// <summary>For diagnostics: ids and counts only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Backup {BackupId:B} of {GpoId:B}: {Files.Count} files");
}

/// <summary>A trustee of a backed-up DACL.</summary>
public sealed record BackupTrustee(string Sid, string? Name, string? Type);

/// <summary>A link of a backed-up GPO (from the sidecar).</summary>
/// <param name="ContainerDn">Domain head, OU or site.</param>
/// <param name="Order">Link order (1 = highest precedence).</param>
/// <param name="Options">1 disabled, 2 enforced.</param>
public sealed record BackupLink(string ContainerDn, int Order, int Options);

/// <summary>An entry of a backup folder's list (manifest.xml / bkupInfo.xml).</summary>
public sealed record GpoBackupEntry(Guid BackupId, Guid GpoId, string DisplayName, string Domain, DateTimeOffset BackupTime, string Comment);

/// <summary>A backup cannot be read or is not trustworthy (the message is value-free).</summary>
public sealed class GpoBackupException : Exception
{
    public GpoBackupException()
    {
    }

    public GpoBackupException(string message)
        : base(message)
    {
    }

    public GpoBackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
