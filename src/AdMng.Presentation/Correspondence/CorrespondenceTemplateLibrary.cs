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

    /// <summary>Id of the built-in "is the mailbox still needed?" template: one message per mailbox, named in the subject.</summary>
    /// <summary>The custom field of the mailbox templates that names the help desk (its link text; change it to send
    /// people to another system).</summary>
    public const string HelpdeskField = "System zgłoszeń";

    /// <summary>The custom field of the mailbox templates with the link to the help desk: <c>https://{{System zgłoszeń}}</c>.</summary>
    public const string HelpdeskLinkField = "Link do systemu zgłoszeń";

    /// <summary>The help desk of the operator's example message (the default of <see cref="HelpdeskField"/>).</summary>
    public const string DefaultHelpdesk = "hd.uni.lodz.pl";

    public const string MailboxStillNeededId = "mailbox-still-needed";

    /// <summary>Id of the built-in "are the mailboxes still needed?" template: one message per owner with the list of their
    /// mailboxes.</summary>
    public const string MailboxStillNeededGroupedId = "mailbox-still-needed-grouped";

    /// <summary>The days from today to the default deadline (<c>{{Termin}}</c>) of the built-in mailbox templates.</summary>
    public const int MailboxDeadlineDays = 14;

    /// <summary>The font of the built-in mailbox templates (the operator's message: Aptos 12 pt, single line height).</summary>
    public static MessageBaseStyle MailboxStyle { get; } = new("Aptos,sans-serif", 12, "normal");

    private static readonly Lazy<string> MailboxBody = new(() => BuiltInBody("mailbox-still-needed.html"));

    private static readonly Lazy<string> MailboxGroupedBody = new(() => BuiltInBody("mailbox-still-needed-grouped.html"));

    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;

    /// <param name="settings">The settings holding the saved templates and the last draft.</param>
    /// <param name="images">The image store (null: templates are saved without images).</param>
    /// <param name="time">The clock of the built-ins' deadline (today + <see cref="MailboxDeadlineDays"/>); null:
    /// <see cref="TimeProvider.System"/>.</param>
    public CorrespondenceTemplateLibrary(ISettingsStore settings, MessageImageStore? images = null, TimeProvider? time = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Images = images;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A template or the last draft was saved, deleted or imported.</summary>
    public event EventHandler? Changed;

    /// <summary>The image store (null: templates are saved without images).</summary>
    public MessageImageStore? Images { get; }

    /// <summary>The built-in templates: the mailbox query per mailbox, then grouped by owner (new instances on each call,
    /// the deadline counted from today; never stored).</summary>
    public IReadOnlyList<CorrespondenceTemplate> BuiltIns => [BuiltIn(MailboxStillNeededId), BuiltIn(MailboxStillNeededGroupedId)];

    /// <summary>The operator's templates (settings, in the order they were saved). Treat them as read-only: change a
    /// template through <see cref="SaveAsync"/>.</summary>
    public IReadOnlyList<CorrespondenceTemplate> Saved => _settings.Current.Correspondence.Templates;

    /// <summary>The built-ins, then the saved templates.</summary>
    public IReadOnlyList<CorrespondenceTemplate> All => [.. BuiltIns, .. Saved];

    public static bool IsBuiltIn(string? id) =>
        string.Equals(id, MailboxStillNeededId, StringComparison.Ordinal) || string.Equals(id, MailboxStillNeededGroupedId, StringComparison.Ordinal);

    /// <summary>The id of the built-in mailbox template of a grouping: one message per owner, else one per mailbox.</summary>
    public static string MailboxTemplateId(bool groupByOwner) => groupByOwner ? MailboxStillNeededGroupedId : MailboxStillNeededId;

    /// <summary>A copy of the template <paramref name="id"/> (a built-in or a saved one), or null.</summary>
    public CorrespondenceTemplate? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (IsBuiltIn(id))
        {
            return BuiltIn(id);
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

    /// <summary>The operator saved a version of the built-in <paramref name="id"/> ("Zapisz jako szablon wbudowany").</summary>
    public bool IsCustomized(string? id) =>
        IsBuiltIn(id) && _settings.Current.Correspondence.BuiltInOverrides.TryGetValue(id!, out var custom) && custom is not null;

    /// <summary>
    /// Saves <paramref name="template"/> as the operator's version of the built-in <paramref name="id"/> (with the images its
    /// body references): from now on the built-in – in the template list and on the mailbox owner page – is this one. The
    /// built-in keeps its id, name and grouping; its date fields named like the default's (the deadline) still count from
    /// the day it is loaded. Returns the built-in as it is now loaded.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is no built-in.</exception>
    public async Task<CorrespondenceTemplate> SaveBuiltInAsync(string id, CorrespondenceTemplate template, IEnumerable<MessageImage>? images = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!IsBuiltIn(id))
        {
            throw new ArgumentException("The id names no built-in template.", nameof(id));
        }

        var standard = Standard(id);
        var stored = WithImages(template, id, images, out var bytes);
        stored.Name = standard.Name;
        stored.GroupByAddress = standard.GroupByAddress;
        await StoreImagesAsync(bytes).ConfigureAwait(true);
        await _settings.UpdateAsync(s => s.Correspondence.BuiltInOverrides[id] = Copy(stored)).ConfigureAwait(true);
        CollectGarbage();
        OnChanged();
        return BuiltIn(id);
    }

    /// <summary>"Przywróć domyślny": the built-in <paramref name="id"/> is the default one again (nothing when it is not
    /// customized).</summary>
    public async Task RestoreBuiltInAsync(string id)
    {
        if (!IsCustomized(id))
        {
            return;
        }

        await _settings.UpdateAsync(s => s.Correspondence.BuiltInOverrides.Remove(id)).ConfigureAwait(true);
        CollectGarbage();
        OnChanged();
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
    /// <see cref="CorrespondenceSettings.MaxTemplates"/> are not added. Parsing the JSON, checking the images and sanitizing
    /// run on the thread pool (up to 20 MiB and 100 templates: never on the UI thread, rule 6); the images and the settings
    /// are stored on the caller's context.
    /// </summary>
    /// <exception cref="FormatException">Not a templates file (also unreadable JSON, or larger than <see cref="MaxFileLength"/>).</exception>
    public async Task<TemplateImportResult> ImportAsync(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var room = Math.Max(0, CorrespondenceSettings.MaxTemplates - Saved.Count);
        // Always give the caller's thread back, also when the parse finished before the await: the rest runs as a posted
        // continuation on the caller's context (the UI thread), never inline in the caller's frame.
        var (imported, accepted, lost) = await Task.Run(() => Prepare(json, room))
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
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

    /// <summary>
    /// The templates of an export file as they are imported (at most <paramref name="room"/>), the images accepted by content
    /// id and the number of images refused – the work of <see cref="ImportAsync"/> that touches neither the settings nor the
    /// image store.
    /// </summary>
    /// <exception cref="FormatException">Not a templates file.</exception>
    private static (List<CorrespondenceTemplate> Imported, Dictionary<string, MessageImage> Accepted, int Lost) Prepare(string json, int room)
    {
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

        var lost = 0;
        var imported = new List<CorrespondenceTemplate>();
        foreach (var template in file.Templates.Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Name)).Take(room))
        {
            imported.Add(Imported(template, accepted, ref lost));
        }

        return (imported, accepted, lost);
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
    /// the body references, custom fields, list item template, salutation and the base style (font, size, line height; none
    /// when the draft has none). The options a template may carry (voting, grouping) are left null (keep the window's) – set
    /// them on the result.
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
            FontFamily = draft.BaseStyle?.FontFamily,
            FontSize = draft.BaseStyle?.FontSizePoints,
            LineHeight = draft.BaseStyle?.LineHeight,
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
            FontFamily = template.FontFamily,
            FontSize = template.FontSize,
            LineHeight = template.LineHeight,
        };
    }

    /// <summary>The base style a template carries: null when it has none of its own (keep the window's), else its values
    /// with every missing or invalid part the default's.</summary>
    public static MessageBaseStyle? StyleOf(CorrespondenceTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.FontFamily is null && template.FontSize is null && template.LineHeight is null
            ? null
            : MessageBaseStyle.Of(template.FontFamily, template.FontSize, template.LineHeight);
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
        // The list item template is plain text (its text box; the renderer encodes it): sanitizing it as HTML would turn
        // a typed "<" into "&lt;", shown as such in every message.
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

    /// <summary>
    /// "Weryfikacja skrzynki pocztowej" (design note 6.1, "Template after the operator's example (1:1)"): one message per
    /// mailbox, the mailbox's name (<c>{{Skrzynka}}</c>) in the subject, the operator's example message as the body (an
    /// embedded resource, never changed), the deadline <c>{{Termin}}</c> today + <see cref="MailboxDeadlineDays"/> in
    /// <see cref="CustomFieldFormats.DefaultDate"/>, <c>{{podpis}}</c> where the signature goes, Aptos 12 pt with a single
    /// line height, no voting buttons.
    /// </summary>
    /// <summary>The built-in <paramref name="id"/>: the operator's version when there is one (its date fields named like a
    /// date field of the default get the default's value – the deadline counted from today), else the default.</summary>
    private CorrespondenceTemplate BuiltIn(string id)
    {
        var standard = Standard(id);
        if (!_settings.Current.Correspondence.BuiltInOverrides.TryGetValue(id, out var custom) || custom is null)
        {
            return standard;
        }

        var copy = Copy(custom, id);
        copy.Name = standard.Name;
        copy.GroupByAddress = standard.GroupByAddress;
        var defaults = (standard.CustomFields ?? []).Where(f => f.Kind == CustomFieldKind.Date).ToList();
        copy.CustomFields = [.. (copy.CustomFields ?? []).Select(f => f.Kind == CustomFieldKind.Date
            && defaults.FirstOrDefault(d => FieldKey.Same(d.Name, f.Name)) is { } deadline
                ? f with { Value = deadline.Value }
                : f)];
        return copy;
    }

    /// <summary>The default version of the built-in <paramref name="id"/>.</summary>
    private CorrespondenceTemplate Standard(string id) =>
        string.Equals(id, MailboxStillNeededGroupedId, StringComparison.Ordinal) ? MailboxStillNeededGrouped() : MailboxStillNeeded();

    private CorrespondenceTemplate MailboxStillNeeded() => MailboxTemplate(
        MailboxStillNeededId,
        "Weryfikacja skrzynki pocztowej (wiadomość na każdą skrzynkę)",
        "Weryfikacja skrzynki pocztowej: {{Skrzynka}}",
        MailboxBody.Value,
        groupByAddress: false);

    /// <summary>"Weryfikacja skrzynek pocztowych": the same message once per owner, its mailboxes listed by
    /// <c>{{lista}}</c> (a paragraph of its own, which the renderer replaces with the list).</summary>
    private CorrespondenceTemplate MailboxStillNeededGrouped() => MailboxTemplate(
        MailboxStillNeededGroupedId,
        "Weryfikacja skrzynek pocztowych (jedna wiadomość na osobę)",
        "Weryfikacja skrzynek pocztowych",
        MailboxGroupedBody.Value,
        groupByAddress: true);

    private CorrespondenceTemplate MailboxTemplate(string id, string name, string subject, string body, bool groupByAddress)
    {
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        return new CorrespondenceTemplate
        {
            Id = id,
            Name = name,
            Subject = subject,
            BodyHtml = body,
            CustomFields =
            [
                new CustomFieldDefinition("Termin", CustomFieldFormats.ValueOf(today.AddDays(MailboxDeadlineDays)), CustomFieldKind.Date,
                    CustomFieldFormats.DefaultDate),
                new CustomFieldDefinition(HelpdeskField, DefaultHelpdesk),
                new CustomFieldDefinition(HelpdeskLinkField, "https://{{" + HelpdeskField + "}}"),
            ],
            ListItemTemplate = "{{Skrzynka}} ({{Adres skrzynki}})",
            Salutation = null,
            VotingEnabled = false,
            GroupByAddress = groupByAddress,
            FontFamily = MailboxStyle.FontFamily,
            FontSize = MailboxStyle.FontSizePoints,
            LineHeight = MailboxStyle.LineHeight,
        };
    }

    /// <summary>The body of a built-in template (an embedded resource of this assembly): line breaks as LF, without the
    /// file's last line break.</summary>
    private static string BuiltInBody(string fileName)
    {
        var name = "AdMng.Presentation.Correspondence.Templates." + fileName;
        using var stream = typeof(CorrespondenceTemplateLibrary).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The built-in template {fileName} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
    }

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
