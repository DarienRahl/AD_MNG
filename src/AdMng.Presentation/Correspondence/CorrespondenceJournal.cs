using AdMng.Core.Correspondence.Journal;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Correspondence;

/// <summary>What <see cref="CorrespondenceJournal.LoadAsync"/> found.</summary>
/// <param name="Entries">The entries kept.</param>
/// <param name="Pruned">Entries older than the retention that were removed.</param>
/// <param name="ResetBackup">The key could not be read: a new key was made and the old journal renamed to this file
/// (<c>journal-&lt;date&gt;.old</c>); null otherwise.</param>
public sealed record CorrespondenceJournalLoad(int Entries, int Pruned, string? ResetBackup);

/// <summary>
/// "Już wysłane" (application singleton, design note 6.2 and 7.6): <c>&lt;SettingsDirectory&gt;/correspondence/journal.txt</c>
/// with one line per sent address – the keyed hash (<see cref="JournalHashing"/>), the time and the subject as typed – and
/// <c>journal.key</c> (32 random bytes protected by <see cref="ICorrespondenceKeyProtector"/>). Never the address (rule 1
/// as amended for U5). Each line is written and flushed at once (FileShare.ReadWrite); pruning rewrites the file atomically;
/// thread-safe. The constructor never touches the file system.
/// </summary>
public sealed class CorrespondenceJournal
{
    private const string Track = "U5 T4 shell";

    public const string FolderName = "correspondence";

    public const string FileName = "journal.txt";

    public const string KeyFileName = "journal.key";

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

    /// <summary>Entries in the journal.</summary>
    public int Count => 0;

    /// <summary>The oldest entry's time, or null.</summary>
    public DateTimeOffset? Oldest => null;

    /// <summary>The newest entry's time, or null.</summary>
    public DateTimeOffset? Newest => null;

    internal ICorrespondenceKeyProtector Protector { get; }

    internal TimeProvider Time { get; }

    /// <summary>Reads the key and the journal and prunes entries older than <paramref name="retention"/>.</summary>
    public Task<CorrespondenceJournalLoad> LoadAsync(TimeSpan retention, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(Track);

    /// <summary>The latest entry of <paramref name="address"/>, or null.</summary>
    public JournalEntry? Find(string address) => throw new NotImplementedException(Track);

    /// <summary>Records that <paramref name="address"/> was sent <paramref name="subject"/> (as typed) now.</summary>
    public Task AppendAsync(string address, string subject, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(Track);

    /// <summary>Removes entries older than <paramref name="retention"/>; returns how many were removed.</summary>
    public Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(Track);

    /// <summary>Removes every entry.</summary>
    public Task ClearAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException(Track);

    public override string ToString() => nameof(CorrespondenceJournal);
}
