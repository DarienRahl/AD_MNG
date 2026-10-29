using System.Text;
using AdMng.Core.Correspondence.Journal;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Tests.TestDoubles;
using ManualTimeProvider = AdMng.Testing.ManualTimeProvider;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// "Już wysłane" (design note 6.2 and 7.6): keyed hashes only, a line flushed per send, prune and clear rewrite the file
/// atomically, an unreadable key starts a new journal and keeps the old one aside, several writers at once.
/// </summary>
public sealed class CorrespondenceJournalTests : IDisposable
{
    private static readonly TimeSpan Year = TimeSpan.FromDays(365);

    private readonly TempAppPaths _paths = new();
    private readonly ManualTimeProvider _time = new();
    private readonly CorrespondenceShellScenario.XorKeyProtector _protector = new();
    private readonly CorrespondenceJournal _journal;

    public CorrespondenceJournalTests()
    {
        _journal = new CorrespondenceJournal(_paths, _protector, _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _paths.Dispose();

    [Fact]
    public void Constructor_NeverTouchesTheFileSystem()
    {
        Assert.False(Directory.Exists(_journal.Folder));
        Assert.Equal(Path.Combine(_paths.SettingsDirectory, "correspondence"), _journal.Folder);
        Assert.Equal(Path.Combine(_journal.Folder, "journal.txt"), _journal.FilePath);
        Assert.Equal(Path.Combine(_journal.Folder, "journal.key"), _journal.KeyPath);
        Assert.False(_journal.IsLoaded);
        Assert.Null(_journal.Find("jan.kowalski@contoso.test"));
        Assert.Equal(nameof(CorrespondenceJournal), _journal.ToString());
    }

    [Fact]
    public async Task Load_MakesAProtectedKeyAndAnEmptyJournal()
    {
        var load = await _journal.LoadAsync(Year, Ct);

        Assert.Equal(new CorrespondenceJournalLoad(0, 0, null), load);
        Assert.True(_journal.IsLoaded);
        Assert.Equal(0, _journal.Count);
        Assert.Null(_journal.Oldest);
        Assert.Null(_journal.Newest);
        Assert.Equal([JournalFile.Header], File.ReadAllLines(_journal.FilePath));
        var key = File.ReadAllBytes(_journal.KeyPath);
        Assert.Equal(4 + JournalHashing.KeyBytes, key.Length);
        Assert.Equal(1, _protector.Protected);
        Assert.Empty(Directory.GetFiles(_journal.Folder, "*.tmp"));
    }

    [Fact]
    public async Task Append_WritesAHashLineAtOnce_AndFindNormalizesTheAddress()
    {
        await _journal.LoadAsync(Year, Ct);

        await _journal.AppendAsync("Małgorzata.Wójcik@Contoso.TEST", "Zaproszenie na szkolenie – Łódź, 12 listopada", Ct);

        // The line is on disk before the call returns, readable while the journal is open for appending.
        string content;
        using (var reader = new StreamReader(new FileStream(_journal.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
        {
            content = await reader.ReadToEndAsync(Ct);
        }

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var entry = Assert.Single(JournalFile.Read(new StringReader(content)));
        Assert.True(JournalHashing.IsHash(entry.AddressHash));
        Assert.Equal(_time.GetUtcNow(), entry.SentAt);
        Assert.Equal("Zaproszenie na szkolenie – Łódź, 12 listopada", entry.Subject);

        // Rule 1: never the address (nor its parts) in the file.
        foreach (var part in new[] { "małgorzata", "wójcik", "contoso", "@", "Małgorzata" })
        {
            Assert.DoesNotContain(part, content, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(entry, _journal.Find("  Małgorzata.Wójcik@contoso.test "));
        Assert.Null(_journal.Find("malgorzata.wojcik@contoso.test"));   // another address (no diacritics)
        Assert.Equal(entry, _journal.Find("mailto:małgorzata.wójcik@contoso.test"));
        Assert.Equal(entry, _journal.Find("MAŁGORZATA.WÓJCIK@CONTOSO.TEST"));
        Assert.Null(_journal.Find("jan.kowalski@contoso.test"));
        Assert.Null(_journal.Find(" "));
        Assert.Equal(1, _journal.Count);
        Assert.Equal(nameof(JournalEntry), entry.ToString());
    }

    [Fact]
    public async Task Find_TheLatestEntryWins_AndCountsAreKept()
    {
        await _journal.LoadAsync(Year, Ct);
        var first = _time.GetUtcNow();
        await _journal.AppendAsync("jan.kowalski@contoso.test", "Pierwsza wiadomość", Ct);
        _time.Advance(TimeSpan.FromDays(3));
        await _journal.AppendAsync("lukasz.wisniewski@contoso.test", "Inna", Ct);
        _time.Advance(TimeSpan.FromHours(5).Add(TimeSpan.FromMilliseconds(700)));
        await _journal.AppendAsync("Jan.Kowalski@contoso.test", "Druga wiadomość", Ct);

        Assert.Equal("Druga wiadomość", _journal.Find("jan.kowalski@contoso.test")!.Subject);
        Assert.Equal(3, _journal.Count);
        Assert.Equal(first, _journal.Oldest);
        Assert.Equal(0, _journal.Newest!.Value.Millisecond);   // whole seconds, as the file stores them

        // Another window (or process) reads the same file.
        var other = new CorrespondenceJournal(_paths, _protector, _time);
        await other.LoadAsync(Year, Ct);
        Assert.Equal(3, other.Count);
        Assert.Equal("Druga wiadomość", other.Find("jan.kowalski@contoso.test")!.Subject);
        Assert.Equal(_journal.Newest, other.Newest);
    }

    [Fact]
    public async Task AppendAndFind_BeforeLoad_AreRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _journal.AppendAsync("jan.kowalski@contoso.test", "Temat", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _journal.PruneAsync(Year, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _journal.ClearAsync(Ct));
        await _journal.LoadAsync(Year, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => _journal.AppendAsync("  ", "Temat", Ct));
    }

    [Fact]
    public async Task Prune_RemovesOldEntries_AndRewritesTheFileAtomically()
    {
        await _journal.LoadAsync(Year, Ct);
        await _journal.AppendAsync("jan.kowalski@contoso.test", "Stara", Ct);
        _time.Advance(TimeSpan.FromDays(200));
        await _journal.AppendAsync("pawel.lewandowski@contoso.test", "Nowsza", Ct);
        _time.Advance(TimeSpan.FromDays(200));

        Assert.Equal(1, await _journal.PruneAsync(Year, Ct));

        Assert.Equal(1, _journal.Count);
        Assert.Null(_journal.Find("jan.kowalski@contoso.test"));
        Assert.Equal("Nowsza", _journal.Find("pawel.lewandowski@contoso.test")!.Subject);
        var lines = File.ReadAllLines(_journal.FilePath);
        Assert.Equal(JournalFile.Header, lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.Empty(Directory.GetFiles(_journal.Folder, "*.tmp"));
        Assert.Equal(0, await _journal.PruneAsync(Year, Ct));

        // Loading prunes with the retention of the settings.
        _time.Advance(TimeSpan.FromDays(200));
        var load = await new CorrespondenceJournal(_paths, _protector, _time).LoadAsync(Year, Ct);
        Assert.Equal(new CorrespondenceJournalLoad(0, 1, null), load);
    }

    [Fact]
    public async Task Prune_KeepsLinesAnotherProcessAppended()
    {
        await _journal.LoadAsync(Year, Ct);
        var other = new CorrespondenceJournal(_paths, _protector, _time);
        await other.LoadAsync(Year, Ct);
        await _journal.AppendAsync("jan.kowalski@contoso.test", "Stara", Ct);
        _time.Advance(TimeSpan.FromDays(400));
        await other.AppendAsync("zaneta.szymanska@contoso.test", "Z drugiego okna", Ct);

        Assert.Equal(1, await _journal.PruneAsync(Year, Ct));

        Assert.Equal("Z drugiego okna", _journal.Find("zaneta.szymanska@contoso.test")!.Subject);
        Assert.Equal(1, _journal.Count);
    }

    [Fact]
    public async Task Clear_RemovesEveryEntry_AndKeepsTheKey()
    {
        await _journal.LoadAsync(Year, Ct);
        await _journal.AppendAsync("jan.kowalski@contoso.test", "Temat", Ct);
        var key = File.ReadAllBytes(_journal.KeyPath);

        await _journal.ClearAsync(Ct);

        Assert.Equal(0, _journal.Count);
        Assert.Null(_journal.Find("jan.kowalski@contoso.test"));
        Assert.Equal([JournalFile.Header], File.ReadAllLines(_journal.FilePath));
        Assert.Equal(key, File.ReadAllBytes(_journal.KeyPath));
        await _journal.AppendAsync("jan.kowalski@contoso.test", "Po wyczyszczeniu", Ct);
        Assert.Equal("Po wyczyszczeniu", _journal.Find("jan.kowalski@contoso.test")!.Subject);
    }

    [Fact]
    public async Task UnreadableKey_StartsANewJournal_AndKeepsTheOldOneAside()
    {
        await _journal.LoadAsync(Year, Ct);
        await _journal.AppendAsync("jan.kowalski@contoso.test", "Wysłane starym kluczem", Ct);
        var oldJournal = File.ReadAllText(_journal.FilePath);
        File.WriteAllBytes(_journal.KeyPath, [1, 2, 3, 4, 5]);    // e.g. protected for another user

        var reopened = new CorrespondenceJournal(_paths, _protector, _time);
        var load = await reopened.LoadAsync(Year, Ct);

        Assert.NotNull(load.ResetBackup);
        Assert.Equal(Path.Combine(reopened.Folder, $"journal-{_time.GetLocalNow():yyyy-MM-dd}.old"), load.ResetBackup);
        Assert.Equal(oldJournal, File.ReadAllText(load.ResetBackup));
        Assert.Equal(0, load.Entries);
        Assert.Equal([JournalFile.Header], File.ReadAllLines(reopened.FilePath));
        Assert.Null(reopened.Find("jan.kowalski@contoso.test"));
        await reopened.AppendAsync("jan.kowalski@contoso.test", "Nowy klucz", Ct);
        Assert.NotNull(reopened.Find("jan.kowalski@contoso.test"));

        // The next reset the same day gets a free name.
        File.WriteAllBytes(reopened.KeyPath, [9]);
        var again = await new CorrespondenceJournal(_paths, _protector, _time).LoadAsync(Year, Ct);
        Assert.Equal(Path.Combine(reopened.Folder, $"journal-{_time.GetLocalNow():yyyy-MM-dd}-2.old"), again.ResetBackup);
    }

    [Fact]
    public async Task KeyOfAWrongLength_IsNeverUsedToHash()
    {
        await _journal.LoadAsync(Year, Ct);
        await _journal.AppendAsync("jan.kowalski@contoso.test", "Temat", Ct);
        _protector.UnprotectOverride = new byte[16];

        var reopened = new CorrespondenceJournal(_paths, _protector, _time);
        var load = await reopened.LoadAsync(Year, Ct);

        Assert.NotNull(load.ResetBackup);
        _protector.UnprotectOverride = null;
        Assert.Equal(32, _protector.Unprotect(File.ReadAllBytes(reopened.KeyPath)).Length);
        Assert.Equal(0, reopened.Count);
    }

    [Fact]
    public async Task LostKey_WithEntries_KeepsTheOldJournalAside_ButAnEmptyJournalIsJustReused()
    {
        await _journal.LoadAsync(Year, Ct);
        File.Delete(_journal.KeyPath);
        var load = await new CorrespondenceJournal(_paths, _protector, _time).LoadAsync(Year, Ct);
        Assert.Null(load.ResetBackup);   // nothing to keep

        var withEntries = new CorrespondenceJournal(_paths, _protector, _time);
        await withEntries.LoadAsync(Year, Ct);
        await withEntries.AppendAsync("pawel.lewandowski@contoso.test", "Temat", Ct);
        File.Delete(withEntries.KeyPath);
        load = await new CorrespondenceJournal(_paths, _protector, _time).LoadAsync(Year, Ct);
        Assert.NotNull(load.ResetBackup);
        Assert.Single(JournalFile.Read(new StringReader(File.ReadAllText(load.ResetBackup))));
    }

    [Fact]
    public async Task AJournalOfAnotherFormat_IsKeptAside()
    {
        Directory.CreateDirectory(_journal.Folder);
        File.WriteAllText(_journal.FilePath, "AdMng.CorrespondenceJournal\t2\nnieznany format\n");
        var load = await _journal.LoadAsync(Year, Ct);

        Assert.NotNull(load.ResetBackup);
        Assert.StartsWith("AdMng.CorrespondenceJournal\t2", File.ReadAllText(load.ResetBackup), StringComparison.Ordinal);
        Assert.Equal([JournalFile.Header], File.ReadAllLines(_journal.FilePath));
    }

    [Fact]
    public async Task AHostWithoutAProtector_FailsToLoad_AndNeverMovesTheJournal()
    {
        await _journal.LoadAsync(Year, Ct);
        await _journal.AppendAsync("jan.kowalski@contoso.test", "Temat", Ct);
        _protector.Failure = new PlatformNotSupportedException();

        var other = new CorrespondenceJournal(_paths, _protector, _time);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => other.LoadAsync(Year, Ct));

        Assert.False(other.IsLoaded);
        Assert.Empty(Directory.GetFiles(_journal.Folder, "*.old"));
        Assert.Single(JournalFile.Read(new StringReader(File.ReadAllText(_journal.FilePath))));
    }

    [Fact]
    public async Task ConcurrentAppends_FromSeveralWindowsAndThreads_AreAllKept()
    {
        await _journal.LoadAsync(Year, Ct);
        var second = new CorrespondenceJournal(_paths, _protector, _time);
        await second.LoadAsync(Year, Ct);
        var addresses = Enumerable.Range(0, 120).Select(i => $"odbiorca{i}@gmina-łódź.example.pl").ToList();

        await Task.WhenAll(addresses.Select((address, i) => Task.Run(
            () => (i % 2 == 0 ? _journal : second).AppendAsync(address, $"Temat {i}", Ct), Ct)));

        Assert.Equal(60, _journal.Count);
        Assert.Equal(60, second.Count);
        var entries = JournalFile.Read(new StringReader(File.ReadAllText(_journal.FilePath)));
        Assert.Equal(120, entries.Count);
        Assert.Equal(121, File.ReadAllLines(_journal.FilePath).Length);
        var reread = new CorrespondenceJournal(_paths, _protector, _time);
        await reread.LoadAsync(Year, Ct);
        Assert.All(addresses, address => Assert.NotNull(reread.Find(address)));
    }

    [Fact]
    public async Task Append_WaitsWhileAnotherProcessHoldsTheLock()
    {
        await _journal.LoadAsync(Year, Ct);
        Assert.True(File.Exists(_journal.LockPath));
        var held = new FileStream(_journal.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var append = _journal.AppendAsync("żaneta.szymańska@contoso.test", "Temat", Ct);
        await Task.Delay(200, Ct);
        Assert.False(append.IsCompleted);
        Assert.Single(File.ReadAllLines(_journal.FilePath));     // the header only

        await held.DisposeAsync();
        await append;

        Assert.NotNull(_journal.Find("żaneta.szymańska@contoso.test"));
        Assert.Equal(2, File.ReadAllLines(_journal.FilePath).Length);
        Assert.Equal(0, new FileInfo(_journal.LockPath).Length);
    }

    [Fact]
    public async Task NoFileOfTheFolder_HoldsAnAddressOrAName()
    {
        await _journal.LoadAsync(Year, Ct);
        string[] addresses = ["łukasz.wiśniewski@contoso.test", "agnieszka.żółć@urząd-łódź.example.pl", "Jan.Kowalski@contoso.test"];
        foreach (var address in addresses)
        {
            await _journal.AppendAsync(address, "Zaproszenie", Ct);
        }

        _time.Advance(TimeSpan.FromDays(400));
        await _journal.AppendAsync("paweł.lewandowski@contoso.test", "Później", Ct);
        await _journal.PruneAsync(Year, Ct);

        var files = Directory.GetFiles(_journal.Folder);
        Assert.Equal(
            [CorrespondenceJournal.KeyFileName, CorrespondenceJournal.LockFileName, CorrespondenceJournal.FileName],
            files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        // The key file holds 32 random (protected) bytes: any byte value may occur there, '@' too.
        foreach (var file in files.Where(f => Path.GetFileName(f) != CorrespondenceJournal.KeyFileName))
        {
            var bytes = File.ReadAllBytes(file);
            var text = Encoding.UTF8.GetString(bytes);
            foreach (var part in new[] { "łukasz", "wiśniewski", "żółć", "kowalski", "paweł", "contoso", "example", "@" })
            {
                Assert.DoesNotContain(part, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(part, Encoding.Latin1.GetString(bytes), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task ParallelFindsAndAppends_DoNotInterfere()
    {
        await _journal.LoadAsync(Year, Ct);
        await _journal.AppendAsync("jan.kowalski@contoso.test", "Temat", Ct);

        var finds = Enumerable.Range(0, 200).Select(_ => Task.Run(() => { Assert.NotNull(_journal.Find("jan.kowalski@contoso.test")); }, Ct));
        var appends = Enumerable.Range(0, 30).Select(i => _journal.AppendAsync($"inny{i}@contoso.test", "Temat", Ct));
        await Task.WhenAll(finds.Concat(appends));

        Assert.Equal(31, _journal.Count);
    }
}
