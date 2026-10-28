using System.Text;
using System.Text.Json;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// The templates of Korespondencja (application singleton, design note 6.2): the built-ins (not stored) and the operator's
/// templates in the settings, with their images in the <see cref="MessageImageStore"/>; save, delete, export
/// (<c>{"format":"AdMng.CorrespondenceTemplates","version":1,...}</c> with the images in base64, at most
/// <see cref="MaxFileLength"/>) and import (new ids); the last draft (<see cref="CorrespondenceSettings.LastDraft"/>).
/// Templates are operator content (text with <c>{{fields}}</c> and images), never directory data (rule 1). Image bytes
/// are written to the store before the settings that reference them; garbage collection of the store keeps every image
/// a template of messages to users, a template of Korespondencja or the last draft references.
/// </summary>
public sealed class CorrespondenceTemplateLibrary
{
    public const string ExportFormat = "AdMng.CorrespondenceTemplates";

    public const int ExportVersion = 1;

    /// <summary>The largest export file (20 MiB of UTF-8).</summary>
    public const int MaxFileLength = 20 * 1024 * 1024;

    /// <summary>Id of the built-in "is the mailbox still needed?" template.</summary>
    public const string MailboxStillNeededId = "mailbox-still-needed";

    private readonly ISettingsStore _settings;

    public CorrespondenceTemplateLibrary(ISettingsStore settings, MessageImageStore? images = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Images = images;
    }

    /// <summary>A template or the last draft was saved, deleted or imported.</summary>
    public event EventHandler? Changed;

    /// <summary>The image store (null: templates are saved without images).</summary>
    public MessageImageStore? Images { get; }

    /// <summary>The built-in templates (new instances on each call; never stored).</summary>
    public static IReadOnlyList<CorrespondenceTemplate> BuiltIns => [MailboxStillNeeded()];

    /// <summary>The operator's templates (settings, in the order they were saved). Treat them as read-only: change a
    /// template through <see cref="SaveAsync"/>.</summary>
    public IReadOnlyList<CorrespondenceTemplate> Saved => _settings.Current.Correspondence.Templates;

    /// <summary>The built-ins, then the saved templates.</summary>
    public IReadOnlyList<CorrespondenceTemplate> All => [.. BuiltIns, .. Saved];

    public static bool IsBuiltIn(string? id) => string.Equals(id, MailboxStillNeededId, StringComparison.Ordinal);

    /// <summary>A copy of the template <paramref name="id"/> (a built-in or a saved one), or null.</summary>
    public CorrespondenceTemplate? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (IsBuiltIn(id))
        {
            return MailboxStillNeeded();
        }

