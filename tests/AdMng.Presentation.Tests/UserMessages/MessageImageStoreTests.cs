using AdMng.Core.Messaging.Images;
using AdMng.Core.Messaging;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// The image files of saved templates (U1c, design note section 11): content-addressed, atomic writes, the hash and the
/// image checked on every read, garbage collection of unreferenced files older than ten minutes, and files deleted by
/// another window in between.
/// </summary>
public sealed class MessageImageStoreTests : IDisposable
{
    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(16, 6, seed: 0x71, withMetadata: true), "logo-działu.png");
    private static readonly MessageImage Photo = MessageImage.Create(MessageImageSamples.Jpeg(24, 16, withMetadata: true), "zdjęcie.jpg");

    private readonly TempAppPaths _paths = new();
    private readonly TestDoubles.ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly MessageImageStore _store;

    public MessageImageStoreTests()
    {
        _store = new MessageImageStore(_paths, _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _paths.Dispose();

    private static MessageTemplate Template(params MessageImage[] images) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "Instrukcja – zażółć gęślą jaźń",
        Images = [.. images.Select(MessageTemplateImage.From)],
    };

    [Fact]
    public async Task Save_WritesContentAddressedFiles_AtomicallyAndOnce()
    {
        await _store.SaveAsync([Logo, Photo, Logo], Ct);

        var files = Directory.GetFiles(_store.Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { Logo.Sha256 + ".png", Photo.Sha256 + ".jpg" }.Order(StringComparer.Ordinal), files);
        Assert.Equal(Logo.Data, await File.ReadAllBytesAsync(_store.PathOf(Logo.Sha256, Logo.MediaType), Ct));
        Assert.Equal(Path.Combine(_paths.SettingsDirectory, "message-images"), _store.Folder);

        // Saved again: the file stays (no temporary leftovers), its time is refreshed.
        _time.Advance(TimeSpan.FromMinutes(5));
        await _store.SaveAsync([Logo], Ct);
        Assert.Equal(2, Directory.GetFiles(_store.Folder).Length);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, File.GetLastWriteTimeUtc(_store.PathOf(Logo.Sha256, Logo.MediaType)));
    }

    [Fact]
    public async Task Load_VerifiesTheHashAndTheImage()
    {
        await _store.SaveAsync([Logo, Photo], Ct);
        var template = Template(Logo, Photo);

        var loaded = await _store.LoadAsync(template, Ct);
        Assert.Equal(new[] { Logo.ContentId, Photo.ContentId }.Order(StringComparer.Ordinal), loaded.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Logo.Data, loaded[Logo.ContentId].Data);
        Assert.Equal(Logo.FileName, loaded[Logo.ContentId].FileName);

        // A changed file (same name, other bytes) and a missing one are not returned.
        await File.WriteAllBytesAsync(_store.PathOf(Logo.Sha256, Logo.MediaType), MessageImageSamples.Png(16, 6, seed: 0x72), Ct);
        File.Delete(_store.PathOf(Photo.Sha256, Photo.MediaType));
        Assert.Empty(await _store.LoadAsync(template, Ct));
        Assert.Null(await _store.LoadAsync(MessageTemplateImage.From(Logo) with { ContentId = "img-0000000000000000@admng" }, Ct));
    }

    [Fact]
    public async Task Save_RepairsAChangedFile()
    {
        await _store.SaveAsync([Logo], Ct);
        var path = _store.PathOf(Logo.Sha256, Logo.MediaType);
        await File.WriteAllBytesAsync(path, [1, 2, 3], Ct);

        await _store.SaveAsync([Logo], Ct);

        Assert.Equal(Logo.Data, await File.ReadAllBytesAsync(path, Ct));
        Assert.NotNull(await _store.LoadAsync(MessageTemplateImage.From(Logo), Ct));
    }

    [Fact]
    public async Task GarbageCollection_DeletesOnlyOldUnreferencedFiles()
    {
        await _store.SaveAsync([Logo, Photo], Ct);
        var orphan = Path.Combine(_store.Folder, ".abandoned.tmp");
        await File.WriteAllBytesAsync(orphan, [0], Ct);
        File.SetLastWriteTimeUtc(orphan, _time.GetUtcNow().UtcDateTime);
        var templates = new[] { Template(Logo) };

        // Younger than ten minutes: another window may be between writing its images and its settings.
        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, _store.CollectGarbage(templates));

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(2, _store.CollectGarbage(templates));
        Assert.True(File.Exists(_store.PathOf(Logo.Sha256, Logo.MediaType)));
        Assert.False(File.Exists(_store.PathOf(Photo.Sha256, Photo.MediaType)));
        Assert.False(File.Exists(orphan));
        Assert.Equal(Logo.Data.Length, _store.TotalBytes());
    }

    [Fact]
    public async Task FilesDeletedByAnotherWindow_AreSimplyMissing()
    {
        await _store.SaveAsync([Logo], Ct);
        var other = new MessageImageStore(_paths, _time);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, other.CollectGarbage([]));

        Assert.Null(await _store.LoadAsync(MessageTemplateImage.From(Logo), Ct));
        Assert.Equal(0, _store.CollectGarbage([]));
        using (var empty = new TempAppPaths())
        {
            Assert.Equal(0, new MessageImageStore(empty, _time).CollectGarbage([]));
        }

        // Concurrent saves of the same image end with one valid file.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _store.SaveAsync([Logo], Ct), Ct)));
        Assert.NotNull(await _store.LoadAsync(MessageTemplateImage.From(Logo), Ct));
        Assert.Single(Directory.GetFiles(_store.Folder));
    }
}
