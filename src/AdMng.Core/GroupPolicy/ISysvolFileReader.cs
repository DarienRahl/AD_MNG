using System.Globalization;
using AdMng.Core.Directory;

namespace AdMng.Core.GroupPolicy;

/// <summary>
/// Reads GPO files from the SYSVOL share of the session's domain controller (CLAUDE.md rule 4: the DC of the session,
/// never another one). File contents are returned in memory only and never written anywhere (rule 1); the caller keeps
/// them as long as it parses them. The Windows implementation (AdMng.Directory) reads the UNC path with the operator's
/// Windows identity or, for a session opened with other credentials, in a logon session of the run's own that carries the
/// session's in-memory credential for network access (rule 2: never persisted).
/// </summary>
public interface ISysvolFileReader
{
    /// <summary>Opens SYSVOL of the session's DC for one run; dispose the result to release the connection.</summary>
    /// <exception cref="SysvolUnavailableException">SYSVOL cannot be read with this session (the reason says why).</exception>
    Task<ISysvolConnection> OpenAsync(IDirectorySession session, CancellationToken cancellationToken);
}

/// <summary>SYSVOL of one DC, opened by <see cref="ISysvolFileReader.OpenAsync"/>.</summary>
public interface ISysvolConnection : IAsyncDisposable
{
    /// <summary>The DC whose SYSVOL is read (the session's DC).</summary>
    string Server { get; }

    /// <summary>
    /// Reads one file of a GPO folder (<c>&lt;domain&gt;\Policies\{GUID}\&lt;relative path&gt;</c>) into memory. A missing
    /// file is <see cref="SysvolFileStatus.NotFound"/> (normal: most GPOs have no such file); a larger file than
    /// <paramref name="maxBytes"/> is not read.
    /// </summary>
    /// <param name="gpoId">The GPO's GUID (from the cn of its container; gPCFileSysPath is never used as a path).</param>
    /// <param name="relativePath">'/'-separated path below the GPO folder (validated by <see cref="SysvolPaths.TryNormalize"/>).</param>
    /// <param name="maxBytes">Largest file read.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    Task<SysvolFileResult> ReadGpoFileAsync(Guid gpoId, string relativePath, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>Outcome of one file read.</summary>
public enum SysvolFileStatus
{
    Read,
    NotFound,
    AccessDenied,
    TooLarge,
    Failed,
}

/// <summary>One file read from SYSVOL: its content (in memory) or why it could not be read.</summary>
public sealed record SysvolFileResult(SysvolFileStatus Status, byte[]? Content = null)
{
    public static SysvolFileResult NotFound { get; } = new(SysvolFileStatus.NotFound);

    public static SysvolFileResult AccessDenied { get; } = new(SysvolFileStatus.AccessDenied);

    public static SysvolFileResult TooLarge { get; } = new(SysvolFileStatus.TooLarge);

    public static SysvolFileResult Failed { get; } = new(SysvolFileStatus.Failed);

    public static SysvolFileResult Of(byte[] content) => new(SysvolFileStatus.Read, content ?? throw new ArgumentNullException(nameof(content)));

    /// <summary>Never the content (directory data stays out of logs and exception texts).</summary>
    public override string ToString() => Content is null ? Status.ToString() : string.Create(CultureInfo.InvariantCulture, $"{Status} ({Content.Length} bytes)");
}

/// <summary>Why SYSVOL of the session's DC cannot be read at all.</summary>
public enum SysvolUnavailableReason
{
    /// <summary>The reader cannot read SYSVOL on this platform or for this kind of session.</summary>
    NotSupported,

    /// <summary>The session was opened by a simple bind with a distinguished name: there is no logon name for the share.</summary>
    DistinguishedNameLogon,

    /// <summary>The DC refused the credentials for the share.</summary>
    LogonFailure,

    /// <summary>The share refused access.</summary>
    AccessDenied,

    /// <summary>The DC or its SYSVOL share could not be reached.</summary>
    Unreachable,

    /// <summary>Anything else.</summary>
    Failed,
}

/// <summary>SYSVOL of the session's DC cannot be read (<see cref="Reason"/>).</summary>
public sealed class SysvolUnavailableException : Exception
{
    public SysvolUnavailableException()
    {
    }

    public SysvolUnavailableException(string message)
        : base(message)
    {
    }

    public SysvolUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SysvolUnavailableException(SysvolUnavailableReason reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public SysvolUnavailableReason Reason { get; } = SysvolUnavailableReason.Failed;
}

/// <summary>Validation of the names and paths a SYSVOL reader puts into a UNC path or a command line.</summary>
public static class SysvolPaths
{
    /// <summary>Longest accepted relative path.</summary>
    public const int MaxRelativePathLength = 512;

    /// <summary>
    /// A host name a SYSVOL path may name: a DNS name (labels of <c>[A-Za-z0-9-]</c>, 1–63 characters, not starting or
    /// ending with '-', at most 253 characters, one trailing dot removed) or an IPv4 address. Nothing else reaches a path.
    /// </summary>
    public static bool TryNormalizeHost(string? value, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var name = value.Trim();
        if (name.EndsWith('.'))
        {
            name = name[..^1];
        }

        if (name.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }
        }

        host = name;
        return true;
    }

    /// <summary>
    /// Normalizes a path below a GPO folder ('\' → '/') and refuses rooted paths, empty, <c>.</c> and <c>..</c> segments,
    /// control characters and <c>: * ? " &lt; &gt; |</c>.
    /// </summary>
    public static bool TryNormalize(string? relativePath, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(relativePath) || relativePath.Length > MaxRelativePathLength || relativePath[0] is '/' or '\\')
        {
            return false;
        }

        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')))
        {
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>The GPO folder name: the GUID in braces, upper case (as GPMC creates it).</summary>
    public static string FolderName(Guid gpoId) => gpoId.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();
}
