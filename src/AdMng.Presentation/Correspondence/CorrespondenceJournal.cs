using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdMng.Core.Correspondence.Journal;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Correspondence;

/// <summary>What <see cref="CorrespondenceJournal.LoadAsync"/> found.</summary>
/// <param name="Entries">The entries kept.</param>
/// <param name="Pruned">Entries older than the retention that were removed.</param>
/// <param name="ResetBackup">The key could not be read (or the journal had a format this version does not read): a new key
/// was made and the old journal renamed to this file (<c>journal-&lt;date&gt;.old</c>, full path); null otherwise.</param>
public sealed record CorrespondenceJournalLoad(int Entries, int Pruned, string? ResetBackup);

/// <summary>
/// "Już wysłane" (application singleton, design note 6.2 and 7.6): <c>&lt;SettingsDirectory&gt;/correspondence/journal.txt</c>
/// with one line per sent address – the keyed hash (<see cref="JournalHashing"/>), the time and the subject as typed – and
/// <c>journal.key</c> (32 random bytes protected by <see cref="ICorrespondenceKeyProtector"/>). Never the address (rule 1
/// as amended for U5). Each line is written and flushed at once (FileShare.ReadWrite, so other windows and AD Manager
/// processes can read and append); pruning rewrites the file atomically (temporary file + move) after reading it again.
/// Thread-safe within a process; across instances and processes every file operation holds <c>journal.lock</c> (an empty
/// file opened exclusively), so appends of two processes never overwrite each other and a rewrite never loses a line.
/// The constructor never touches the file system.
/// </summary>
/// <remarks>
/// A key file that cannot be decrypted (another user, a restored profile, a damaged file) or that does not hold
/// <see cref="JournalHashing.KeyBytes"/> bytes cannot hash an address the way the journal was written: a new key is made
/// and the old journal is kept as <c>journal-&lt;date&gt;.old</c> (<see cref="CorrespondenceJournalLoad.ResetBackup"/>), so
/// nothing is silently mixed. A protector that is missing altogether (a host without DPAPI) is an error, never a reset.
/// </remarks>
public sealed class CorrespondenceJournal
{
    public const string FolderName = "correspondence";

    public const string FileName = "journal.txt";

    public const string KeyFileName = "journal.key";

    /// <summary>The empty file that serializes the file operations of every instance and process.</summary>
    public const string LockFileName = "journal.lock";

    /// <summary>How long a file operation waits for another instance or process to release <see cref="LockFileName"/>.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

    private const string HeaderPrefix = "AdMng.CorrespondenceJournal\t";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Serializes the file operations of this process (loads, appends, prunes, clears).</summary>
    private readonly SemaphoreSlim _files = new(1, 1);

    /// <summary>Guards the key and the entries in memory.</summary>
    private readonly Lock _state = new();

    private readonly List<JournalEntry> _entries = [];
    private readonly Dictionary<string, JournalEntry> _latest = new(StringComparer.Ordinal);
    private byte[]? _key;

    public CorrespondenceJournal(IAppPaths paths, ICorrespondenceKeyProtector protector, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Protector = protector ?? throw new ArgumentNullException(nameof(protector));
        Time = time ?? throw new ArgumentNullException(nameof(time));
        Folder = Path.Combine(paths.SettingsDirectory, FolderName);
    }

    /// <summary>The journal folder (shown by "Sprawdź Outlooka").</summary>
    public string Folder { get; }

    public string FilePath => Path.Combine(Folder, FileName);

    public string KeyPath => Path.Combine(Folder, KeyFileName);

    public string LockPath => Path.Combine(Folder, LockFileName);

