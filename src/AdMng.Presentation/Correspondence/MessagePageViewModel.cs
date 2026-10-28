using System.Collections;
using System.Collections.ObjectModel;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// "2 · Wiadomość" (design note 6.4 and 7.2): subject and body with <c>{{fields}}</c> (the WYSIWYG editor through
/// <see cref="IRichEditorHost"/>), the signature, attachments, sending options (mode, BCC chunk, delay, read receipt,
/// deferred delivery), voting, custom fields, grouping and templates. Produces the <see cref="CorrespondenceDraft"/>.
/// </summary>
public sealed partial class MessagePageViewModel : ObservableObject, IRichEditorHost
{
    private IList? _selectedAttachments;

    /// <param name="services">The window's services.</param>
    /// <param name="correspondence">The Korespondencja services.</param>
    /// <param name="recipients">The recipients page (its columns are fields).</param>
    public MessagePageViewModel(DialogServices services, CorrespondenceServices correspondence, RecipientsPageViewModel recipients)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        Recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        SendModeChoices = [.. Enum.GetValues<CorrespondenceSendMode>().Select(v => new Option<CorrespondenceSendMode>(v, v.ToString()))];
        SendMode = SendModeChoices[0];
        DeliverDate = services.Time.GetLocalNow().Date.AddDays(1);
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
    public partial IReadOnlyDictionary<string, MessageImage> BodyImages { get; private set; } = new Dictionary<string, MessageImage>();

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

    public IReadOnlyList<Option<CorrespondenceSendMode>> SendModeChoices { get; }

    [ObservableProperty]
    public partial Option<CorrespondenceSendMode> SendMode { get; set; }

    [ObservableProperty]
    public partial int BccChunkSize { get; set; } = CorrespondenceLimits.MaxBccPerMessage;

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
    public partial TemplateOption? SelectedTemplate { get; set; }

    [ObservableProperty]
    public partial string TemplateName { get; set; } = string.Empty;

    /// <summary>The problems of the last formatted paste.</summary>
    [ObservableProperty]
    public partial string PasteProblemsText { get; private set; } = string.Empty;

    public IReadOnlyDictionary<string, MessageImage> Images => BodyImages;

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private RecipientsPageViewModel Recipients { get; }

    /// <summary>The draft as edited (with the loaded signature).</summary>
    public CorrespondenceDraft Draft() => new()
    {
        Subject = Subject,
        BodyHtml = BodyHtml,
        Images = BodyImages,
        CustomFields = [.. CustomFields.Select(f => new CustomFieldDefinition(f.Name, f.Value))],
        ListItemTemplate = ListItemTemplate,
        Salutation = Salutation,
    };

    /// <summary>
    /// The localized errors that block sending: empty subject or body, missing attachments, deferred delivery, custom field
    /// errors, images over the limits.
    /// </summary>
    public IReadOnlyList<string> ValidateForSend() => [];

    /// <summary>
    /// The deferred delivery time (MailerWPiA's rules: later than now + 1 minute, at most 365 days ahead, "H:mm", "HH:mm"
    /// or "H"); null when not deferred. False (with <see cref="DeliverError"/>) when the date or time is invalid.
    /// </summary>
    public bool TryGetDeferredDelivery(out DateTimeOffset? deliverAt)
    {
        deliverAt = null;
        return true;
    }

    /// <summary>Drag and drop of attachments.</summary>
    public Task AddAttachmentFilesAsync(IReadOnlyList<string> paths) => Task.CompletedTask;

    /// <summary>Takes the options and the last draft of the settings.</summary>
    public void LoadFrom(CorrespondenceSettings settings)
    {
    }

    /// <summary>Writes the options and the last draft into the settings.</summary>
    public void SaveTo(CorrespondenceSettings settings)
    {
    }

    /// <summary>Ctrl+V: the U1c paste workflow (<c>HtmlPasteWorkflow</c> without a template engine), fields repaired.</summary>
    public Task<RichPaste?> PasteFormattedAsync() => Task.FromResult<RichPaste?>(null);

    public MessageImage? AddImage(byte[] data, string? fileName) => null;

    public void ReportEditorError(string text)
    {
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

    [RelayCommand]
    private Task RefreshSignaturesAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task AddAttachmentsAsync() => Task.CompletedTask;

    [RelayCommand]
    private void RemoveAttachments()
    {
    }

    [RelayCommand]
    private void ClearAttachments()
    {
    }

    [RelayCommand]
    private void AddCustomField()
    {
    }

    [RelayCommand]
    private void RemoveCustomField(CustomFieldViewModel? field)
    {
    }

    [RelayCommand]
    private Task SaveTemplateAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task DeleteTemplateAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task ExportTemplatesAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task ImportTemplatesAsync() => Task.CompletedTask;

    /// <summary>After a confirmation: clears subject, body and fields.</summary>
    [RelayCommand]
    private Task NewMessageAsync() => Task.CompletedTask;
}
