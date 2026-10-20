using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Owner, group and mode of a path (<c>lstat</c>: a symbolic link is reported as such, not followed).</summary>
public sealed record PosixFileStatus(uint Uid, uint Gid, UnixFileMode Mode, bool IsDirectory, bool IsSymbolicLink);

/// <summary>
/// The POSIX start-up checks of the Linux service (stage B design 11), replacing <see cref="DataDirectoryAcl"/> and
/// <see cref="BootstrapFileGuard"/> there: owner and mode of the data folder and its files, POSIX ACL entries, network
/// file systems, the bootstrap file and its folder, the keytab, the TLS key, the credentials folder, umask and core
/// dumps. Every result is a list of value-free problem codes (<c>name:problem</c> with the file's name, never contents).
/// </summary>
[SupportedOSPlatform("linux")]
public static partial class PosixPermissionCheck
{
    /// <summary>The credentials folder of the package (root 0700; the service reads the systemd copy only).</summary>
    public const string DefaultCredentialsDirectory = "/etc/admng-watchdog/credentials";

    /// <summary>The umask of the unit (<c>UMask=0077</c>).</summary>
    public const int ExpectedUmask = 0x3F; // 0077

    private const string PosixAclAttribute = "system.posix_acl_access";
    private const int WriteOk = 2;
    private const int ReadOk = 4;
    private const int ENOENT = 2;

    private const UnixFileMode GroupOtherBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>File system types (as .NET's <see cref="DriveInfo.DriveFormat"/> names them) that are not local.</summary>
    private static readonly HashSet<string> NetworkFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "nfs", "nfs4", "nfsd", "cifs", "smb", "smb2", "smb3", "smbfs", "9p", "v9fs", "fuse", "fuseblk", "ceph", "afs", "coda", "ncpfs",
    };

    /// <summary>The effective user id of the process.</summary>
    public static uint EffectiveUserId() => GetEffectiveUserId();

