using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// "2 · Wiadomość" (design note 6.4 and 7.2): subject and body with <c>{{fields}}</c> (the WYSIWYG editor through
/// <see cref="IRichEditorHost"/>), the signature, attachments, sending options (mode, BCC chunk, delay, read receipt,
/// deferred delivery), voting, custom fields, grouping and templates. Produces the <see cref="CorrespondenceDraft"/>.
/// </summary>
/// <remarks>
/// <para>The body, its images, the custom fields and the options are operator content: they are kept in the settings as the
/// last draft (by the window) and in templates; attachments are paths chosen for this window and never persisted. Images
/// get the Korespondencja limits (<see cref="CorrespondenceLimits"/>). Signatures are listed and parsed off the UI thread.</para>
/// <para>"Wstaw pole" offers the built-in fields (<c>{{lista}}</c> and <c>{{liczba}}</c> only when grouping), the custom
/// fields and the columns of the recipient list that do not duplicate a built-in field.</para>
/// </remarks>
public sealed partial class MessagePageViewModel : ObservableObject, IRichEditorHost
{
    /// <summary>The file name offered by "Eksportuj szablony".</summary>
    public const string TemplatesFileName = "szablony-korespondencji.json";

    /// <summary>The characters a custom field name cannot contain (they delimit fields).</summary>
    private const string FieldDelimiters = "{ } |";

    /// <summary>MailerWPiA's time formats ("%H": a single custom specifier, not the standard format "H").</summary>
    private static readonly string[] DeliverTimeFormats = ["H:mm", "HH:mm", "%H", "HH"];

    private readonly SerialCancellation _signatureLoads = new();
    private readonly SerialCancellation _pastes = new();
    private readonly HashSet<CustomFieldViewModel> _watchedFields = [];
    private IList? _selectedAttachments;
    private CorrespondenceSignature? _signature;
    private string? _signatureName;
    private string? _savedSignatureName;
    private bool _refreshingSignatures;
    private bool _changingTemplate;

    /// <param name="services">The window's services.</param>
    /// <param name="correspondence">The Korespondencja services.</param>
    /// <param name="recipients">The recipients page (its columns are fields).</param>
    public MessagePageViewModel(DialogServices services, CorrespondenceServices correspondence, RecipientsPageViewModel recipients)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        Recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        SendModeChoices = [.. Enum.GetValues<CorrespondenceSendMode>().Select(v => new Option<CorrespondenceSendMode>(v, Localizer[CorrespondenceText.SendModeKey(v)]))];
        SendMode = SendModeChoices[0];
        DeliverDate = services.Time.GetLocalNow().Date.AddDays(1);
        Signatures = [NoSignatureOption()];
        _refreshingSignatures = true;
        SelectedSignature = Signatures[0];
        _refreshingSignatures = false;

