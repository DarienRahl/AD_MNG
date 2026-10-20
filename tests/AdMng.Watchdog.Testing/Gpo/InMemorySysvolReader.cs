using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Testing.Gpo;

/// <summary>
/// In-memory <see cref="ISysvolReader"/> (stage B design 8.3) for tests: GPO folders keyed by GUID, folders derived from the
/// file paths, raw (hostile) listing entries, injected failures per folder or file, and a log of every call with the
/// folder it addressed – so a test can prove that no read ever went to a host other than the pinned DC.
/// </summary>
public sealed class InMemorySysvolReader : ISysvolReader
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, GpoFolderState> _folders = [];
    private readonly List<SysvolCall> _calls = [];

    /// <summary>Default modification time of added files.</summary>
    public DateTimeOffset DefaultModifiedAt { get; set; } = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Every call in order.</summary>
    public IReadOnlyList<SysvolCall> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Adds or replaces a file ('/'-separated path below the GPO folder).</summary>
    public InMemorySysvolReader SetFile(Guid gpo, string path, byte[] content, DateTimeOffset? modifiedAt = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        lock (_lock)
        {
            Folder(gpo).Files[path] = (content.ToArray(), modifiedAt ?? DefaultModifiedAt);
        }

        return this;
    }

    /// <summary>Removes a file.</summary>
    public InMemorySysvolReader RemoveFile(Guid gpo, string path)
    {
        lock (_lock)
        {
            Folder(gpo).Files.Remove(path);
        }

        return this;
    }

    /// <summary>Adds a listing entry as is (no file behind it unless <see cref="SetFile"/> adds one) – traversal paths,
    /// negative sizes, huge trees.</summary>
    public InMemorySysvolReader AddRawEntry(Guid gpo, SysvolEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            Folder(gpo).RawEntries.Add(entry);
        }

        return this;
    }

    /// <summary>Makes the listing (and every read) of the folder fail.</summary>
    public InMemorySysvolReader FailFolder(Guid gpo, SysvolErrorKind kind, string? ntStatus = null)
    {
        lock (_lock)
        {
            Folder(gpo).Failure = (kind, ntStatus);
        }

        return this;
    }

    /// <summary>Makes the read of one file fail.</summary>
    public InMemorySysvolReader FailFile(Guid gpo, string path, SysvolErrorKind kind, string? ntStatus = null)
    {
        lock (_lock)
        {
            Folder(gpo).FileFailures[path] = (kind, ntStatus);
        }

        return this;
    }

    public Task<SysvolListing> ListAsync(SysvolGpoFolder folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _calls.Add(new SysvolCall(folder, "list", []));
            if (!_folders.TryGetValue(folder.GpoGuid, out var state))
            {
                throw new SysvolReadException(SysvolErrorKind.NotFound, "NT_STATUS_OBJECT_NAME_NOT_FOUND", "The GPO folder does not exist.");
            }

            ThrowIfFailing(state);
            var entries = new List<SysvolEntry>();
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, (content, modifiedAt)) in state.Files)
            {
                var parts = path.Split('/');
                for (var i = 1; i < parts.Length; i++)
                {
                    var directory = string.Join('/', parts[..i]);
                    if (directories.Add(directory))
                    {
                        entries.Add(new SysvolEntry { RelativePath = directory, IsDirectory = true, ModifiedAt = modifiedAt });
                    }
                }

                entries.Add(new SysvolEntry { RelativePath = path, Size = content.LongLength, ModifiedAt = modifiedAt });
            }

            entries.AddRange(state.RawEntries);
            return Task.FromResult(new SysvolListing { Entries = entries });
        }
    }

    public Task<IReadOnlyList<SysvolFile>> ReadAsync(SysvolGpoFolder folder, IReadOnlyList<string> relativePaths, long maxBytesPerFile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(relativePaths);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _calls.Add(new SysvolCall(folder, "read", [.. relativePaths]));
            if (!_folders.TryGetValue(folder.GpoGuid, out var state))
            {
                throw new SysvolReadException(SysvolErrorKind.NotFound, "NT_STATUS_OBJECT_NAME_NOT_FOUND", "The GPO folder does not exist.");
            }

            ThrowIfFailing(state);
            var result = new List<SysvolFile>();
            foreach (var path in relativePaths)
            {
                var match = state.Files.Keys.FirstOrDefault(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (state.FileFailures.TryGetValue(path, out var failure))
                {
                    result.Add(new SysvolFile { RelativePath = path, Error = failure.Kind, NtStatus = failure.NtStatus });
                }
                else if (match is null)
                {
                    result.Add(new SysvolFile { RelativePath = path, Error = SysvolErrorKind.NotFound, NtStatus = "NT_STATUS_OBJECT_NAME_NOT_FOUND" });
                }
                else if (state.Files[match].Content.LongLength > maxBytesPerFile)
                {
                    result.Add(new SysvolFile { RelativePath = path, Error = SysvolErrorKind.TooLarge });
                }
                else
                {
                    result.Add(new SysvolFile { RelativePath = path, Content = state.Files[match].Content.ToArray() });
                }
            }

            return Task.FromResult<IReadOnlyList<SysvolFile>>(result);
        }
    }

    private GpoFolderState Folder(Guid gpo)
    {
        if (!_folders.TryGetValue(gpo, out var state))
        {
            state = new GpoFolderState();
            _folders[gpo] = state;
        }

        return state;
    }

    private static void ThrowIfFailing(GpoFolderState state)
    {
        if (state.Failure is { } failure)
        {
            throw new SysvolReadException(failure.Kind, failure.NtStatus, "Injected SYSVOL failure.");
        }
    }

    private sealed class GpoFolderState
    {
        public Dictionary<string, (byte[] Content, DateTimeOffset ModifiedAt)> Files { get; } = new(StringComparer.Ordinal);
        public List<SysvolEntry> RawEntries { get; } = [];
        public Dictionary<string, (SysvolErrorKind Kind, string? NtStatus)> FileFailures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public (SysvolErrorKind Kind, string? NtStatus)? Failure { get; set; }
    }
}

/// <summary>One call of <see cref="InMemorySysvolReader"/>.</summary>
public sealed record SysvolCall(SysvolGpoFolder Folder, string Operation, IReadOnlyList<string> Paths);
