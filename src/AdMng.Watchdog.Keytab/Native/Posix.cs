using System.Runtime.InteropServices;

namespace AdMng.Watchdog.Keytab.Native;

/// <summary>The few libc calls .NET has no API for: effective uid, group lookup, chown, directory fsync.</summary>
internal static unsafe partial class Posix
{
    private const string Libc = "libc";
    private const int ReadOnlyDirectory = 0x10000; // O_RDONLY | O_DIRECTORY (x86_64, aarch64)

    [LibraryImport(Libc, EntryPoint = "geteuid")]
    public static partial uint GetEffectiveUserId();

    [LibraryImport(Libc, EntryPoint = "getgrnam", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetGroupByName(string name);

    [LibraryImport(Libc, EntryPoint = "chown", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Chown(string path, uint owner, uint group);

    [LibraryImport(Libc, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport(Libc, EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [LibraryImport(Libc, EntryPoint = "close")]
    private static partial int Close(int fd);

    public static bool IsRoot => GetEffectiveUserId() == 0;

    /// <summary>The gid of a group (struct group: name, passwd, gid at offset 16 on LP64), null when it does not exist.</summary>
    public static uint? GroupId(string name)
    {
        var group = GetGroupByName(name);
        return group == IntPtr.Zero ? null : *(uint*)((byte*)group + 16);
    }

    /// <summary>Sets owner root and the given group (no-op when not root or the group does not exist).</summary>
    /// <exception cref="IOException">chown failed.</exception>
    public static void ChownRootGroup(string path, string group)
    {
        if (!IsRoot || GroupId(group) is not { } gid)
        {
            return;
        }

        if (Chown(path, 0, gid) != 0)
        {
            throw new IOException($"chown of {Path.GetFileName(path)} failed (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    /// <summary>fsync of a directory, so that a rename in it is durable.</summary>
    public static void SyncDirectory(string directory)
    {
        var fd = Open(directory, ReadOnlyDirectory);
        if (fd < 0)
        {
            return;
        }

        try
        {
            _ = Fsync(fd);
        }
        finally
        {
            _ = Close(fd);
        }
    }
}