    /// <summary>The key was read (or made) by <see cref="LoadAsync"/>: <see cref="Find"/> and <see cref="AppendAsync"/> work.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_state)
            {
                return _key is not null;
            }
        }
    }

    /// <summary>Entries in the journal.</summary>
    public int Count
    {
        get
        {
            lock (_state)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The oldest entry's time, or null.</summary>
    public DateTimeOffset? Oldest
    {
        get
        {
            lock (_state)
            {
                return _entries.Count == 0 ? null : _entries.Min(e => e.SentAt);
            }
        }
    }

    /// <summary>The newest entry's time, or null.</summary>
    public DateTimeOffset? Newest
    {
        get
        {
            lock (_state)
            {
                return _entries.Count == 0 ? null : _entries.Max(e => e.SentAt);
            }
        }
    }

    internal ICorrespondenceKeyProtector Protector { get; }

    internal TimeProvider Time { get; }

    /// <summary>
    /// Reads the key (a new one when there is none; see the remarks for an unreadable one) and the journal, and prunes entries
    /// older than <paramref name="retention"/>. Every window calls it when it opens, so entries other AD Manager processes
    /// appended are read too.
    /// </summary>
    /// <exception cref="IOException">The folder or a file cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">No access to the folder.</exception>
    /// <exception cref="PlatformNotSupportedException">The host has no key protector.</exception>
    public async Task<CorrespondenceJournalLoad> LoadAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.Zero);
        await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Locked(() => Load(retention)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _files.Release();
        }
    }

    /// <summary>The latest entry of <paramref name="address"/>, or null (also before <see cref="LoadAsync"/> and for a value
    /// that is no address).</summary>
    public JournalEntry? Find(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        lock (_state)
        {
            return _key is { } key && HashOf(key, address) is { } hash ? _latest.GetValueOrDefault(hash) : null;
        }
    }

    /// <summary>Records that <paramref name="address"/> was sent <paramref name="subject"/> (as typed) now: the line is
    /// written and flushed before this returns.</summary>
    /// <exception cref="InvalidOperationException">The journal was not loaded.</exception>
    /// <exception cref="ArgumentException"><paramref name="address"/> is blank.</exception>
    /// <exception cref="IOException">The line cannot be written.</exception>
    public async Task AppendAsync(string address, string subject, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        string hash;
        lock (_state)
        {
            var key = _key ?? throw new InvalidOperationException("The correspondence journal is not loaded.");
            hash = HashOf(key, address) ?? throw new ArgumentException("Not an e-mail address.", nameof(address));
        }

        var now = Time.GetUtcNow();
        var entry = new JournalEntry(hash, new DateTimeOffset(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero), subject ?? string.Empty);
        var line = JournalFile.Line(entry);
        await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => Locked(() => AppendLine(line)), CancellationToken.None).ConfigureAwait(false);
            lock (_state)
            {
                Add(entry);
            }
        }
        finally
        {
            _files.Release();
        }
    }

    /// <summary>Removes entries older than <paramref name="retention"/>; returns how many were removed.</summary>
    /// <exception cref="InvalidOperationException">The journal was not loaded.</exception>
    public async Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.Zero);
        RequireLoaded();
        await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Locked(() => Prune(retention)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _files.Release();
        }
    }

    /// <summary>Removes every entry (the key stays).</summary>
    /// <exception cref="InvalidOperationException">The journal was not loaded.</exception>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        RequireLoaded();
        await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => Locked(() => Rewrite([])), cancellationToken).ConfigureAwait(false);
            lock (_state)
            {
                Replace([]);
            }
        }
        finally
        {
            _files.Release();
        }
    }

    public override string ToString() => nameof(CorrespondenceJournal);

    private static string? HashOf(byte[] key, string address)
    {
        try
        {
            return JournalHashing.Hash(key, address);
        }
        catch (ArgumentException)
        {
            return null;   // blank after normalization
        }
    }

    /// <summary>Runs <paramref name="action"/> while holding <see cref="LockFileName"/> (see <see cref="AcquireLock"/>).</summary>
    private T Locked<T>(Func<T> action)
    {
        using var held = AcquireLock();
        return action();
    }

    private void Locked(Action action)
    {
        using var held = AcquireLock();
        action();
    }

    /// <summary>
    /// Opens <see cref="LockFileName"/> exclusively (FileShare.None: a share mode on Windows, an exclusive <c>flock</c> on
    /// Linux), waiting while another instance or process holds it – at most <see cref="LockTimeout"/>, then the last
    /// sharing violation is thrown.
    /// </summary>
    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(Folder);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
                && Stopwatch.GetElapsedTime(started) < LockTimeout)
            {
                // Held by another window or process for one short write: try again shortly.
                Thread.Sleep(Random.Shared.Next(5, 25));
            }
        }
    }

    private CorrespondenceJournalLoad Load(TimeSpan retention)
    {
        string? backup = null;
        var key = ReadKey(out var unreadable);
        if (key is null)
        {
            // Protected first: a host without a protector fails here, before anything is moved.
            key = RandomNumberGenerator.GetBytes(JournalHashing.KeyBytes);
            var protectedKey = Protector.Protect(key);

            // A journal written with a lost or unreadable key can never be matched again: keep it aside, start a new one.
            if (unreadable || HasEntries())
            {
                backup = MoveAside();
            }

            WriteKey(protectedKey);
        }

        var entries = ReadEntries(out var otherFormat);
        if (otherFormat)
        {
            backup ??= MoveAside();
            entries = [];
        }

        EnsureHeader();
        lock (_state)
        {
            if (_key is { } previous && !ReferenceEquals(previous, key))
            {
                CryptographicOperations.ZeroMemory(previous);
            }

            _key = key;
            Replace(entries);
        }

        var pruned = Prune(retention);
        return new CorrespondenceJournalLoad(Count, pruned, backup);
    }

    /// <summary>The key, or null when there is none (<paramref name="unreadable"/> false) or it cannot be used (true).</summary>
    private byte[]? ReadKey(out bool unreadable)
    {
        unreadable = false;
        if (!File.Exists(KeyPath))
        {
            return null;
        }

        var protectedKey = File.ReadAllBytes(KeyPath);
        byte[] key;
        try
        {
            key = Protector.Unprotect(protectedKey);
        }
        catch (CryptographicException)
        {
            unreadable = true;
            return null;
        }

        if (key is not { Length: JournalHashing.KeyBytes })
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            unreadable = true;
            return null;
        }

        return key;
    }

    private void WriteKey(byte[] protectedKey)
    {
        var temporary = Path.Combine(Folder, $".{KeyFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, protectedKey);
            File.Move(temporary, KeyPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private bool HasEntries()
    {
        if (!File.Exists(FilePath))
        {
            return false;
        }

        using var reader = OpenReader();
        return JournalFile.Read(reader).Count > 0;
    }

    private List<JournalEntry> ReadEntries(out bool otherFormat)
    {
        otherFormat = false;
        if (!File.Exists(FilePath))
        {
            return [];
        }

        using (var reader = OpenReader())
        {
            var first = reader.ReadLine()?.TrimStart('﻿').TrimEnd();
            if (first is not null && first != JournalFile.Header && first.StartsWith(HeaderPrefix, StringComparison.Ordinal))
            {
                otherFormat = true;
                return [];
            }
        }

        using var again = OpenReader();
        return [.. JournalFile.Read(again)];
    }

    private StreamReader OpenReader() =>
        new(new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Utf8, detectEncodingFromByteOrderMarks: true);

    /// <summary>Renames the journal to <c>journal-&lt;date&gt;.old</c> (a free name); returns its path, or null when there is none.</summary>
    private string? MoveAside()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var date = Time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var target = Path.Combine(Folder, $"journal-{date}.old");
        for (var number = 2; File.Exists(target); number++)
        {
            target = Path.Combine(Folder, $"journal-{date}-{number}.old");
        }

        File.Move(FilePath, target);
        return target;
    }

    private void EnsureHeader()
    {
        using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
        {
            stream.Write(Utf8.GetBytes(JournalFile.Header + "\n"));
            stream.Flush(flushToDisk: true);
        }
    }

    private void AppendLine(string line)
    {
        using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var text = stream.Length == 0 ? JournalFile.Header + "\n" + line + "\n" : line + "\n";
        stream.Write(Utf8.GetBytes(text));
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Reads the file again (lines other processes appended stay), drops entries older than the retention and
    /// rewrites the file when anything was dropped. Called with the file gate and the lock file held.</summary>
    private int Prune(TimeSpan retention)
    {
        var cutoff = Time.GetUtcNow() - retention;
        var current = ReadEntries(out var otherFormat);
        if (otherFormat)
        {
            current = [];
        }

        var kept = current.Where(e => e.SentAt >= cutoff).ToList();
        var removed = current.Count - kept.Count;
        if (removed > 0)
        {
            Rewrite(kept);
        }

        lock (_state)
        {
            Replace(kept);
        }

        return removed;
    }

    /// <summary>The header and <paramref name="entries"/> written to a temporary file that then replaces the journal.</summary>
    private void Rewrite(IReadOnlyList<JournalEntry> entries)
    {
        var temporary = Path.Combine(Folder, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var text = new StringBuilder(JournalFile.Header).Append('\n');
                foreach (var entry in entries)
                {
                    text.Append(JournalFile.Line(entry)).Append('\n');
                }

                stream.Write(Utf8.GetBytes(text.ToString()));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void RequireLoaded()
    {
        if (!IsLoaded)
        {
            throw new InvalidOperationException("The correspondence journal is not loaded.");
        }
    }

    /// <summary>Replaces the entries in memory (called under <see cref="_state"/>).</summary>
    private void Replace(IEnumerable<JournalEntry> entries)
    {
        _entries.Clear();
        _latest.Clear();
        foreach (var entry in entries)
        {
            Add(entry);
        }
    }

    /// <summary>Adds an entry in memory; the latest entry of an address wins (called under <see cref="_state"/>).</summary>
    private void Add(JournalEntry entry)
    {
        _entries.Add(entry);
        if (!_latest.TryGetValue(entry.AddressHash, out var existing) || entry.SentAt >= existing.SentAt)
        {
            _latest[entry.AddressHash] = entry;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temporary file is harmless; the next rewrite uses another name.
        }
    }
}
