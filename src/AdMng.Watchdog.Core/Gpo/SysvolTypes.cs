using System.Globalization;

namespace AdMng.Watchdog.Core.Gpo;

/// <summary>
/// The one GPO folder the service may read (stage B design 8.2/8.3): <c>\\&lt;Dc&gt;\SYSVOL\&lt;DomainDns&gt;\Policies\{GUID}</c>.
/// Built only from a DNS host name and a domain DNS name restricted to <c>[a-z0-9.-]</c> and a <see cref="Guid"/>, so a
/// path or command built from it cannot carry separators, quotes or shell syntax.
/// </summary>
public sealed class SysvolGpoFolder : IEquatable<SysvolGpoFolder>
{
    private SysvolGpoFolder(string dc, string domainDns, Guid gpoGuid)
    {
        Dc = dc;
        DomainDns = domainDns;
        GpoGuid = gpoGuid;
    }

    /// <summary>DNS host name of the pinned DC (lower case, no trailing dot).</summary>
    public string Dc { get; }

    /// <summary>DNS name of the domain (lower case, no trailing dot).</summary>
    public string DomainDns { get; }

    public Guid GpoGuid { get; }

    /// <summary>The GPO folder name: the GUID in "B" format, upper case (as GPMC creates it).</summary>
    public string FolderName => GpoGuid.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <summary>smbclient service: <c>//dc01.contoso.com/SYSVOL</c>.</summary>
    public string Service => "//" + Dc + "/SYSVOL";

    /// <summary>Directory below the share (smbclient <c>-D</c>): <c>contoso.com/Policies/{GUID}</c>.</summary>
    public string Directory => DomainDns + "/Policies/" + FolderName;

    /// <summary>UNC path, for display and logs.</summary>
    public string UncPath => @"\\" + Dc + @"\SYSVOL\" + DomainDns + @"\Policies\" + FolderName;

    /// <summary>Validates and normalizes the parts.</summary>
    /// <exception cref="ArgumentException">A name is not a DNS name or the GUID is empty.</exception>
    public static SysvolGpoFolder Create(string dc, string domainDns, Guid gpoGuid)
    {
        if (!TryNormalizeDnsName(dc, out var host))
        {
            throw new ArgumentException("The DC must be a DNS host name ([a-z0-9.-]).", nameof(dc));
        }

        if (!TryNormalizeDnsName(domainDns, out var domain))
        {
            throw new ArgumentException("The domain must be a DNS name ([a-z0-9.-]).", nameof(domainDns));
        }

        if (gpoGuid == Guid.Empty)
        {
            throw new ArgumentException("The GPO GUID must not be empty.", nameof(gpoGuid));
        }

        return new SysvolGpoFolder(host, domain, gpoGuid);
    }

    /// <summary>
    /// Lower-cases and checks a DNS name: labels of 1–63 characters <c>[a-z0-9-]</c> not starting or ending with '-',
    /// at most 253 characters, one trailing dot allowed (removed); an all-digit last label (IPv4 literal) is refused.
    /// </summary>
    public static bool TryNormalizeDnsName(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var name = value.Trim();
        if (name.EndsWith('.'))
        {
            name = name[..^1];
        }

        name = name.ToLowerInvariant();
        if (name.Length is 0 or > 253)
        {
            return false;
        }

        var labels = name.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'
                || !label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        if (labels[^1].All(char.IsAsciiDigit))
        {
            return false; // an IPv4 literal: Kerberos needs the host name (no SPN for an address)
        }

        normalized = name;
        return true;
    }

    public bool Equals(SysvolGpoFolder? other) =>
        other is not null && Dc == other.Dc && DomainDns == other.DomainDns && GpoGuid == other.GpoGuid;

    public override bool Equals(object? obj) => Equals(obj as SysvolGpoFolder);

    public override int GetHashCode() => HashCode.Combine(Dc, DomainDns, GpoGuid);

    public override string ToString() => UncPath;
}

/// <summary>One entry of a recursive GPO folder listing.</summary>
public sealed record SysvolEntry
{
    /// <summary>Path relative to the GPO folder, '/'-separated, as the server reported it (validate before use).</summary>
    public required string RelativePath { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>Size in bytes (0 for directories).</summary>
    public long Size { get; init; }

    /// <summary>SMB last-write time (second precision is enough – smbclient prints seconds).</summary>
    public DateTimeOffset ModifiedAt { get; init; }
}

/// <summary>A recursive listing of one GPO folder.</summary>
public sealed record SysvolListing
{
    public IReadOnlyList<SysvolEntry> Entries { get; init; } = [];
}

/// <summary>Why a SYSVOL read failed (8.3: only <c>NT_STATUS_*</c> codes are evaluated, never logged verbatim).</summary>
public enum SysvolErrorKind
{
    /// <summary>Anything else (unexpected exit code or output).</summary>
    Failed = 0,
    /// <summary><c>NT_STATUS_ACCESS_DENIED</c> → HealthPermissionGap <c>sysvol.accessDenied</c>.</summary>
    AccessDenied = 1,
    /// <summary><c>NT_STATUS_OBJECT_NAME_NOT_FOUND</c> / <c>…PATH_NOT_FOUND</c> / <c>NT_STATUS_NO_SUCH_FILE</c>.</summary>
    NotFound = 2,
    /// <summary><c>NT_STATUS_LOGON_FAILURE</c> / <c>NT_STATUS_INVALID_PARAMETER</c> / no usable TGT → Kerberos hint.</summary>
    Authentication = 3,
    /// <summary>The invocation exceeded its time limit.</summary>
    Timeout = 4,
    /// <summary>The DC or the share could not be reached.</summary>
    Unreachable = 5,
    /// <summary>The file exceeds the per-file limit and was not read.</summary>
    TooLarge = 6,
}

/// <summary>One file read from a GPO folder: its content, or why it could not be read.</summary>
public sealed record SysvolFile
{
    public required string RelativePath { get; init; }

    /// <summary>The content; null when <see cref="Error"/> is set.</summary>
    public byte[]? Content { get; init; }

    public SysvolErrorKind? Error { get; init; }

    /// <summary>The <c>NT_STATUS_*</c> code when the server reported one.</summary>
    public string? NtStatus { get; init; }
}

/// <summary>A SYSVOL listing or read failed as a whole.</summary>
public sealed class SysvolReadException : Exception
{
    public SysvolReadException()
    {
    }

    public SysvolReadException(string message)
        : base(message)
    {
    }

    public SysvolReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SysvolReadException(SysvolErrorKind kind, string? ntStatus, string message)
        : base(message)
    {
        Kind = kind;
        NtStatus = ntStatus;
    }

    public SysvolErrorKind Kind { get; }

    /// <summary>The <c>NT_STATUS_*</c> code when the server reported one (validated code, no free text).</summary>
    public string? NtStatus { get; }
}
