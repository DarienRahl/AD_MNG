using System.Globalization;
using AdMng.Core.Security;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>GPO status (<c>flags</c> of the container): which parts of the GPO are processed.</summary>
public enum GpoStatus
{
    AllSettingsEnabled = 0,
    UserSettingsDisabled = 1,
    ComputerSettingsDisabled = 2,
    AllSettingsDisabled = 3,
}

/// <summary>
/// A group policy object as the GPO views show it (design note docs/dev/k-computers-and-gpo.md 5.1): the container in
/// <c>CN=Policies,CN=System</c> read from the session's DC, plus the <c>GPT.INI</c> version of its SYSVOL folder when it
/// was read. In memory for the window that shows it.
/// </summary>
public sealed record GpoInfo
{
    /// <summary>GUID of the Default Domain Policy (the same in every domain).</summary>
    public static readonly Guid DefaultDomainPolicyId = new("31B2F340-016D-11D2-945F-00C04FB984F9");

    /// <summary>GUID of the Default Domain Controllers Policy (the same in every domain).</summary>
    public static readonly Guid DefaultDomainControllersPolicyId = new("6AC1786C-016F-11D2-945F-00C04FB984F9");

    public required Guid Id { get; init; }

    public required string Dn { get; init; }

    /// <summary><c>displayName</c>, or the GUID in braces when it has none.</summary>
    public required string DisplayName { get; init; }

    /// <summary><c>versionNumber</c> of the container (null: not set).</summary>
    public GpoVersion? DirectoryVersion { get; init; }

    /// <summary><c>Version</c> of <c>GPT.INI</c> on the session's DC (null: not read, missing or unreadable).</summary>
    public GpoVersion? SysvolVersion { get; init; }

    /// <summary>Why <see cref="SysvolVersion"/> is missing after a read (null: read, or not attempted).</summary>
    public SysvolFileStatus? SysvolStatus { get; init; }

    public int Flags { get; init; }

    public GpoStatus Status => (GpoStatus)(Flags & 3);

    public string? MachineExtensionNames { get; init; }

    public string? UserExtensionNames { get; init; }

    /// <summary><c>gPCWQLFilter</c> as stored (<c>[domain;{ID};0]</c>).</summary>
    public string? WmiFilterLink { get; init; }

    /// <summary>The WMI filter's id from <see cref="WmiFilterLink"/>.</summary>
    public string? WmiFilterId => WmiFilterLinks.ParseId(WmiFilterLink);

    public string? FileSysPath { get; init; }

    public int? FunctionalityVersion { get; init; }

    /// <summary>The container's security descriptor (owner + DACL; in memory only).</summary>
    public byte[]? SecurityDescriptor { get; init; }

    /// <summary>Owner SID from <see cref="SecurityDescriptor"/>.</summary>
    public string? OwnerSid => SecurityDescriptor is { } sd && SecurityDescriptorSummary.TryParse(sd, out var summary) ? summary?.Owner : null;

    /// <summary>Who applies the GPO (from the DACL).</summary>
    public GroupPolicySecurityFilter? SecurityFilter => GroupPolicySecurityFilter.FromDescriptor(SecurityDescriptor);

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? Changed { get; init; }

    /// <summary>The Default Domain Policy or the Default Domain Controllers Policy (cannot be deleted).</summary>
    public bool IsDefault => Id == DefaultDomainPolicyId || Id == DefaultDomainControllersPolicyId;

    /// <summary>
    /// AD and SYSVOL disagree: <c>GPT.INI</c> behind AD means SYSVOL replication pending on this DC, ahead of AD a failed
    /// write or AD replication lag (design note 3.4).
    /// </summary>
    public bool VersionMismatch => SysvolVersion is { } sysvol && DirectoryVersion is { } directory && sysvol != directory;

    /// <summary>The version the next content change starts from: the higher half of AD and GPT.INI.</summary>
    public GpoVersion CurrentVersion => GpoVersion.Max(DirectoryVersion ?? GpoVersion.Zero, SysvolVersion ?? GpoVersion.Zero);

    /// <summary>For diagnostics: the GUID and versions only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{SysvolPaths.FolderName(Id)} {DirectoryVersion}/{SysvolVersion}");
}

/// <summary>A link of a GPO from a domain head, OU or site.</summary>
/// <param name="ContainerDn">The linking container.</param>
/// <param name="Kind">Site, domain or OU.</param>
/// <param name="ContainerName">Display name of the container (name of the site / OU, DNS name of the domain).</param>
/// <param name="GpoId">The linked GPO.</param>
/// <param name="Order">Link order in the container (1 = highest precedence).</param>
/// <param name="Enforced">Enforced (No Override).</param>
/// <param name="Enabled">The link is enabled.</param>
public sealed record GpoLinkInfo(string ContainerDn, GroupPolicySomKind Kind, string ContainerName, Guid GpoId, int Order, bool Enforced, bool Enabled);