        // The recipients page and the collections live as long as this page: no unsubscription needed.
        Recipients.Columns.CollectionChanged += (_, _) => OnFieldsChanged();
        CustomFields.CollectionChanged += OnCustomFieldsChanged;
        Attachments.CollectionChanged += (_, _) => RefreshAttachmentsHeader();
        TemplatesListener.Attach(this, correspondence.Templates);
        RefreshTemplates();
        RefreshFieldChoices();
        RefreshAttachmentsHeader();
    }

    /// <summary>The view inserts <c>{{field}}</c> at the caret of the editor or the subject box.</summary>
    public event EventHandler<string>? InsertFieldRequested;

    [ObservableProperty]
    public partial string Subject { get; set; } = string.Empty;

    /// <summary>"Zwrot grzecznościowy" (<c>{{zwrot}}</c>).</summary>
    [ObservableProperty]
    public partial string Salutation { get; set; } = BuiltInFields.DefaultSalutation;

    /// <summary>The body as edited (sanitized subset, <c>cid:</c> images; two-way with the editor).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBodyEmpty))]
    public partial string BodyHtml { get; set; } = string.Empty;

    /// <summary>The body images by content id (the editor registers images through the host).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Images))]
    public partial IReadOnlyDictionary<string, MessageImage> BodyImages { get; private set; } = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);

    public bool IsBodyEmpty => string.IsNullOrWhiteSpace(BodyHtml);

    /// <summary>The <c>{{field}}</c> texts of "Wstaw pole": built-ins (list/count only when grouping), custom fields, columns.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> FieldChoices { get; private set; } = [];

    /// <summary>"(bez podpisu)" first, then the Outlook signatures.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SignatureOption> Signatures { get; private set; } = [];

    [ObservableProperty]
    public partial SignatureOption? SelectedSignature { get; set; }

    [ObservableProperty]
    public partial string SignatureProblemsText { get; private set; } = string.Empty;

    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    /// <summary>The attachment list's multi-selection (set by the view).</summary>
    public IList? SelectedAttachments
    {
        get => _selectedAttachments;
        set => SetProperty(ref _selectedAttachments, value);
    }

    /// <summary>"Załączniki (2, 1,4 MB)".</summary>
    [ObservableProperty]
    public partial string AttachmentsHeaderText { get; private set; } = string.Empty;

    /// <summary>There are attachments (the header turns red: they go to every message).</summary>
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>The size of every attachment together.</summary>
    public long AttachmentsBytes => Attachments.Sum(a => a.Length);

    public IReadOnlyList<Option<CorrespondenceSendMode>> SendModeChoices { get; }

    [ObservableProperty]
    public partial Option<CorrespondenceSendMode> SendMode { get; set; }

    /// <summary>The mode chosen (<see cref="SendMode"/>'s value).</summary>
    public bool IsBcc => SendMode.Value == CorrespondenceSendMode.Bcc;

    /// <summary>Recipients of one BCC message (1–<see cref="CorrespondenceLimits.MaxBccPerMessage"/>).</summary>
    [ObservableProperty]
    public partial int BccChunkSize { get; set; } = CorrespondenceLimits.MaxBccPerMessage;

    /// <summary>Seconds between two messages (0–<see cref="CorrespondenceSettings.MaxDelaySeconds"/>).</summary>
    [ObservableProperty]
    public partial double DelaySeconds { get; set; } = 3;

    [ObservableProperty]
    public partial bool ReadReceipt { get; set; }

    [ObservableProperty]
    public partial bool DeliverLater { get; set; }

    /// <summary>The day of deferred delivery (default tomorrow).</summary>
    [ObservableProperty]
    public partial DateTime? DeliverDate { get; set; }

    /// <summary>The time of deferred delivery ("H:mm", "HH:mm" or "H").</summary>
    [ObservableProperty]
    public partial string DeliverTime { get; set; } = CorrespondenceSettings.DefaultDeferTime;

    [ObservableProperty]
    public partial string? DeliverError { get; private set; }

    [ObservableProperty]
    public partial bool UseVoting { get; set; }

    /// <summary>Voting buttons, semicolon separated.</summary>
    [ObservableProperty]
    public partial string VotingOptions { get; set; } = CorrespondenceSettings.DefaultVotingOptions;

    public ObservableCollection<CustomFieldViewModel> CustomFields { get; } = [];

    /// <summary>One message per address with all its rows (<c>{{lista}}</c>, <c>{{liczba}}</c>).</summary>
    [ObservableProperty]
    public partial bool GroupByAddress { get; set; }

    /// <summary>The HTML of one <c>{{lista}}</c> item.</summary>
    [ObservableProperty]
    public partial string ListItemTemplate { get; set; } = string.Empty;

    /// <summary>Built-ins, then the saved templates.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TemplateOption> Templates { get; private set; } = [];

    /// <summary>Loading asks first when the body is not empty and differs.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTemplateCommand))]
    public partial TemplateOption? SelectedTemplate { get; set; }

    [ObservableProperty]
    public partial string TemplateName { get; set; } = string.Empty;

    /// <summary>The problems of the last formatted paste.</summary>
    [ObservableProperty]
    public partial string PasteProblemsText { get; private set; } = string.Empty;

    public IReadOnlyDictionary<string, MessageImage> Images => BodyImages;

    /// <summary>The voting buttons as sent ("Tak;Nie": trimmed, empty ones left out), or null when there are none.</summary>
    public string? VotingOptionsText => NormalizeVotingOptions(VotingOptions);

    /// <summary>The template being loaded (tests await it), or null.</summary>
    internal Task? TemplateLoading { get; private set; }

    /// <summary>The signature being read (tests and <see cref="DraftAsync"/> await it), or null.</summary>
    internal Task? SignatureLoading { get; private set; }

    /// <summary>The signature of the draft (read when it was chosen), or null.</summary>
    internal CorrespondenceSignature? LoadedSignature => _signatureName is not null && _signatureName == SelectedSignature?.Name ? _signature : null;

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private RecipientsPageViewModel Recipients { get; }

    private ILocalizer Localizer => Services.Localizer;

    private int AnsiCodePage => Correspondence.Platform?.AnsiCodePage ?? Services.Regional.AnsiCodePage;

    /// <summary>The draft as edited (with the loaded signature).</summary>
    public CorrespondenceDraft Draft() => new()
    {
        Subject = Subject,
        BodyHtml = BodyHtml,
        Images = BodyImages,
        Signature = LoadedSignature,
        CustomFields = [.. CustomFields.Where(f => !string.IsNullOrWhiteSpace(f.Name)).Select(f => new CustomFieldDefinition(f.Name.Trim(), f.Value ?? string.Empty))],
        ListItemTemplate = ListItemTemplate,
        Salutation = Salutation,
    };

    /// <summary>The draft once the chosen signature has been read (sending and the preview use it).</summary>
    public async Task<CorrespondenceDraft> DraftAsync()
    {
        if (SignatureLoading is { } loading)
        {
            await loading.ConfigureAwait(true);
        }

        return Draft();
    }

    /// <summary>The draft as a template (the last draft, a template to save): with the voting and grouping options.</summary>
    public CorrespondenceTemplate ToTemplate(string name = "")
    {
        var template = CorrespondenceTemplateLibrary.FromDraft(Draft(), name);
        template.VotingEnabled = UseVoting;
        template.VotingOptions = VotingOptionsText;
        template.GroupByAddress = GroupByAddress;
        return template;
    }

    /// <summary>
    /// The localized errors that block sending: empty subject or body, missing attachments, deferred delivery, custom field
    /// errors, images over the limits.
    /// </summary>
    public IReadOnlyList<string> ValidateForSend()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Subject))
        {
            errors.Add(Localizer[L.CorrespondenceMessage.SubjectEmpty]);
        }
        else if (Subject.Trim().Length > CorrespondenceLimits.MaxSubjectLength)
        {
            errors.Add(Localizer.Format(L.CorrespondenceMessage.SubjectTooLong, CorrespondenceLimits.MaxSubjectLength));
        }

        if (IsBodyEmpty || (HtmlToText.Convert(BodyHtml).Trim().Length == 0 && MessageHtmlChecks.ReferencedImageIds(BodyHtml).Count == 0))
        {
            errors.Add(Localizer[L.CorrespondenceMessage.BodyEmpty]);
        }

        if (Attachments.Where(a => !a.Exists).Select(a => a.Name).ToList() is { Count: > 0 } missing)
        {
            errors.Add(Localizer.Format(L.CorrespondenceMessage.AttachmentsMissing, string.Join(", ", missing)));
        }

        if (!TryGetDeferredDelivery(out _) && DeliverError is { } deliver)
        {
            errors.Add(deliver);
        }

        if (UseVoting && VotingOptionsText is null)
        {
            errors.Add(Localizer[L.CorrespondenceMessage.VotingEmpty]);
        }

        ValidateCustomFields();
        errors.AddRange(CustomFields.Where(f => f.Error is not null)
            .Select(f => Localizer.Format(L.CorrespondenceMessage.CustomFieldError, f.Name?.Trim() ?? string.Empty, f.Error)));
        if (ImagesOverLimitText() is { } images)
        {
            errors.Add(images);
        }

        return errors;
    }

    /// <summary>
    /// The deferred delivery time (MailerWPiA's rules: later than now + 1 minute, at most 365 days ahead, "H:mm", "HH:mm"
    /// or "H"); null when not deferred. False (with <see cref="DeliverError"/>) when the date or time is invalid.
    /// </summary>
    public bool TryGetDeferredDelivery(out DateTimeOffset? deliverAt)
    {
        deliverAt = null;
        if (!DeliverLater)
        {
            DeliverError = null;
            return true;
        }

        var now = Services.Time.GetLocalNow();
        var day = (DeliverDate ?? now.Date).Date;
        var typed = (DeliverTime ?? string.Empty).Trim().Replace('.', ':');
        if (!DateTime.TryParseExact(typed, DeliverTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            DeliverError = Localizer.Format(L.CorrespondenceMessage.DeliverTimeInvalid, DeliverTime ?? string.Empty);
            return false;
        }

        var local = DateTime.SpecifyKind(day.Add(time.TimeOfDay), DateTimeKind.Unspecified);
        var zone = Services.Time.LocalTimeZone;
        var at = new DateTimeOffset(local, zone.IsInvalidTime(local) ? zone.GetUtcOffset(local.AddHours(1)) : zone.GetUtcOffset(local));
        if (at <= now.AddMinutes(1))
        {
            DeliverError = Localizer.Format(L.CorrespondenceMessage.DeliverNotFuture, local.ToString("g", Localizer.Culture));
            return false;
        }

        if (at > now.AddDays(365))
        {
            DeliverError = Localizer[L.CorrespondenceMessage.DeliverTooFar];
            return false;
        }

        DeliverError = null;
        deliverAt = at;
        return true;
    }

    /// <summary>
    /// Drag and drop (and "Dodaj załączniki"): the existing files not on the list yet (sizes read off the UI thread); MailerWPiA's
    /// warning when the attachments together weigh more than <see cref="CorrespondenceLimits.AttachmentWarningBytes"/>.
    /// </summary>
    public async Task AddAttachmentFilesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var known = Attachments.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Where(p => !known.Contains(p)).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var files = await Task.Run(() => candidates.Select(Probe).Where(f => f.Exists).ToList()).ConfigureAwait(true);
        var added = 0;
        foreach (var (path, length, _) in files)
        {
            if (known.Add(path))
            {
                Attachments.Add(Attachment(path, length, exists: true));
                added++;
            }
        }

        if (added > 0 && AttachmentsBytes > CorrespondenceLimits.AttachmentWarningBytes)
        {
            await Services.Dialogs.ShowInfoAsync(
                Localizer[L.CorrespondenceMessage.LargeAttachmentsTitle],
                Localizer.Format(L.CorrespondenceMessage.LargeAttachments, CorrespondenceText.Size(Localizer, AttachmentsBytes))).ConfigureAwait(true);
        }
    }

    /// <summary>Reads again whether the attachment files exist and how large they are (off the UI thread; before sending).</summary>
    public async Task RefreshAttachmentsAsync()
    {
        var current = Attachments.ToList();
        if (current.Count == 0)
        {
            return;
        }

        var probes = await Task.Run(() => current.Select(a => Probe(a.Path)).ToList()).ConfigureAwait(true);
        for (var i = 0; i < current.Count; i++)
        {
            var (path, length, exists) = probes[i];
            var index = Attachments.IndexOf(current[i]);
            if (index >= 0 && (current[i].Exists != exists || current[i].Length != length))
            {
                Attachments[index] = Attachment(path, length, exists);
            }
        }
    }

    /// <summary>Takes the options of the settings (the last draft itself is restored by the window, with its images).</summary>
    public void LoadFrom(CorrespondenceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Salutation = string.IsNullOrWhiteSpace(settings.Salutation) ? BuiltInFields.DefaultSalutation : settings.Salutation.Trim();
        _savedSignatureName = string.IsNullOrWhiteSpace(settings.SignatureName) ? null : settings.SignatureName.Trim();
        DelaySeconds = settings.DelaySeconds;
        SendMode = SendModeChoices.FirstOrDefault(o => o.Value == settings.SendMode) ?? SendModeChoices[0];
        BccChunkSize = settings.BccChunkSize;
        ReadReceipt = settings.ReadReceipt;
        GroupByAddress = settings.GroupByAddress;
        UseVoting = settings.VotingEnabled;
        VotingOptions = string.IsNullOrWhiteSpace(settings.VotingOptions) ? CorrespondenceSettings.DefaultVotingOptions : settings.VotingOptions;
        DeliverTime = string.IsNullOrWhiteSpace(settings.DeferTime) ? CorrespondenceSettings.DefaultDeferTime : settings.DeferTime.Trim();
        if (_savedSignatureName is not null && Signatures.FirstOrDefault(s => string.Equals(s.Name, _savedSignatureName, StringComparison.OrdinalIgnoreCase)) is { } saved)
        {
            SelectedSignature = saved;
        }
    }

    /// <summary>Writes the options (and the subject as typed) into the settings.</summary>
    public void SaveTo(CorrespondenceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Subject = Subject ?? string.Empty;
        settings.Salutation = string.IsNullOrWhiteSpace(Salutation) ? BuiltInFields.DefaultSalutation : Salutation.Trim();
        settings.SignatureName = SelectedSignature?.Name ?? string.Empty;
        settings.DelaySeconds = DelaySeconds;
        settings.SendMode = SendMode.Value;
        settings.BccChunkSize = BccChunkSize;
        settings.ReadReceipt = ReadReceipt;
        settings.GroupByAddress = GroupByAddress;
        settings.VotingEnabled = UseVoting;
        settings.VotingOptions = VotingOptionsText ?? CorrespondenceSettings.DefaultVotingOptions;
        settings.DeferTime = string.IsNullOrWhiteSpace(DeliverTime) ? CorrespondenceSettings.DefaultDeferTime : DeliverTime.Trim();
    }

    /// <summary>
    /// Ctrl+V: the U1c paste workflow (<c>HtmlPasteWorkflow</c> without a template engine) over the clipboard: the fragment
    /// with its <c>{{fields}}</c> repaired (<see cref="FieldTemplate.RepairHtml"/>), its images registered as body images
    /// (within the limits of a message), the warnings in <see cref="PasteProblemsText"/>. Null when nothing could be pasted.
    /// </summary>
    public async Task<RichPaste?> PasteFormattedAsync()
    {
        var workflow = new HtmlPasteWorkflow(Services.Clipboard, Correspondence.PastedImages, engine: null);
        var content = workflow.ReadClipboard();
        if (content is null || content.IsEmpty)
        {
            return null;
        }

        PasteProblemsText = string.Empty;
        var token = _pastes.Next();
        HtmlPasteResult result;
        try
        {
            result = await workflow.ImportAsync(content, [], [], token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }

        if (result.Kind == HtmlPasteKind.TooLarge)
        {
            PasteProblemsText = Localizer[L.CorrespondenceMessage.PasteTooLarge];
            return null;
        }

        var problems = HtmlPasteProblemViewModel.Group(
            result.Problems.Where(p => p.Severity != MessageIssueSeverity.Info && p.Kind != HtmlImportProblemKind.LinkOutsideAllowedDomains),
            Localizer).Select(p => p.Text).ToList();
        var images = new List<MessageImage>();
        foreach (var image in result.Images)
        {
            if (RegisterImage(image) is { } registered)
            {
                images.Add(registered);
            }
            else
            {
                problems.Add(Localizer.Format(L.CorrespondenceMessage.ImageLimit, CorrespondenceLimits.MaxImagesPerMessage));
            }
        }

        if (problems.Count > 0)
        {
            PasteProblemsText = Localizer.Format(L.CorrespondenceMessage.PasteProblems, string.Join("; ", problems.Distinct(StringComparer.Ordinal)));
        }

        return result.Kind == HtmlPasteKind.Nothing || string.IsNullOrWhiteSpace(result.Html)
            ? null
            : new RichPaste(FieldTemplate.RepairHtml(result.Html), images);
    }

    /// <summary>
    /// Validates and registers an image (a pasted bitmap, an image of the editor the page does not know yet): PNG, JPEG or
    /// GIF of at most <see cref="CorrespondenceLimits.MaxImageBytes"/>, metadata stripped. An image the body already holds
    /// is returned as it is (the same content gives the same content id).
    /// </summary>
    public MessageImage? AddImage(byte[] data, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!MessageImage.TryCreate(data, fileName, null, out var image, out var error, CorrespondenceLimits.MaxImageBytes))
        {
            ReportEditorError(error == MessageImageError.TooLarge
                ? Localizer.Format(L.CorrespondenceMessage.ImageTooLarge, CorrespondenceText.Size(Localizer, CorrespondenceLimits.MaxImageBytes))
                : Localizer.Format(L.CorrespondenceMessage.ImageRejected, UserMessageText.Reason(Localizer, error.ToString())));
            return null;
        }

        if (RegisterImage(image!) is { } registered)
        {
            return registered;
        }

        ReportEditorError(Localizer.Format(L.CorrespondenceMessage.ImageLimit, CorrespondenceLimits.MaxImagesPerMessage));
        return null;
    }

    /// <summary>Shows an error of the editor (a refused image, a paste that failed) under the editor.</summary>
    public void ReportEditorError(string text) => PasteProblemsText = text ?? string.Empty;

    /// <summary>Loads the template <paramref name="id"/> (a built-in or a saved one) as <see cref="SelectedTemplate"/> does:
    /// asking first when the page holds another message. True when it was loaded.</summary>
    public async Task<bool> LoadTemplateAsync(string id)
    {
        if (Templates.FirstOrDefault(t => t.Id == id) is not { } option)
        {
            RefreshTemplates();
            if (Templates.FirstOrDefault(t => t.Id == id) is not { } refreshed)
            {
                return false;
            }

            option = refreshed;
        }

        if (Equals(SelectedTemplate, option))
        {
            ChangeSelectedTemplate(null);
        }

        SelectedTemplate = option;
        if (TemplateLoading is { } loading)
        {
            await loading.ConfigureAwait(true);
        }

        return Equals(SelectedTemplate, option);
    }

    /// <summary>A body image by content id registered without validation (the image was validated when it was created):
    /// the existing one when the body holds it, null when the body is full.</summary>
    internal MessageImage? RegisterImage(MessageImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (BodyImages.TryGetValue(image.ContentId, out var existing))
        {
            return existing;
        }

        var images = BodyImages;
        if (images.Count >= CorrespondenceLimits.MaxImagesPerMessage)
        {
            // Images the body no longer references do not count (the editor registers them again if an undo brings them back).
            var referenced = MessageHtmlChecks.ReferencedImageIds(BodyHtml).Concat(MessageHtmlChecks.ReferencedImageIds(ListItemTemplate))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            images = images.Where(i => referenced.Contains(i.Key)).ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase);
            if (images.Count >= CorrespondenceLimits.MaxImagesPerMessage)
            {
                return null;
            }
        }

        BodyImages = new Dictionary<string, MessageImage>(images, StringComparer.OrdinalIgnoreCase) { [image.ContentId] = image };
        return image;
    }

    /// <summary>The template list again (a template was saved, deleted or imported, here or in another window).</summary>
    internal void RefreshTemplates()
    {
        var selected = SelectedTemplate?.Id;
        Templates = [.. Correspondence.Templates.All.Select(t =>
        {
            var builtIn = CorrespondenceTemplateLibrary.IsBuiltIn(t.Id);
            return new TemplateOption(t.Id, builtIn ? Localizer.Format(L.CorrespondenceMessage.BuiltInTemplate, t.Name) : t.Name, builtIn);
        })];
        ChangeSelectedTemplate(Templates.FirstOrDefault(t => t.Id == selected));
        ExportTemplatesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The text of images over the limits of one message (body and signature), or null.</summary>
    internal string? ImagesOverLimitText()
    {
        var referenced = MessageHtmlChecks.ReferencedImageIds(BodyHtml).Concat(MessageHtmlChecks.ReferencedImageIds(ListItemTemplate))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var images = BodyImages.Values.Where(i => referenced.Contains(i.ContentId))
            .Concat(LoadedSignature?.Images ?? [])
            .DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var bytes = images.Sum(i => (long)i.Data.Length);
        return images.Count > CorrespondenceLimits.MaxImagesPerMessage || bytes > CorrespondenceLimits.MaxImagesBytesPerMessage
            ? Localizer.Format(L.CorrespondenceMessage.ImagesOverLimit, images.Count, CorrespondenceText.Size(Localizer, bytes),
                CorrespondenceLimits.MaxImagesPerMessage, CorrespondenceText.Size(Localizer, CorrespondenceLimits.MaxImagesBytesPerMessage))
            : null;
    }

    /// <summary>"Tak;Nie": the options trimmed, empty ones left out; null when none is left.</summary>
    internal static string? NormalizeVotingOptions(string? options)
    {
        var parts = (options ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : string.Join(';', parts);
    }

    /// <summary>The template choice without loading it.</summary>
    private void ChangeSelectedTemplate(TemplateOption? option)
    {
        _changingTemplate = true;
        try
        {
            SelectedTemplate = option;
        }
        finally
        {
            _changingTemplate = false;
        }
    }

    partial void OnSelectedTemplateChanged(TemplateOption? oldValue, TemplateOption? newValue)
    {
        if (!_changingTemplate && newValue is not null)
        {
            TemplateLoading = LoadSelectedTemplateAsync(newValue, oldValue);
        }
    }

    partial void OnSelectedSignatureChanged(SignatureOption? value)
    {
        if (!_refreshingSignatures)
        {
            SignatureLoading = LoadSignatureAsync(value?.Name);
        }
    }

    partial void OnGroupByAddressChanged(bool value) => RefreshFieldChoices();

    partial void OnBccChunkSizeChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, CorrespondenceLimits.MaxBccPerMessage);
        if (clamped != value)
        {
            BccChunkSize = clamped;
        }
    }

    partial void OnDelaySecondsChanged(double value)
    {
        var clamped = double.IsFinite(value) ? Math.Clamp(value, 0, CorrespondenceSettings.MaxDelaySeconds) : 3;
        if (!clamped.Equals(value))
        {
            DelaySeconds = clamped;
        }
    }

    partial void OnSendModeChanged(Option<CorrespondenceSendMode> value) => OnPropertyChanged(nameof(IsBcc));

    partial void OnVotingOptionsChanged(string value) => OnPropertyChanged(nameof(VotingOptionsText));

    partial void OnDeliverLaterChanged(bool value) => TryGetDeferredDelivery(out _);

    partial void OnDeliverDateChanged(DateTime? value) => TryGetDeferredDelivery(out _);

    partial void OnDeliverTimeChanged(string value) => TryGetDeferredDelivery(out _);

    private async Task LoadSelectedTemplateAsync(TemplateOption option, TemplateOption? previous)
    {
        if (Correspondence.Templates.Find(option.Id) is not { } template)
        {
            RefreshTemplates();
            return;
        }

        var holdsMessage = !IsBodyEmpty || !string.IsNullOrWhiteSpace(Subject);
        var differs = !string.Equals(Subject?.Trim(), template.Subject?.Trim(), StringComparison.Ordinal)
                      || !string.Equals(BodyHtml?.Trim(), template.BodyHtml?.Trim(), StringComparison.Ordinal);
        if (holdsMessage && differs && !await Services.Dialogs.ConfirmAsync(
                Localizer[L.CorrespondenceMessage.TemplatesTitle],
                Localizer.Format(L.CorrespondenceMessage.ReplaceMessage, template.Name),
                destructive: false).ConfigureAwait(true))
        {
            ChangeSelectedTemplate(previous);
            return;
        }

        IReadOnlyDictionary<string, MessageImage> images;
        try
        {
            images = await Correspondence.Templates.LoadImagesAsync(template).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            images = new Dictionary<string, MessageImage>();
        }

        foreach (var image in images.Values)
        {
            RegisterImage(image);
        }

        Subject = template.Subject ?? string.Empty;
        ListItemTemplate = template.ListItemTemplate ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(template.Salutation))
        {
            Salutation = template.Salutation.Trim();
        }

        CustomFields.Clear();
        foreach (var field in template.CustomFields ?? [])
        {
            CustomFields.Add(new CustomFieldViewModel(field.Name, field.Value));
        }

        if (template.VotingEnabled is { } voting)
        {
            UseVoting = voting;
        }

        if (!string.IsNullOrWhiteSpace(template.VotingOptions))
        {
            VotingOptions = template.VotingOptions;
        }

        if (template.GroupByAddress is { } group)
        {
            GroupByAddress = group;
        }

        TemplateName = template.Name ?? string.Empty;
        BodyHtml = template.BodyHtml ?? string.Empty;
    }

    private async Task LoadSignatureAsync(string? name)
    {
        var token = _signatureLoads.Next();
        _signature = null;
        _signatureName = null;
        SignatureProblemsText = string.Empty;
        if (name is null || Correspondence.Signatures is not { } source)
        {
            return;
        }

        var codePage = AnsiCodePage;
        try
        {
            // Parsing (CSS inlining, sanitizing, images) runs on the thread pool, whatever the source does itself.
            var result = await Task.Run(() => source.ReadAsync(name, codePage, token), token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (result is null)
            {
                SignatureProblemsText = Localizer.Format(L.CorrespondenceMessage.SignatureNotFound, name);
                return;
            }

            _signature = result.Signature;
            _signatureName = name;
            SignatureProblemsText = result.Problems.Count == 0
                ? string.Empty
                : Localizer.Format(L.CorrespondenceMessage.SignatureProblems, name,
                    string.Join("; ", result.Problems.Select(p => CorrespondenceText.SignatureProblem(Localizer, p)).Distinct(StringComparer.Ordinal)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another signature was chosen meanwhile.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!token.IsCancellationRequested)
            {
                SignatureProblemsText = Localizer.Format(L.CorrespondenceMessage.SignatureUnreadable, name, Services.Errors.Translate(ex));
            }
        }
    }

    private SignatureOption NoSignatureOption() => new(null, Localizer[L.CorrespondenceMessage.NoSignature]);

    /// <summary>Whether a file exists and its size (null-safe for paths that cannot be read).</summary>
    private static (string Path, long Length, bool Exists) Probe(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            return info.Exists ? (full, info.Length, true) : (full, 0, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return (path, 0, false);
        }
    }

    private AttachmentViewModel Attachment(string path, long length, bool exists) => new(
        path,
        Path.GetFileName(path),
        exists ? length : 0,
        exists ? CorrespondenceText.Size(Localizer, length) : Localizer[L.CorrespondenceMessage.AttachmentMissing],
        exists);

    private void RefreshAttachmentsHeader()
    {
        AttachmentsHeaderText = Attachments.Count == 0
            ? Localizer[L.CorrespondenceMessage.AttachmentsHeader]
            : Localizer.Format(L.CorrespondenceMessage.AttachmentsHeaderCount, Attachments.Count, CorrespondenceText.Size(Localizer, AttachmentsBytes));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentsBytes));
        RemoveAttachmentsCommand.NotifyCanExecuteChanged();
        ClearAttachmentsCommand.NotifyCanExecuteChanged();
    }

    private void OnCustomFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var gone in _watchedFields.Where(f => !CustomFields.Contains(f)).ToList())
        {
            gone.PropertyChanged -= OnCustomFieldChanged;
            _watchedFields.Remove(gone);
        }

        foreach (var field in CustomFields)
        {
            if (_watchedFields.Add(field))
            {
                field.PropertyChanged += OnCustomFieldChanged;
            }
        }

        OnFieldsChanged();
    }

    private void OnCustomFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CustomFieldViewModel.Name) or nameof(CustomFieldViewModel.Value))
        {
            OnFieldsChanged();
        }
    }

    private void OnFieldsChanged()
    {
        ValidateCustomFields();
        RefreshFieldChoices();
    }

    /// <summary>Sets <see cref="CustomFieldViewModel.Error"/> of every custom field (see the resource texts for the rules).</summary>
    private void ValidateCustomFields()
    {
        var fields = CustomFields.ToList();
        var columns = Recipients.KnownColumns.Select(FieldKey.Of).ToHashSet(StringComparer.Ordinal);

        // A value may use columns and built-ins; a name that is a column or a built-in refers to them, not to the custom
        // field (which is an error of its own).
        var customKeys = fields.Where(f => !IsBuiltInName(f.Name)).Select(f => FieldKey.Of(f.Name))
            .Where(k => k.Length > 0 && !columns.Contains(k)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var name = field.Name?.Trim() ?? string.Empty;
            var key = FieldKey.Of(name);
            string? error = null;
            if (name.Length == 0)
            {
                error = Localizer[L.CorrespondenceMessage.CustomFieldNameEmpty];
            }
            else if (name.Length > FieldTemplate.MaxFieldNameLength || name.IndexOfAny(['{', '}', '|']) >= 0 || key.Length == 0)
            {
                error = Localizer.Format(L.CorrespondenceMessage.CustomFieldNameInvalid, FieldDelimiters, FieldTemplate.MaxFieldNameLength);
            }
            else if (IsBuiltInName(name))
            {
                error = Localizer[L.CorrespondenceMessage.CustomFieldBuiltIn];
            }
            else if (!seen.Add(key))
            {
                error = Localizer[L.CorrespondenceMessage.CustomFieldDuplicate];
            }
            else if (columns.Contains(key))
            {
                error = Localizer[L.CorrespondenceMessage.CustomFieldColumn];
            }
            else if (FieldTemplate.UsedFields(field.Value).Where(u => customKeys.Contains(FieldKey.Of(u))).ToList() is { Count: > 0 } used)
            {
                error = Localizer.Format(L.CorrespondenceMessage.CustomFieldUsesCustom, string.Join(", ", used.Select(u => "{{" + u + "}}")));
            }

            if (!string.Equals(field.Error, error, StringComparison.Ordinal))
            {
                field.Error = error;
            }
        }
    }

    /// <summary>The name of a built-in field or of a column that duplicates one ("E-mail", "Adres e-mail", "Name" …).</summary>
    private static bool IsBuiltInName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && (BuiltInFields.All.Any(b => FieldKey.Same(b, name)) || BuiltInFields.IsBuiltInColumn(name.Trim()));

    private void RefreshFieldChoices()
    {
        var names = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? name)
        {
            var trimmed = name?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && trimmed.Length <= FieldTemplate.MaxFieldNameLength && trimmed.IndexOfAny(['{', '}', '|']) < 0
                && FieldKey.Of(trimmed) is { Length: > 0 } key && keys.Add(key))
            {
                names.Add(trimmed);
            }
        }

        Add(BuiltInFields.Salutation);
        Add(BuiltInFields.Name);
        Add(BuiltInFields.Email);
        if (GroupByAddress)
        {
            Add(BuiltInFields.List);
            Add(BuiltInFields.Count);
        }

        foreach (var field in CustomFields)
        {
            Add(field.Name);
        }

        foreach (var column in Recipients.KnownColumns)
        {
            if (!BuiltInFields.IsBuiltInColumn(column))
            {
                Add(column);
            }
        }

        var choices = names.Select(n => "{{" + n + "}}").ToList();
        if (!choices.SequenceEqual(FieldChoices, StringComparer.Ordinal))
        {
            FieldChoices = choices;
        }
    }

    /// <summary>"Wstaw pole": asks the view to insert <paramref name="field"/> at the caret.</summary>
    [RelayCommand]
    private void InsertField(string? field)
    {
        if (!string.IsNullOrEmpty(field))
        {
            InsertFieldRequested?.Invoke(this, field);
        }
    }

    /// <summary>The Outlook signatures again (listed off the UI thread); the chosen one stays chosen and is read again.</summary>
    [RelayCommand]
    private async Task RefreshSignaturesAsync()
    {
        var source = Correspondence.Signatures;
        IReadOnlyList<string> names = [];
        string? problem = null;
        if (source is not null)
        {
            try
            {
                names = await Task.Run(source.List).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = Localizer.Format(L.CorrespondenceMessage.SignaturesUnreadable, Services.Errors.Translate(ex));
            }
        }

        var keep = SelectedSignature?.Name ?? _savedSignatureName;
        _savedSignatureName = null;
        IReadOnlyList<SignatureOption> options = [NoSignatureOption(), .. names.Select(n => new SignatureOption(n, n))];
        _refreshingSignatures = true;
        try
        {
            Signatures = options;
            SelectedSignature = options.FirstOrDefault(o => keep is not null && string.Equals(o.Name, keep, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }
        finally
        {
            _refreshingSignatures = false;
        }

        SignatureLoading = LoadSignatureAsync(SelectedSignature?.Name);
        await SignatureLoading.ConfigureAwait(true);
        if (problem is not null)
        {
            SignatureProblemsText = problem;
        }
        else if (names.Count == 0 && source?.Folder is { Length: > 0 } folder)
        {
            SignatureProblemsText = Localizer.Format(L.CorrespondenceMessage.NoSignatures, folder);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddAttachments))]
    private async Task AddAttachmentsAsync()
    {
        if (Correspondence.Dialogs is not { } dialogs)
        {
            return;
        }

        var files = await dialogs.OpenFilesAsync(
            Localizer[L.CorrespondenceMessage.AddAttachmentsTitle],
            [new FileFilter(Localizer[L.CorrespondenceMessage.AllFiles], "*.*")]).ConfigureAwait(true);
        await AddAttachmentFilesAsync(files).ConfigureAwait(true);
    }

    private bool CanAddAttachments() => Correspondence.Dialogs is not null;

    [RelayCommand(CanExecute = nameof(HasAttachments))]
    private void RemoveAttachments()
    {
        foreach (var attachment in SelectedAttachments?.OfType<AttachmentViewModel>().ToList() ?? [])
        {
            Attachments.Remove(attachment);
        }
    }

    [RelayCommand(CanExecute = nameof(HasAttachments))]
    private void ClearAttachments() => Attachments.Clear();

    [RelayCommand]
    private void AddCustomField() => CustomFields.Add(new CustomFieldViewModel());

    [RelayCommand]
    private void RemoveCustomField(CustomFieldViewModel? field)
    {
        if (field is not null)
        {
            CustomFields.Remove(field);
        }
    }

    /// <summary>Saves the message as a template: over the chosen saved template when the name is the same, else as a new one.</summary>
    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        var name = TemplateName?.Trim() ?? string.Empty;
        var title = Localizer[L.CorrespondenceMessage.TemplatesTitle];
        if (name.Length == 0)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer[L.CorrespondenceMessage.TemplateNameRequired]).ConfigureAwait(true);
            return;
        }

        var template = ToTemplate(name);
        if (SelectedTemplate is { IsBuiltIn: false } selected
            && Correspondence.Templates.Find(selected.Id) is { } stored
            && string.Equals(stored.Name?.Trim(), name, StringComparison.CurrentCultureIgnoreCase))
        {
            template = CorrespondenceTemplateLibrary.Copy(template, selected.Id);
        }

        try
        {
            var saved = await Correspondence.Templates.SaveAsync(template, BodyImages.Values).ConfigureAwait(true);
            RefreshTemplates();
            ChangeSelectedTemplate(Templates.FirstOrDefault(t => t.Id == saved.Id));
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.CorrespondenceMessage.TemplateSaved, saved.Name));
        }
        catch (InvalidOperationException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer.Format(L.CorrespondenceMessage.TemplateTooMany, CorrespondenceSettings.MaxTemplates)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer.Format(L.CorrespondenceMessage.FileFailed, Services.Errors.Translate(ex))).ConfigureAwait(true);
        }
    }

    private bool CanDeleteTemplate() => SelectedTemplate is { IsBuiltIn: false };

    [RelayCommand(CanExecute = nameof(CanDeleteTemplate))]
    private async Task DeleteTemplateAsync()
    {
        if (SelectedTemplate is not { IsBuiltIn: false } selected || Correspondence.Templates.Find(selected.Id) is not { } template)
        {
            return;
        }

        var title = Localizer[L.CorrespondenceMessage.TemplatesTitle];
        if (!await Services.Dialogs.ConfirmAsync(title, Localizer.Format(L.CorrespondenceMessage.DeleteTemplate, template.Name), destructive: true).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await Correspondence.Templates.DeleteAsync(selected.Id).ConfigureAwait(true);
            RefreshTemplates();
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.CorrespondenceMessage.TemplateDeleted, template.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer.Format(L.CorrespondenceMessage.FileFailed, Services.Errors.Translate(ex))).ConfigureAwait(true);
        }
    }

    private bool CanExportTemplates() => Correspondence.Templates.Saved.Count > 0;

    /// <summary>Every saved template (with its images) into a JSON file the operator chooses.</summary>
    [RelayCommand(CanExecute = nameof(CanExportTemplates))]
    private async Task ExportTemplatesAsync()
    {
        var title = Localizer[L.CorrespondenceMessage.ExportTitle];
        var path = await Services.Files.SaveFileAsync(title, TemplatesFileName, [new FileFilter(Localizer[L.CorrespondenceMessage.JsonFilter], "*.json")]).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var json = await Correspondence.Templates.ExportJsonAsync().ConfigureAwait(true);
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.CorrespondenceMessage.Exported, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer.Format(L.CorrespondenceMessage.FileFailed, Services.Errors.Translate(ex))).ConfigureAwait(true);
        }
    }

    /// <summary>Templates of an export file, added with new ids.</summary>
    [RelayCommand]
    private async Task ImportTemplatesAsync()
    {
        var title = Localizer[L.CorrespondenceMessage.ImportTitle];
        var path = await Services.Files.OpenFileAsync(title, [new FileFilter(Localizer[L.CorrespondenceMessage.JsonFilter], "*.json")]).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var json = await Task.Run(() => new FileInfo(path).Length > CorrespondenceTemplateLibrary.MaxFileLength
                ? throw new FormatException("The templates file is too large.")
                : File.ReadAllText(path)).ConfigureAwait(true);
            var result = await Correspondence.Templates.ImportAsync(json).ConfigureAwait(true);
            RefreshTemplates();
            Services.Notifications.Show(
                NotificationSeverity.Success,
                result.ImagesRejected > 0
                    ? Localizer.Format(L.CorrespondenceMessage.ImportedImagesRejected, result.Added, result.ImagesRejected)
                    : Localizer.Format(L.CorrespondenceMessage.Imported, result.Added));
        }
        catch (FormatException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer[L.CorrespondenceMessage.ImportInvalid]).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer.Format(L.CorrespondenceMessage.FileFailed, Services.Errors.Translate(ex))).ConfigureAwait(true);
        }
    }

    /// <summary>After a confirmation (when there is something to lose): clears subject, body, images, custom fields, the list
    /// item template and the attachments.</summary>
    [RelayCommand]
    private async Task NewMessageAsync()
    {
        var holds = !IsBodyEmpty || !string.IsNullOrWhiteSpace(Subject) || CustomFields.Count > 0 || Attachments.Count > 0
                    || !string.IsNullOrWhiteSpace(ListItemTemplate);
        if (holds && !await Services.Dialogs.ConfirmAsync(
                Localizer[L.CorrespondenceMessage.NewMessageTitle], Localizer[L.CorrespondenceMessage.NewMessage], destructive: true).ConfigureAwait(true))
        {
            return;
        }

        Subject = string.Empty;
        BodyHtml = string.Empty;
        BodyImages = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
        CustomFields.Clear();
        ListItemTemplate = string.Empty;
        Attachments.Clear();
        TemplateName = string.Empty;
        PasteProblemsText = string.Empty;
        ChangeSelectedTemplate(null);
    }

    /// <summary>
    /// Follows <see cref="CorrespondenceTemplateLibrary.Changed"/> (an application-wide event) through a weak reference, so a
    /// closed window is never kept alive by the library; the subscription ends at the first event after the page is gone.
    /// </summary>
    private sealed class TemplatesListener
    {
        private readonly WeakReference<MessagePageViewModel> _page;
        private readonly CorrespondenceTemplateLibrary _library;

        private TemplatesListener(MessagePageViewModel page, CorrespondenceTemplateLibrary library)
        {
            _page = new WeakReference<MessagePageViewModel>(page);
            _library = library;
        }

        public static void Attach(MessagePageViewModel page, CorrespondenceTemplateLibrary library)
        {
            var listener = new TemplatesListener(page, library);
            library.Changed += listener.OnChanged;
        }

        private void OnChanged(object? sender, EventArgs e)
        {
            if (_page.TryGetTarget(out var page))
            {
                _ = page.Services.Dispatcher.RunAsync(page.RefreshTemplates);
            }
            else
            {
                _library.Changed -= OnChanged;
            }
        }
    }
}
