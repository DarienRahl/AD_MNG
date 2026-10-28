using System.Text.Json;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// The message templates: the built-ins (names in the UI language, read-only) and the operator's templates stored in the
/// settings (text with fields only – like saved views, never directory data). Export / import as JSON for the team.
/// U1c: a template's images are metadata in the settings and bytes in the <see cref="MessageImageStore"/> – saving writes
/// the images first, then the settings, and collects garbage afterwards; the export (format version 2) carries each image
/// once, the import accepts versions 1 and 2 and checks every image again.
/// </summary>
public sealed class UserMessageTemplateLibrary
{
    /// <summary>Format marker of exported files.</summary>
    public const string ExportFormat = "AdMng.MessageTemplates";

    /// <summary>Version of exported files (2: with images, U1c).</summary>
    public const int ExportVersion = 2;

    /// <summary>Largest export / import file (characters of JSON).</summary>
    public const int MaxFileLength = 20 * 1024 * 1024;

    private readonly ISettingsStore _settings;
    private readonly ILocalizer _localizer;

    public UserMessageTemplateLibrary(ISettingsStore settings, ILocalizer localizer, MessageImageStore? images = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Images = images;
    }

    /// <summary>The image files of the templates (null: templates keep no images – tests without a settings folder).</summary>
    public MessageImageStore? Images { get; }

    /// <summary>Raised after a template was saved, deleted or imported.</summary>
    public event EventHandler? Changed;

    /// <summary>Built-ins first (in their fixed order), then the operator's templates by name.</summary>
    public IReadOnlyList<MessageTemplate> All =>
        [.. BuiltInMessageTemplates.All(UiLanguage), .. Custom.OrderBy(t => t.Name, StringComparer.Create(_localizer.Culture, true))];

    /// <summary>The operator's templates.</summary>
    public IReadOnlyList<MessageTemplate> Custom => _settings.Current.UserMessages.Templates;

    public static bool IsBuiltIn(string? id) => BuiltInMessageTemplates.IsBuiltIn(id);

    public MessageTemplate? Find(string? id) =>
        id is null ? null : BuiltInMessageTemplates.Get(id, UiLanguage) ?? Custom.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Saves an operator template: a built-in or an unknown id is stored as a new template (new id), an existing operator
    /// template is replaced. The HTML is normalized (cosmetic changes of the sanitizer) and images referenced by neither
    /// language are dropped; the bytes of <paramref name="images"/> the template references are written to the image store
    /// before the settings. Returns the stored template.
    /// </summary>
    public async Task<MessageTemplate> SaveAsync(MessageTemplate template, IEnumerable<MessageImage>? images = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Name);
        var normalized = MessageHtmlChecks.Normalize(template);
        var stored = IsBuiltIn(template.Id) || Custom.All(t => t.Id != template.Id)
            ? normalized with { Id = Guid.NewGuid().ToString("N"), Name = template.Name.Trim() }
            : normalized with { Name = template.Name.Trim() };
        if (Images is not null && stored.Images.Count > 0 && images is not null)
        {
            var referenced = stored.Images.Select(i => i.ContentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await Images.SaveAsync([.. images.Where(i => referenced.Contains(i.ContentId))]).ConfigureAwait(true);
        }

        await _settings.UpdateAsync(s =>
        {
            var templates = s.UserMessages.Templates;
            var index = templates.FindIndex(t => t.Id == stored.Id);
            if (index >= 0)
            {
                templates[index] = stored;
            }
            else
            {
                templates.Add(stored);
            }
        }).ConfigureAwait(true);
        CollectGarbage();
        Changed?.Invoke(this, EventArgs.Empty);
        return stored;
    }