/// <summary>A domain head, OU or site with its <c>gPLink</c> and <c>gPOptions</c> as read.</summary>
/// <param name="Dn">The container.</param>
/// <param name="Kind">Site, domain or OU.</param>
/// <param name="Name">Display name.</param>
/// <param name="Links">The parsed <c>gPLink</c> (own and foreign entries).</param>
/// <param name="RawGpLink">The value as read (the compare-and-swap of a link change uses it).</param>
/// <param name="GpOptions">The value of <c>gPOptions</c> as read (null: not set).</param>
public sealed record GroupPolicyContainerInfo(string Dn, GroupPolicySomKind Kind, string Name, GpLinkValue Links, string? RawGpLink, string? GpOptions)
{
    /// <summary>Block inheritance (<c>gPOptions</c> bit 1; never for sites).</summary>
    public bool BlocksInheritance => Kind != GroupPolicySomKind.Site
        && long.TryParse(GpOptions, NumberStyles.Integer, CultureInfo.InvariantCulture, out var options)
        && (options & GroupPolicyLinks.BlockInheritanceOption) != 0;

    /// <summary>The scope-of-management form used by <see cref="GroupPolicyInheritance"/>.</summary>
    public GroupPolicySom ToSom() => new(Dn, Kind, GroupPolicyLinks.Parse(RawGpLink), BlocksInheritance);
}

/// <summary>One query of a WMI filter.</summary>
/// <param name="Namespace">WMI namespace, e.g. <c>root\CIMv2</c>.</param>
/// <param name="Query">The WQL query.</param>
/// <param name="Language">Query language (always <c>WQL</c>).</param>
public sealed record WmiFilterQuery(string Namespace, string Query, string Language = "WQL");

/// <summary>A WMI filter (<c>msWMI-Som</c> in <c>CN=SOM,CN=WMIPolicy,CN=System</c>).</summary>
public sealed record WmiFilterInfo
{
    public required string Dn { get; init; }

    /// <summary><c>msWMI-ID</c>: <c>{GUID}</c>.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary><c>msWMI-Parm1</c>.</summary>
    public string? Description { get; init; }

    /// <summary>The queries of <c>msWMI-Parm2</c> (empty when it cannot be parsed - see <see cref="RawQueries"/>).</summary>
    public IReadOnlyList<WmiFilterQuery> Queries { get; init; } = [];

    /// <summary><c>msWMI-Parm2</c> as stored.</summary>
    public string? RawQueries { get; init; }

    public string? Author { get; init; }

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? Changed { get; init; }

    public override string ToString() => Id;
}

/// <summary>The files of a GPO folder copied into memory (copy, backup, compare), within the limits of <see cref="GpoTransaction"/>.</summary>
/// <param name="Entries">Every file and folder (paths relative to the GPO folder).</param>
/// <param name="Files">Content per file path (GPT.INI included; leftovers of interrupted writes excluded).</param>
public sealed record GpoFolderContent(IReadOnlyList<SysvolEntry> Entries, IReadOnlyDictionary<string, byte[]> Files)
{
    /// <summary>Leftovers of interrupted writes (<c>*.admng-*.tmp</c>).</summary>
    public IReadOnlyList<string> Leftovers { get; init; } = [];

    /// <summary>Total bytes held.</summary>
    public long TotalBytes => Files.Values.Sum(f => (long)f.Length);

    /// <summary>The folders (relative paths).</summary>
    public IEnumerable<string> Folders => Entries.Where(e => e.IsFolder).Select(e => e.RelativePath);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Files.Count} files, {TotalBytes} B");
}

/// <summary>A GPO folder exceeds the limits of an in-memory copy (<see cref="GpoTransaction.MaxEntriesPerGpo"/> …).</summary>
public sealed class GpoTooLargeException : Exception
{
    public GpoTooLargeException()
        : base("The GPO is too large for a safe copy in memory.")
    {
    }

    public GpoTooLargeException(string message)
        : base(message)
    {
    }

    public GpoTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The <c>gPCWQLFilter</c> value: <c>[&lt;domain DNS&gt;;{FILTER-ID};0]</c>.</summary>
public static class WmiFilterLinks
{
    /// <summary>The link value for a filter of a domain.</summary>
    public static string Format(string domainDnsName, string filterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainDnsName);
        ArgumentException.ThrowIfNullOrWhiteSpace(filterId);
        return "[" + domainDnsName.Trim().TrimEnd('.') + ";" + filterId + ";0]";
    }

    /// <summary>The filter id (<c>{GUID}</c>) of a link value, null when there is none.</summary>
    public static string? ParseId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimStart('[').TrimEnd(']');
        var parts = trimmed.Split(';');
        return parts.Length >= 2 && parts[1].Trim() is { Length: > 0 } id ? id : null;
    }
}