        return Saved.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal)) is { } saved ? Copy(saved) : null;
    }

    /// <summary>
    /// Saves a template (a built-in or unknown id gets a new id, an existing one is replaced) and the bytes of its images:
    /// the template keeps the images its body or list item template references by <c>cid:</c> – from
    /// <paramref name="images"/>, else its own metadata. Returns a copy of the stored template.
    /// </summary>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    /// <exception cref="InvalidOperationException">A new template would exceed <see cref="CorrespondenceSettings.MaxTemplates"/>.</exception>
    public async Task<CorrespondenceTemplate> SaveAsync(CorrespondenceTemplate template, IEnumerable<MessageImage>? images = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Name);
        var isNew = IsBuiltIn(template.Id) || Saved.All(t => !string.Equals(t.Id, template.Id, StringComparison.Ordinal));
        if (isNew && Saved.Count >= CorrespondenceSettings.MaxTemplates)
        {
            throw new InvalidOperationException($"At most {CorrespondenceSettings.MaxTemplates} templates can be saved.");
        }

        var stored = WithImages(template, isNew ? NewId() : template.Id, images, out var bytes);
        stored.Name = template.Name.Trim();
        await StoreImagesAsync(bytes).ConfigureAwait(true);
        await _settings.UpdateAsync(s =>
        {
            var templates = s.Correspondence.Templates;
            var index = templates.FindIndex(t => string.Equals(t.Id, stored.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                templates[index] = Copy(stored);
            }
            else
            {
                templates.Add(Copy(stored));
            }
        }).ConfigureAwait(true);
        CollectGarbage();
        OnChanged();
        return stored;
    }

    /// <summary>Deletes a saved template (built-ins cannot be deleted; an unknown id does nothing).</summary>
    public async Task DeleteAsync(string id)
    {
        if (IsBuiltIn(id) || Saved.All(t => !string.Equals(t.Id, id, StringComparison.Ordinal)))
        {
            return;
        }

        await _settings.UpdateAsync(s => s.Correspondence.Templates.RemoveAll(t => string.Equals(t.Id, id, StringComparison.Ordinal))).ConfigureAwait(true);
        CollectGarbage();
        OnChanged();
    }

    /// <summary>The images of a template by content id (missing or changed files are left out).</summary>
    public async Task<IReadOnlyDictionary<string, MessageImage>> LoadImagesAsync(CorrespondenceTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        var result = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
        if (Images is null)
        {
            return result;
        }

        foreach (var metadata in template.Images ?? [])
        {
            if (metadata is not null && !result.ContainsKey(metadata.ContentId)
                && await Images.LoadAsync(metadata, cancellationToken, CorrespondenceLimits.MaxImageBytes).ConfigureAwait(false) is { } image)
            {
                result[image.ContentId] = image;
            }
        }

        return result;
    }

    /// <summary>The export file of <paramref name="templates"/> (null: every saved template), read off the UI thread.</summary>
    /// <exception cref="IOException">The file would exceed <see cref="MaxFileLength"/>.</exception>
    public Task<string> ExportJsonAsync(IEnumerable<CorrespondenceTemplate>? templates = null)
    {
        var list = (templates ?? Saved).Where(t => t is not null).Select(t => Copy(t)).ToList();
        return Task.Run(async () =>
        {
            var images = new List<TemplateFileImage>();
            foreach (var metadata in list.SelectMany(t => t.Images ?? []).Where(i => i is not null).DistinctBy(i => i.Sha256, StringComparer.OrdinalIgnoreCase))
            {
                if (Images is not null && await Images.LoadAsync(metadata, CancellationToken.None, CorrespondenceLimits.MaxImageBytes).ConfigureAwait(false) is { } image)
                {
                    images.Add(new TemplateFileImage { Sha256 = image.Sha256, MediaType = image.MediaType, FileName = image.FileName, Data = image.Data });
                }
            }

            var file = new TemplatesFile { Format = ExportFormat, Version = ExportVersion, Templates = list, Images = images };
            var json = JsonSerializer.Serialize(file, JsonSettingsStore.SerializerOptions);
            return Encoding.UTF8.GetByteCount(json) <= MaxFileLength
                ? json
                : throw new IOException("The templates file would be larger than 20 MiB.");
        });
    }

    /// <summary>
    /// Imports an export file with new ids (never overwrites). Every image goes through
    /// <see cref="MessageImage.Create(ReadOnlySpan{byte}, string?, string?, int, int)"/> with the Korespondencja limits and must
    /// have its declared SHA-256; the body and the list item template are sanitized again (fragment, <c>{{field}}</c> links
    /// allowed). A template keeps its text when an image is refused (the image is removed from it). Templates beyond
    /// <see cref="CorrespondenceSettings.MaxTemplates"/> are not added.
    /// </summary>
    /// <exception cref="FormatException">Not a templates file (also unreadable JSON, or larger than <see cref="MaxFileLength"/>).</exception>
    public async Task<TemplateImportResult> ImportAsync(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxFileLength)
        {
            throw new FormatException("The templates file is larger than 20 MiB.");
        }

        TemplatesFile? file;
        try
        {
            file = JsonSerializer.Deserialize<TemplatesFile>(json, JsonSettingsStore.SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Not an AD Manager correspondence templates file.", ex);
        }

        if (file is null || !string.Equals(file.Format, ExportFormat, StringComparison.Ordinal) || file.Templates is null || file.Version is < 1 or > ExportVersion)
        {
            throw new FormatException("Not an AD Manager correspondence templates file.");
        }

        var accepted = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in file.Images ?? [])
        {
            if (image?.Data is { Length: > 0 } data
                && MessageImage.TryCreate(data, image.FileName, image.MediaType, out var created, out _, CorrespondenceLimits.MaxImageBytes)
                && string.Equals(created!.Sha256, image.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                accepted[created.ContentId] = created;
            }
        }

        var room = Math.Max(0, CorrespondenceSettings.MaxTemplates - Saved.Count);
        var lost = 0;
        var imported = new List<CorrespondenceTemplate>();
        foreach (var template in file.Templates.Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Name)).Take(room))
        {
            imported.Add(Imported(template, accepted, ref lost));
        }

        if (imported.Count == 0)
        {
            return new TemplateImportResult(0, lost);
        }

        var used = imported.SelectMany(t => t.Images).Select(i => i.ContentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await StoreImagesAsync([.. accepted.Values.Where(i => used.Contains(i.ContentId))]).ConfigureAwait(true);
        await _settings.UpdateAsync(s => s.Correspondence.Templates.AddRange(imported.Select(t => Copy(t)))).ConfigureAwait(true);
        CollectGarbage();
        OnChanged();
        return new TemplateImportResult(imported.Count, lost);
    }

    /// <summary>A copy of the last draft of the settings, or null.</summary>
    public CorrespondenceTemplate? LoadLastDraft() => _settings.Current.Correspondence.LastDraft is { } draft ? Copy(draft) : null;

    /// <summary>
    /// Saves the last draft (on close and before each send) and the bytes of its images (the images its body references:
    /// from <paramref name="images"/>, else its own metadata).
    /// </summary>
    public async Task SaveLastDraftAsync(CorrespondenceTemplate draft, IEnumerable<MessageImage>? images = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var stored = WithImages(draft, draft.Id, images, out var bytes);
        await StoreImagesAsync(bytes).ConfigureAwait(true);
        await _settings.UpdateAsync(s => s.Correspondence.LastDraft = Copy(stored)).ConfigureAwait(true);
        CollectGarbage();
        OnChanged();
    }

    /// <summary>
    /// The template of a draft as edited (the last draft, or a template to save): subject, body, the metadata of the images
    /// the body references, custom fields, list item template and salutation. The options a template may carry (voting,
    /// grouping) are left null (keep the window's) – set them on the result.
    /// </summary>
    public static CorrespondenceTemplate FromDraft(CorrespondenceDraft draft, string name = "")
    {
        ArgumentNullException.ThrowIfNull(draft);
        var template = new CorrespondenceTemplate
        {
            Name = name ?? string.Empty,
            Subject = draft.Subject ?? string.Empty,
            BodyHtml = draft.BodyHtml ?? string.Empty,
            CustomFields = [.. (draft.CustomFields ?? []).Where(f => f is not null)],
            ListItemTemplate = draft.ListItemTemplate ?? string.Empty,
            Salutation = string.IsNullOrWhiteSpace(draft.Salutation) ? null : draft.Salutation.Trim(),
        };
        var images = draft.Images ?? new Dictionary<string, MessageImage>();
        template.Images = [.. ReferencedIds(template)
            .Select(id => images.TryGetValue(id, out var image) ? image : images.Values.FirstOrDefault(i => string.Equals(i.ContentId, id, StringComparison.OrdinalIgnoreCase)))
            .OfType<MessageImage>()
            .Select(MessageTemplateImage.From)
            .Take(CorrespondenceLimits.MaxImagesPerMessage)];
        return template;
    }

    /// <summary>A deep copy (images and custom fields are immutable records).</summary>
    public static CorrespondenceTemplate Copy(CorrespondenceTemplate template, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new CorrespondenceTemplate
        {
            Id = id ?? template.Id,
            Name = template.Name ?? string.Empty,
            Subject = template.Subject ?? string.Empty,
            BodyHtml = template.BodyHtml ?? string.Empty,
            Images = [.. (template.Images ?? []).Where(i => i is not null)],
            CustomFields = [.. (template.CustomFields ?? []).Where(f => f is not null)],
            ListItemTemplate = template.ListItemTemplate ?? string.Empty,
            Salutation = template.Salutation,
            VotingEnabled = template.VotingEnabled,
            VotingOptions = template.VotingOptions,
            GroupByAddress = template.GroupByAddress,
        };
    }

    public override string ToString() => nameof(CorrespondenceTemplateLibrary);

    /// <summary>The content ids the body and the list item template reference by <c>cid:</c>, in order.</summary>
    private static IReadOnlyList<string> ReferencedIds(CorrespondenceTemplate template) =>
    [
        .. MessageHtmlChecks.ReferencedImageIds(template.BodyHtml)
            .Concat(MessageHtmlChecks.ReferencedImageIds(template.ListItemTemplate))
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>A copy with id <paramref name="id"/> whose images are those it references (bytes from
    /// <paramref name="images"/>, which are returned in <paramref name="bytes"/>; else the template's own metadata).</summary>
    private static CorrespondenceTemplate WithImages(CorrespondenceTemplate template, string id, IEnumerable<MessageImage>? images, out List<MessageImage> bytes)
    {
        var provided = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images ?? [])
        {
            if (image is not null)
            {
                provided.TryAdd(image.ContentId, image);
            }
        }

        var copy = Copy(template, id);
        var metadata = new List<MessageTemplateImage>();
        bytes = [];
        foreach (var contentId in ReferencedIds(copy))
        {
            if (metadata.Count >= CorrespondenceLimits.MaxImagesPerMessage)
            {
                break;
            }

            if (provided.TryGetValue(contentId, out var image))
            {
                metadata.Add(MessageTemplateImage.From(image));
                bytes.Add(image);
            }
            else if (copy.Images.FirstOrDefault(i => string.Equals(i.ContentId, contentId, StringComparison.OrdinalIgnoreCase)) is { } known)
            {
                metadata.Add(known);
            }
        }

        copy.Images = metadata;
        return copy;
    }

    /// <summary>An imported template: a new id, the images that were accepted (within the per-message limits), the HTML
    /// sanitized again; every other image loses its <c>&lt;img&gt;</c>.</summary>
    private static CorrespondenceTemplate Imported(CorrespondenceTemplate template, Dictionary<string, MessageImage> accepted, ref int lost)
    {
        var copy = Copy(template, NewId());
        copy.Name = copy.Name.Trim();
        var kept = new List<MessageTemplateImage>();
        long total = 0;
        foreach (var metadata in copy.Images.DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase))
        {
            if (accepted.TryGetValue(metadata.ContentId, out var image)
                && kept.Count < CorrespondenceLimits.MaxImagesPerMessage
                && total + image.Data.Length <= CorrespondenceLimits.MaxImagesBytesPerMessage)
            {
                kept.Add(MessageTemplateImage.From(image));
                total += image.Data.Length;
                continue;
            }

            lost++;
            copy.BodyHtml = string.IsNullOrEmpty(copy.BodyHtml) ? copy.BodyHtml : MessageHtmlChecks.RemoveImage(copy.BodyHtml, metadata.ContentId);
        }

        var ids = kept.Select(i => i.ContentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        copy.Images = kept;
        copy.BodyHtml = Sanitize(copy.BodyHtml, ids);
        copy.ListItemTemplate = Sanitize(copy.ListItemTemplate, ids);
        return copy;
    }

    private static string Sanitize(string html, IReadOnlySet<string> imageIds) =>
        string.IsNullOrWhiteSpace(html)
            ? string.Empty
            : MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions
            {
                Mode = HtmlSanitizeMode.Fragment,
                InlineImageIds = imageIds,
                AllowFieldUrls = true,
            }).Html;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task StoreImagesAsync(IReadOnlyCollection<MessageImage> images)
    {
        if (Images is not null && images.Count > 0)
        {
            await Images.SaveAsync(images).ConfigureAwait(true);
        }
    }

    /// <summary>Deletes image files nothing references any more (older than ten minutes).</summary>
    private void CollectGarbage()
    {
        try
        {
            Images?.CollectGarbage(_settings.Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next save or delete tries again.
        }
    }

    /// <summary>Raises <see cref="Changed"/>.</summary>
    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>"Czy skrzynka pocztowa jest nadal potrzebna?" (design note 6.1); <c>{{lista}}</c> has a paragraph of its own,
    /// which the renderer replaces with the list.</summary>
    private static CorrespondenceTemplate MailboxStillNeeded() => new()
    {
        Id = MailboxStillNeededId,
        Name = "Czy skrzynka pocztowa jest nadal potrzebna?",
        Subject = "Czy skrzynka pocztowa jest nadal potrzebna?",
        BodyHtml =
            "<p>{{Zwrot osobowy}},</p>"
            + "<p>w ewidencji poczty jako osobę, do której przypisano skrzynkę, wskazano {{Pana/Panią}}:</p>"
            + "<p>{{lista}}</p>"
            + "<p>Prosimy o informację, czy skrzynka jest nadal potrzebna. W Outlooku wystarczy kliknąć przycisk nad treścią tej "
            + "wiadomości („Tak, nadal potrzebna” albo „Nie, można usunąć”); można też po prostu odpowiedzieć na tę wiadomość. "
            + "Jeżeli do {{Termin}} nie otrzymamy odpowiedzi, skrzynka zostanie zablokowana.</p>"
            + "<p>Z poważaniem</p>",
        CustomFields = [new CustomFieldDefinition("Termin", "")],
        ListItemTemplate = "{{Skrzynka}} ({{Adres skrzynki}}) – ostatnie logowanie: {{Ostatnie logowanie|brak danych}}",
        VotingEnabled = true,
        VotingOptions = CorrespondenceSettings.DefaultVotingOptions,
        GroupByAddress = true,
    };

    /// <summary>The export file.</summary>
    private sealed class TemplatesFile
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public List<CorrespondenceTemplate>? Templates { get; set; }

        /// <summary>The images of the templates, each once.</summary>
        public List<TemplateFileImage>? Images { get; set; }
    }

    private sealed class TemplateFileImage
    {
        public string Sha256 { get; set; } = string.Empty;

        public string MediaType { get; set; } = string.Empty;

        public string? FileName { get; set; }

        public byte[]? Data { get; set; }
    }
}