    /// <summary>Deletes an operator template (built-ins cannot be deleted).</summary>
    public async Task DeleteAsync(string id)
    {
        if (IsBuiltIn(id))
        {
            return;
        }

        await _settings.UpdateAsync(s => s.UserMessages.Templates.RemoveAll(t => t.Id == id)).ConfigureAwait(true);
        CollectGarbage();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The bytes of a template's images from the store (those that could be read), by content id.</summary>
    public async Task<IReadOnlyDictionary<string, MessageImage>> LoadImagesAsync(MessageTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        return Images is null || (template.Images ?? []).Count == 0
            ? new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase)
            : await Images.LoadAsync(template, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The image folder holds more than <see cref="MessageImageStore.WarningBytes"/>.</summary>
    public bool ImageFolderIsLarge => Images?.TotalBytes() > MessageImageStore.WarningBytes;

    /// <summary>JSON of the operator's templates (or the given ones), format version 2: the images they reference once each.</summary>
    /// <exception cref="IOException">The file would be larger than <see cref="MaxFileLength"/>.</exception>
    public string ExportJson(IEnumerable<MessageTemplate>? templates = null)
    {
        var list = (templates ?? Custom).ToList();
        var images = new List<TemplateFileImage>();
        foreach (var metadata in list.SelectMany(t => t.Images ?? []).DistinctBy(i => i.Sha256, StringComparer.OrdinalIgnoreCase))
        {
            if (Images is not null && Images.LoadAsync(metadata).GetAwaiter().GetResult() is { } image)
            {
                images.Add(new TemplateFileImage { Sha256 = image.Sha256, MediaType = image.MediaType, FileName = image.FileName, Data = image.Data });
            }
        }

        var file = new TemplatesFile { Format = ExportFormat, Version = ExportVersion, Templates = list, Images = images };
        var json = JsonSerializer.Serialize(file, JsonSettingsStore.SerializerOptions);
        return json.Length <= MaxFileLength ? json : throw new IOException("The templates file would be larger than 20 MiB.");
    }

    /// <summary><see cref="ExportJson"/> off the UI thread (it reads the image files).</summary>
    public Task<string> ExportJsonAsync(IEnumerable<MessageTemplate>? templates = null)
    {
        var list = (templates ?? Custom).ToList();
        return Task.Run(() => ExportJson(list));
    }

    /// <summary>
    /// Imports templates from JSON: valid ones are added as copies with new ids (imports never overwrite). Returns the
    /// number added.
    /// </summary>
    /// <exception cref="FormatException">Not a templates file.</exception>
    /// <exception cref="JsonException">Unreadable JSON.</exception>
    public async Task<int> ImportAsync(string json) => (await ImportTemplatesAsync(json).ConfigureAwait(true)).Added;

    /// <summary>
    /// Imports templates from JSON (format versions 1 and 2): valid ones are added as copies with new ids. Every image goes
    /// through <see cref="MessageImage.Create(ReadOnlySpan{byte}, string?, string?, int, int)"/> (magic bytes, limits, metadata
    /// stripped) and must have its declared SHA-256; a template whose image fails keeps its text and loses that image.
    /// </summary>
    /// <exception cref="FormatException">Not a templates file, or larger than <see cref="MaxFileLength"/>.</exception>
    /// <exception cref="JsonException">Unreadable JSON.</exception>
    public async Task<TemplateImportResult> ImportTemplatesAsync(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxFileLength)
        {
            throw new FormatException("The templates file is larger than 20 MiB.");
        }

        var file = JsonSerializer.Deserialize<TemplatesFile>(json, JsonSettingsStore.SerializerOptions);
        if (file is null || !string.Equals(file.Format, ExportFormat, StringComparison.Ordinal) || file.Templates is null || file.Version is < 1 or > ExportVersion)
        {
            throw new FormatException("Not an AD Manager message templates file.");
        }

        var accepted = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in file.Images ?? [])
        {
            if (image?.Data is { Length: > 0 } data
                && MessageImage.TryCreate(data, image.FileName, image.MediaType, out var created, out _)
                && string.Equals(created!.Sha256, image.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                accepted[created.ContentId] = created;
            }
        }

        var valid = file.Templates
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => t with
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = t.Name.Trim(),
                Polish = t.Polish ?? new(),
                English = t.English ?? new(),
                Images = t.Images ?? [],
                Condition = Enum.IsDefined(t.Condition) ? t.Condition : MessageConditionKind.None,
                Recipients = Enum.IsDefined(t.Recipients) ? t.Recipients : MessageRecipientMode.User,
                ConditionDays = Math.Clamp(t.ConditionDays, 0, 3650),
                Salutation = t.Salutation?.Normalized(),
            })
            .ToList();
        if (valid.Count == 0)
        {
            return new TemplateImportResult(0, 0);
        }

        var lost = 0;
        // Stored as they are sent: sanitized like a saved template (a security finding stays visible, never removed).
        valid = [.. valid.Select(t => MessageHtmlChecks.Normalize(WithImportedImages(t, accepted, ref lost)))];

        if (Images is not null)
        {
            var used = valid.SelectMany(t => t.Images).Select(i => i.ContentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await Images.SaveAsync([.. accepted.Values.Where(i => used.Contains(i.ContentId))]).ConfigureAwait(true);
        }

        await _settings.UpdateAsync(s =>
        {
            s.UserMessages.Templates.AddRange(valid);
            s.UserMessages.Normalize();
        }).ConfigureAwait(true);
        CollectGarbage();
        Changed?.Invoke(this, EventArgs.Empty);
        return new TemplateImportResult(valid.Count, lost);
    }

    /// <summary>The template's images that were imported (and fit the template's limits); every other image loses its
    /// <c>&lt;img&gt;</c> elements in both languages.</summary>
    private MessageTemplate WithImportedImages(MessageTemplate template, Dictionary<string, MessageImage> accepted, ref int lost)
    {
        var kept = new List<MessageTemplateImage>();
        var polish = template.Polish;
        var english = template.English;
        long bytes = 0;
        foreach (var metadata in (template.Images ?? []).Where(i => i is not null).DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase))
        {
            if (Images is not null && accepted.TryGetValue(metadata.ContentId, out var image)
                && kept.Count < MessageImageLimits.MaxImagesPerTemplate && bytes + image.Data.Length <= MessageImageLimits.MaxImagesBytesPerTemplate)
            {
                kept.Add(MessageTemplateImage.From(image));
                bytes += image.Data.Length;
                continue;
            }

            lost++;
            polish = polish with { Html = polish.Html is null ? null : MessageHtmlChecks.RemoveImage(polish.Html, metadata.ContentId) };
            english = english with { Html = english.Html is null ? null : MessageHtmlChecks.RemoveImage(english.Html, metadata.ContentId) };
        }

        return template with { Polish = polish, English = english, Images = kept };
    }

    /// <summary>Deletes image files no template references any more (and older than ten minutes).</summary>
    private void CollectGarbage()
    {
        try
        {
            Images?.CollectGarbage(_settings.Current.UserMessages.Templates);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next save or delete tries again.
        }
    }

    private MessageLanguage UiLanguage => _localizer.Culture.TwoLetterISOLanguageName == "pl" ? MessageLanguage.Polish : MessageLanguage.English;

    private sealed class TemplatesFile
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public List<MessageTemplate>? Templates { get; set; }

        /// <summary>Version 2: the images of the templates, each once.</summary>
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

/// <summary>Result of a templates import: templates added and images that were refused (their templates lost them).</summary>
public sealed record TemplateImportResult(int Added, int ImagesRejected);
