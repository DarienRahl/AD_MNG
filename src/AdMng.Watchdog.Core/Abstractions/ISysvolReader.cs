using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>
/// Read-only access to one GPO folder on the SYSVOL share of the pinned DC (stage B design 8.3). The folder is always
/// <c>\\&lt;pinned DC&gt;\SYSVOL\&lt;domain DNS&gt;\Policies\{GUID}</c> built from a validated <see cref="SysvolGpoFolder"/> –
/// never a host or path taken from AD data (8.2). Everything stays in memory; implementations write nothing to disk.
/// The Linux service implements it over <c>smbclient</c> (B5b); tests use the in-memory fake of AdMng.Watchdog.Testing.
/// </summary>
public interface ISysvolReader
{
    /// <summary>Recursive listing (names, sizes, SMB modification times) of the GPO folder. Paths are relative to the
    /// folder, '/'-separated; the caller validates them (<see cref="GpoRelativePath"/>).</summary>
    /// <exception cref="SysvolReadException">The folder could not be listed (access denied, not found, Kerberos, timeout …).</exception>
    Task<SysvolListing> ListAsync(SysvolGpoFolder folder, CancellationToken cancellationToken);

    /// <summary>Reads the given files (relative paths of a listing) into memory. A file that cannot be read is returned
    /// with <see cref="SysvolFile.Error"/> set; a file larger than <paramref name="maxBytesPerFile"/> is not read
    /// (<see cref="SysvolErrorKind.TooLarge"/>).</summary>
    /// <exception cref="SysvolReadException">The read failed as a whole (logon failure, share unreachable, timeout …).</exception>
    Task<IReadOnlyList<SysvolFile>> ReadAsync(SysvolGpoFolder folder, IReadOnlyList<string> relativePaths, long maxBytesPerFile,
        CancellationToken cancellationToken);
}
