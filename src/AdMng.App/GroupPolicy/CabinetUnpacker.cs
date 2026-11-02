using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using Microsoft.Win32.SafeHandles;

namespace AdMng.App.GroupPolicy;

/// <summary>
/// Unpacks a cabinet (CAB) in memory through Windows' own cabinet library (<c>cabinet.dll</c>, its FDI interface) – K3,
/// "Zaktualizuj szablony". The cabinet is read through the handle the extractor holds open (the very bytes whose header
/// and signature were checked); no tool runs and nothing is written to disk. Only templates (<c>.admx</c>,
/// <c>.adml</c>) and ZIP archives (Edge's CAB holds <c>MicrosoftEdgePolicyTemplates.zip</c>) are kept: every name must
/// be a plain relative path, every file is held to the size its entry declares (a template to
/// <see cref="PolicyTemplatePackage.MaxEntryBytes"/>) and all of them to <see cref="PolicyTemplatePackage.MaxArchiveBytes"/>,
/// and a file continued from or into another cabinet is refused.
/// </summary>
/// <remarks>
/// FDI calls back on the calling thread while <c>FDICopy</c> runs: it opens only the cabinet (read-only), reads and seeks
/// it, asks where each file goes (<c>fdintCOPY_FILE</c>: an in-memory buffer here, or skip) and hands every finished
/// file back (<c>fdintCLOSE_FILE_INFO</c>). No exception leaves a callback: the first failure is kept, the callback
/// returns FDI's error value and the failure is thrown once <c>FDICopy</c> has returned.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class CabinetUnpacker
{
    // FDINOTIFICATIONTYPE (fdi.h).
    private const int PartialFileNotification = 1;
    private const int CopyFileNotification = 2;
    private const int CloseFileInfoNotification = 3;
    private const int NextCabinetNotification = 4;

    /// <summary><c>_A_NAME_IS_UTF</c>: the entry's name is UTF-8 (otherwise the code page of the computer that made the CAB).</summary>
    private const ushort NameIsUtf8 = 0x80;

    /// <summary><c>cpuUNKNOWN</c>: FDI picks its decompressor itself.</summary>
    private const int CpuUnknown = -1;

    /// <summary><c>_O_WRONLY | _O_RDWR</c>: an open for writing (FDI only ever reads the cabinet).</summary>
    private const int WriteAccess = 0x0001 | 0x0002;

    /// <summary>Size of <c>ERF</c>: <c>erfOper</c>, <c>erfType</c>, <c>fError</c> (three 32-bit values).</summary>
    private const int ErrorBytes = 3 * sizeof(int);

    private static readonly IntPtr Failure = new(-1);

    private readonly SafeFileHandle _cabinet;
    private readonly long _length;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<IntPtr, object> _handles = [];
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    // The callbacks FDI holds pointers to: kept in fields so they live as long as this unpacker.
    private readonly AllocCallback _alloc = Alloc;
    private readonly FreeCallback _free = Free;
    private readonly OpenCallback _open;
    private readonly ReadCallback _read;
    private readonly WriteCallback _write;
    private readonly CloseCallback _close;
    private readonly SeekCallback _seek;
    private readonly NotifyCallback _notify;

    private byte[] _buffer = new byte[64 * 1024];
    private long _lastHandle;
    private long _kept;
    private Exception? _failure;

    private CabinetUnpacker(SafeFileHandle cabinet, CancellationToken cancellationToken)
    {
        _cabinet = cabinet;
        _length = RandomAccess.GetLength(cabinet);
        _cancellationToken = cancellationToken;
        _open = Open;
        _read = Read;
        _write = Write;
        _close = Close;
        _seek = Seek;
        _notify = Notify;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AllocCallback(uint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeCallback(IntPtr memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenCallback(IntPtr path, int flags, int mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ReadCallback(IntPtr handle, IntPtr buffer, uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint WriteCallback(IntPtr handle, IntPtr buffer, uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CloseCallback(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SeekCallback(IntPtr handle, int distance, int origin);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NotifyCallback(int type, IntPtr notification);

    /// <summary>The templates and ZIP archives of the cabinet read through <paramref name="cabinet"/> ('/'-separated path → content).</summary>
    /// <param name="cabinet">The cabinet, open for reading (its position is not used).</param>
    /// <param name="cancellationToken">Stops the unpacking.</param>
    /// <exception cref="PolicyTemplatePackageException">The cabinet cannot be unpacked (<see cref="PolicyTemplatePackageError.ExtractionFailed"/>) or is too large.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static IReadOnlyDictionary<string, byte[]> Unpack(SafeFileHandle cabinet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cabinet);
        cancellationToken.ThrowIfCancellationRequested();
        return new CabinetUnpacker(cabinet, cancellationToken).Run();
    }

    private IReadOnlyDictionary<string, byte[]> Run()
    {
        var error = Marshal.AllocHGlobal(ErrorBytes);
        var name = Marshal.StringToHGlobalAnsi("package.cab");
        var folder = Marshal.StringToHGlobalAnsi(string.Empty);
        var context = IntPtr.Zero;
        try
        {
            Marshal.Copy(new byte[ErrorBytes], 0, error, ErrorBytes);
            context = FDICreate(_alloc, _free, _open, _read, _write, _close, _seek, CpuUnknown, error);
            if (context == IntPtr.Zero)
            {
                throw Failed("The cabinet library did not start.");
            }

            // The name and folder only make up the path FDI hands to Open, which always opens this cabinet.
            var unpacked = FDICopy(context, name, folder, 0, _notify, IntPtr.Zero, IntPtr.Zero);
            _cancellationToken.ThrowIfCancellationRequested();
            if (_failure is PolicyTemplatePackageException or OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(_failure).Throw();
            }

            if (_failure is not null)
            {
                throw Failed("The cabinet cannot be unpacked.", _failure);
            }

            if (!unpacked)
            {
                throw Failed($"The cabinet cannot be unpacked (FDI error {Marshal.ReadInt32(error)}).");
            }

            return _files;
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                _ = FDIDestroy(context);
            }

            Marshal.FreeHGlobal(folder);
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(error);
            GC.KeepAlive(this);
        }
    }

    // ---- FDI callbacks ---------------------------------------------------------------------------------------------------

    private static IntPtr Alloc(uint size)
    {
        try
        {
            return Marshal.AllocHGlobal((nint)size);
        }
        catch (OutOfMemoryException)
        {
            return IntPtr.Zero;
        }
    }

    private static void Free(IntPtr memory) => Marshal.FreeHGlobal(memory);

    /// <summary>FDI opens the cabinet: always the one of this unpacker, read-only (a set's next cabinet is refused in <see cref="Notify"/>).</summary>
    private IntPtr Open(IntPtr path, int flags, int mode)
    {
        try
        {
            return (flags & WriteAccess) != 0 || _failure is not null ? Failure : Add(new CabinetPosition());
        }
        catch (Exception ex)
        {
            return Refuse(ex);
        }
    }

    private uint Read(IntPtr handle, IntPtr buffer, uint count)
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_handles.TryGetValue(handle, out var item) || item is not CabinetPosition cabinet)
            {
                return uint.MaxValue;
            }

            var wanted = (int)Math.Min(count, Math.Max(0, _length - cabinet.Position));
            if (_buffer.Length < wanted)
            {
                _buffer = new byte[wanted];
            }

            var filled = 0;
            while (filled < wanted)
            {
                var read = RandomAccess.Read(_cabinet, _buffer.AsSpan(filled, wanted - filled), cabinet.Position + filled);
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            Marshal.Copy(_buffer, 0, buffer, filled);
            cabinet.Position += filled;
            return (uint)filled;
        }
        catch (Exception ex)
        {
            _failure ??= ex;
            return uint.MaxValue;
        }
    }

    private uint Write(IntPtr handle, IntPtr buffer, uint count)
    {
        try
        {
            if (!_handles.TryGetValue(handle, out var item) || item is not OutputFile file)
            {
                return uint.MaxValue;
            }

            if (count > file.Content.Length - file.Written)
            {
                _failure ??= Failed("A file of the cabinet is longer than its entry says.");
                return uint.MaxValue;
            }

            Marshal.Copy(buffer, file.Content, file.Written, (int)count);
            file.Written += (int)count;
            return count;
        }
        catch (Exception ex)
        {
            _failure ??= ex;
            return uint.MaxValue;
        }
    }

    private int Close(IntPtr handle)
    {
        try
        {
            return _handles.Remove(handle) ? 0 : -1;
        }
        catch (Exception ex)
        {
            _failure ??= ex;
            return -1;
        }
    }

    private int Seek(IntPtr handle, int distance, int origin)
    {
        try
        {
            if (!_handles.TryGetValue(handle, out var item) || item is not CabinetPosition cabinet)
            {
                return -1;
            }

            var target = origin switch
            {
                0 => distance,
                1 => cabinet.Position + distance,
                2 => _length + distance,
                _ => -1L,
            };
            if (target < 0 || target > int.MaxValue)
            {
                return -1;
            }

            cabinet.Position = target;
            return (int)target;
        }
        catch (Exception ex)
        {
            _failure ??= ex;
            return -1;
        }
    }

    private IntPtr Notify(int type, IntPtr notification)
    {
        try
        {
            var info = Marshal.PtrToStructure<FdiNotification>(notification);
            return type switch
            {
                CopyFileNotification => CopyFile(info),
                CloseFileInfoNotification => CloseFile(info),
                PartialFileNotification or NextCabinetNotification => Refuse(Failed("The cabinet is part of a set of cabinets.")),
                _ => IntPtr.Zero, // fdintCABINET_INFO, fdintENUMERATE: continue
            };
        }
        catch (Exception ex)
        {
            return Refuse(ex);
        }
    }

    /// <summary><c>fdintCOPY_FILE</c>: a buffer for a template or a ZIP archive (0 skips any other file, -1 stops).</summary>
    private IntPtr CopyFile(FdiNotification info)
    {
        var name = (info.Attributes & NameIsUtf8) != 0 ? Marshal.PtrToStringUTF8(info.Name) : Marshal.PtrToStringAnsi(info.Name);
        if (!SysvolPaths.TryNormalize(name, out var path))
        {
            return Refuse(Failed("A file of the cabinet is not a plain relative path."));
        }

        var archive = IsZip(path);
        if ((!archive && !IsTemplate(path)) || _files.ContainsKey(path))
        {
            return IntPtr.Zero; // not needed, or a name repeated in the cabinet (the first one stays)
        }

        long size = info.Size;
        if (size < 0 || size > (archive ? PolicyTemplatePackage.MaxArchiveBytes : PolicyTemplatePackage.MaxEntryBytes)
            || _kept + size > PolicyTemplatePackage.MaxArchiveBytes)
        {
            return Refuse(new PolicyTemplatePackageException(PolicyTemplatePackageError.TooLarge, "The cabinet is too large."));
        }

        _kept += size;
        return Add(new OutputFile(path, new byte[size]));
    }

    /// <summary><c>fdintCLOSE_FILE_INFO</c>: the file is complete (TRUE continues; FALSE stops).</summary>
    private IntPtr CloseFile(FdiNotification info)
    {
        if (!_handles.Remove(info.Handle, out var item) || item is not OutputFile file)
        {
            _failure ??= Failed("The cabinet library closed an unknown file.");
            return IntPtr.Zero;
        }

        if (file.Written != file.Content.Length)
        {
            _failure ??= Failed("A file of the cabinet is shorter than its entry says.");
            return IntPtr.Zero;
        }

        _files.TryAdd(file.Path, file.Content);
        return new IntPtr(1);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------------

    private IntPtr Add(object item)
    {
        var handle = new IntPtr(++_lastHandle);
        _handles[handle] = item;
        return handle;
    }

    private IntPtr Refuse(Exception reason)
    {
        _failure ??= reason;
        return Failure;
    }

    private static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsTemplate(string path) =>
        path.EndsWith(".admx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".adml", StringComparison.OrdinalIgnoreCase);

    private static PolicyTemplatePackageException Failed(string message, Exception? innerException = null) =>
        new(PolicyTemplatePackageError.ExtractionFailed, message, innerException);

    /// <summary>The read position of one open of the cabinet.</summary>
    private sealed class CabinetPosition
    {
        public long Position { get; set; }
    }

    /// <summary>A file being unpacked: its buffer has the size its entry declares.</summary>
    private sealed class OutputFile(string path, byte[] content)
    {
        public string Path { get; } = path;

        public byte[] Content { get; } = content;

        public int Written { get; set; }
    }

    // ---- cabinet.dll (fdi.h) ---------------------------------------------------------------------------------------------

    /// <summary>
    /// FDINOTIFICATION. fdi.h packs its structures to 4 bytes only on 32-bit Windows (<c>#ifndef _WIN64</c>), so the
    /// natural layout of a sequential structure matches on both.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FdiNotification
    {
        public int Size;
        public IntPtr Name;
        public IntPtr Name2;
        public IntPtr Name3;
        public IntPtr User;
        public IntPtr Handle;
        public ushort Date;
        public ushort Time;
        public ushort Attributes;
        public ushort SetId;
        public ushort CabinetIndex;
        public ushort FolderIndex;
        public int Error;
    }

    [DllImport("cabinet.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr FDICreate(AllocCallback alloc, FreeCallback free, OpenCallback open, ReadCallback read, WriteCallback write,
        CloseCallback close, SeekCallback seek, int cpuType, IntPtr error);

    [DllImport("cabinet.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FDICopy(IntPtr context, IntPtr cabinetName, IntPtr cabinetPath, int flags, NotifyCallback notify, IntPtr decrypt, IntPtr user);

    [DllImport("cabinet.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FDIDestroy(IntPtr context);
}
