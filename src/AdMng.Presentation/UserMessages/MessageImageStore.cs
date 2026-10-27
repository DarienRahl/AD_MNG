using System.Security.Cryptography;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// The bytes of the images of saved message templates (U1c, design note u1c-u1d section 11): a content-addressed folder
/// next to the settings, <c>&lt;SettingsDirectory&gt;/message-images/{sha256}.{png|jpg|gif}</c> – operator content (logos,
/// instruction screenshots), never directory data. The template metadata stays in <c>settings.json</c>. Writes are atomic
/// (temp file + move) and skipped when the file exists with the right hash; reads verify the SHA-256 and the image again
/// (a missing or changed file is simply not returned – the planner then reports <c>ImageMissing</c>). Garbage collection
/// deletes files no template references once they are older than <see cref="GarbageAge"/> (another window may be between
/// writing its images and its settings). Application-wide (singleton), thread-safe.
/// </summary>
public sealed class MessageImageStore
{
    /// <summary>The folder name below the settings directory.</summary>
    public const string FolderName = "message-images";

    /// <summary>Unreferenced files younger than this survive a garbage collection.</summary>
    public static readonly TimeSpan GarbageAge = TimeSpan.FromMinutes(10);

    /// <summary>Above this the library warns about the folder size.</summary>
    public const long WarningBytes = 64L * 1024 * 1024;

    private readonly TimeProvider _time;

    public MessageImageStore(IAppPaths paths, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        Folder = Path.Combine(paths.SettingsDirectory, FolderName);
    }

    /// <summary>The folder of the image files.</summary>
    public string Folder { get; }

    /// <summary>The file of an image: <c>{sha256}.{ext}</c>.</summary>
    public string PathOf(string sha256, string mediaType) =>
        Path.Combine(Folder, sha256.ToLowerInvariant() + "." + ImageFormats.Extension(ImageFormats.FromMediaType(mediaType) ?? ImageKind.Png));

    /// <summary>Writes the images that are not stored yet (atomically; an existing file with the right hash is kept).</summary>
    public async Task SaveAsync(IEnumerable<MessageImage> images, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        var list = images.Where(i => i is not null).DistinctBy(i => i.Sha256, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(Folder);
        foreach (var image in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PathOf(image.Sha256, image.MediaType);
            if (await HashOfAsync(path, cancellationToken).ConfigureAwait(false) is { } existing
                && string.Equals(existing, image.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
                continue;
            }

            var temporary = Path.Combine(Folder, $".{image.Sha256}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, image.Data, cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(temporary, _time.GetUtcNow().UtcDateTime);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary>The image of <paramref name="metadata"/>, or null when its file is missing, changed or not a valid image.</summary>
    public async Task<MessageImage?> LoadAsync(MessageTemplateImage metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!metadata.IsValid)
        {
            return null;
        }

        var path = PathOf(metadata.Sha256, metadata.MediaType);
        byte[] data;
        try
        {
            if (new FileInfo(path) is not { Exists: true } info || info.Length > MessageImageLimits.MaxImageBytes)
            {
                return null;
            }

            data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), metadata.Sha256, StringComparison.OrdinalIgnoreCase)
            || !MessageImage.TryCreate(data, metadata.FileName, metadata.MediaType, out var image, out _)
            || !string.Equals(image!.ContentId, metadata.ContentId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return image;
    }

    /// <summary>The images of a template that could be loaded, by content id (missing ones are absent).</summary>
    public async Task<IReadOnlyDictionary<string, MessageImage>> LoadAsync(MessageTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        var result = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var metadata in template.Images ?? [])
        {
            if (!result.ContainsKey(metadata.ContentId) && await LoadAsync(metadata, cancellationToken).ConfigureAwait(false) is { } image)
            {
                result[image.ContentId] = image;
            }
        }

        return result;
    }

    /// <summary>
    /// Deletes the image files no template of <paramref name="templates"/> references and that are older than
    /// <see cref="GarbageAge"/> (plus abandoned temporary files). Returns the number of deleted files.
    /// </summary>
    public int CollectGarbage(IEnumerable<MessageTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (!Directory.Exists(Folder))
        {
            return 0;
        }

        var referenced = templates.SelectMany(t => t.Images ?? []).Select(i => i.Sha256.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var cutoff = _time.GetUtcNow().UtcDateTime - GarbageAge;
        var deleted = 0;
        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles())
        {
            var sha = file.Name.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;
            if (file.LastWriteTimeUtc > cutoff || (!file.Name.StartsWith('.') && referenced.Contains(sha)))
            {
                continue;
            }

            if (TryDelete(file.FullName))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>Bytes of every image file (the library warns above <see cref="WarningBytes"/>).</summary>
    public long TotalBytes()
    {
        try
        {
            return Directory.Exists(Folder) ? new DirectoryInfo(Folder).EnumerateFiles().Sum(f => f.Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static async Task<string?> HashOfAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use or already gone (another window); the next collection tries again.
        }

        return false;
    }

    public override string ToString() => nameof(MessageImageStore);
}