    /// <summary><c>lstat</c> of <paramref name="path"/>; null when it does not exist.</summary>
    /// <exception cref="IOException">The status could not be read (other than "does not exist").</exception>
    public static PosixFileStatus? Stat(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Span<byte> buffer = stackalloc byte[256];
        buffer.Clear();
        int result;
        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                result = LStat(path, pointer);
            }
        }

        if (result != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            return errno == ENOENT ? null : throw new IOException("lstat failed (errno " + errno.ToString(CultureInfo.InvariantCulture) + ").");
        }

        // struct stat: x86_64 st_mode @24, st_uid @28, st_gid @32; aarch64 (asm-generic) st_mode @16, st_uid @24, st_gid @28.
        var (modeOffset, uidOffset, gidOffset) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (24, 28, 32),
            Architecture.Arm64 => (16, 24, 28),
            _ => throw new PlatformNotSupportedException("lstat layout of this architecture is not known."),
        };
        var mode = BitConverter.ToUInt32(buffer.Slice(modeOffset, 4));
        var type = mode & 0xF000;
        return new PosixFileStatus(
            BitConverter.ToUInt32(buffer.Slice(uidOffset, 4)),
            BitConverter.ToUInt32(buffer.Slice(gidOffset, 4)),
            (UnixFileMode)(mode & 0xFFF),
            IsDirectory: type == 0x4000,
            IsSymbolicLink: type == 0xA000);
    }

    /// <summary>True when <paramref name="path"/> carries POSIX ACL entries (<c>system.posix_acl_access</c>).</summary>
    public static bool HasPosixAcl(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // -1 with ENODATA: no ACL; with ENOTSUP and friends: the file system has no ACLs at all.
        return LGetXattr(path, PosixAclAttribute, IntPtr.Zero, UIntPtr.Zero) >= 0;
    }

    /// <summary>True when the process may write <paramref name="path"/> (<c>access(W_OK)</c>: also false on a read-only
    /// mount such as <c>ProtectSystem=strict</c>).</summary>
    public static bool IsWritable(string path) => Access(path, WriteOk) == 0;

    /// <summary>True when the process may read <paramref name="path"/> (<c>access(R_OK)</c>).</summary>
    public static bool IsReadable(string path) => Access(path, ReadOk) == 0;

    /// <summary>The file system type of <paramref name="path"/> (e.g. <c>ext4</c>, <c>nfs</c>), null when unknown.</summary>
    public static string? FileSystemType(string path)
    {
        try
        {
            return new DriveInfo(path).DriveFormat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True for NFS, SMB/CIFS, FUSE and 9p file systems.</summary>
    public static bool IsNetworkFileSystem(string? type) =>
        type is not null && (NetworkFileSystems.Contains(type) || type.StartsWith("fuse.", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Item 1: the data folder and its database, WAL/SHM, <c>keys/</c>, <c>backup/</c> (<see cref="DataDirectory.ProtectedPaths"/>):
    /// owned by <paramref name="ownerUid"/>, not symbolic links, no group/other bits, no POSIX ACL, on a local file system.
    /// Missing files are skipped (the database does not exist before the first start).
    /// </summary>
    public static IReadOnlyList<string> CheckDataDirectory(IEnumerable<string> paths, uint ownerUid)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var problems = new List<string>();
        var first = true;
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.TrimEnd('/'));
            var status = Stat(path);
            if (status is null)
            {
                first = false;
                continue;
            }

            if (status.IsSymbolicLink)
            {
                problems.Add(name + ":symlink");
                first = false;
                continue;
            }

            if (status.Uid != ownerUid)
            {
                problems.Add(name + ":owner");
            }

            if ((status.Mode & GroupOtherBits) != 0)
            {
                problems.Add(name + ":mode");
            }

            if (HasPosixAcl(path))
            {
                problems.Add(name + ":acl");
            }

            if (first && IsNetworkFileSystem(FileSystemType(path)))
            {
                problems.Add(name + ":networkFileSystem");
            }

            first = false;
        }

        return problems;
    }

    /// <summary>Item 2: the bootstrap file and its folder are owned by root and not writable by the process (no rename
    /// attack). A missing file is no problem here (the service then runs with the defaults next to the binary).</summary>
    public static IReadOnlyList<string> CheckBootstrapFile(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        var problems = new List<string>();
        var status = Stat(file);
        if (status is null)
        {
            return problems;
        }

        var folder = Path.GetDirectoryName(file) ?? "/";
        var folderStatus = Stat(folder);
        if (status.IsSymbolicLink)
        {
            problems.Add("appsettings.json:symlink");
        }

        if (status.Uid != 0)
        {
            problems.Add("appsettings.json:owner");
        }

        if (folderStatus is not null && folderStatus.Uid != 0)
        {
            problems.Add("folder:owner");
        }

        if (IsWritable(file))
        {
            problems.Add("appsettings.json:writable");
        }

        if (IsWritable(folder))
        {
            problems.Add("folder:writable");
        }

        return problems;
    }

    /// <summary>Item 3: the keytab is owned by root, not writable by group/other and readable by the process.</summary>
    public static IReadOnlyList<string> CheckKeytab(string keytab)
    {
        ArgumentException.ThrowIfNullOrEmpty(keytab);
        var status = Stat(keytab);
        if (status is null)
        {
            return ["keytab:missing"];
        }

        var problems = new List<string>();
        if (status.IsSymbolicLink)
        {
            problems.Add("keytab:symlink");
        }

        if (status.Uid != 0)
        {
            problems.Add("keytab:owner");
        }

        if ((status.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            problems.Add("keytab:mode");
        }

        if (!IsReadable(keytab))
        {
            problems.Add("keytab:unreadable");
        }

        return problems;
    }

    /// <summary>Item 4: true when the TLS key file can be read by everyone.</summary>
    public static bool IsWorldReadable(string file)
    {
        var status = Stat(file);
        return status is not null && (status.Mode & UnixFileMode.OtherRead) != 0;
    }

    /// <summary>Item 6: the umask of the process from <c>/proc/self/status</c>; null when unknown.</summary>
    public static int? ReadUmask(string statusFile = "/proc/self/status")
    {
        try
        {
            foreach (var line in File.ReadLines(statusFile))
            {
                if (line.StartsWith("Umask:", StringComparison.Ordinal))
                {
                    return Convert.ToInt32(line["Umask:".Length..].Trim(), 8);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
        }

        return null;
    }

    /// <summary>Item 6: true when the soft core-file limit (<c>/proc/self/limits</c>) is not 0; null when unknown.</summary>
    public static bool? CoreDumpsEnabled(string limitsFile = "/proc/self/limits")
    {
        try
        {
            foreach (var line in File.ReadLines(limitsFile))
            {
                if (line.StartsWith("Max core file size", StringComparison.Ordinal))
                {
                    var fields = line["Max core file size".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return fields.Length > 0 && fields[0] != "0";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Items 3 and 6 that only warn (health <c>host.hardening</c>): umask not 0077, core dumps enabled, the credentials
    /// folder readable by the service. Returns the Args of the warning (<c>umask</c>, <c>coreDumps</c>,
    /// <c>credentialsReadable</c>).
    /// </summary>
    public static IReadOnlyList<string> CheckHardening(int? umask, bool? coreDumps, string credentialsDirectory)
    {
        var problems = new List<string>();
        if (umask is { } mask && mask != ExpectedUmask)
        {
            problems.Add("umask");
        }

        if (coreDumps == true)
        {
            problems.Add("coreDumps");
        }

        if (System.IO.Directory.Exists(credentialsDirectory) && IsReadable(credentialsDirectory))
        {
            problems.Add("credentialsReadable");
        }

        return problems;
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int LStat(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "lgetxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint LGetXattr(string path, string name, IntPtr value, UIntPtr size);

    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Access(string path, int mode);
}
