using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Correspondence.Outlook;
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
/// "3 · Podgląd" (design note 6.4 and 7.3): the message as the chosen recipient sees it – MailerWPiA's card (Od/Do/Temat/
/// Załączniki, body, 700 / 380 px / full) – with the warnings (unknown and empty fields, compatibility, size), "Podgląd z
/// Outlooka", "Otwórz w przeglądarce" and "Zapisz jako .html". Previews stay in memory (D5).
/// </summary>
/// <remarks>
/// The recipients are the messages of the send list (the first <see cref="MaxRecipients"/>); a BCC run is previewed as it is
/// sent – with no row's fields and the own address in "Do". Building the card and counting empty fields over the send list
/// run off the UI thread; entering the tab, choosing a recipient or a width refreshes after <see cref="RefreshDelay"/>.
/// "Zapisz jako .html" is an operator export: the card and its image files in a folder the operator picks.
/// </remarks>
public sealed partial class PreviewPageViewModel : ObservableObject
{
    /// <summary>The recipients offered (MailerWPiA's 500).</summary>
    public const int MaxRecipients = 500;

    private readonly SerialCancellation _refreshes = new();
    private bool _changingRecipients;
    private bool _loadingOptions;

    public PreviewPageViewModel(DialogServices services, CorrespondenceServices correspondence, RecipientsPageViewModel recipients,
        MessagePageViewModel message, OutlookConnectionViewModel outlook)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        Recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Outlook = outlook ?? throw new ArgumentNullException(nameof(outlook));
        Debouncer = new Debouncer(services.Time, TimeSpan.FromMilliseconds(200));
        WidthChoices = [.. Enum.GetValues<CorrespondencePreviewWidth>().Select(v => new Option<CorrespondencePreviewWidth>(v, Localizer[CorrespondenceText.WidthKey(v)]))];
        _loadingOptions = true;
        Width = WidthChoices[0];
        _loadingOptions = false;
    }

    /// <summary>The first 500 messages: "Name &lt;email&gt;".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Option<CorrespondenceUnit>> PreviewRecipients { get; private set; } = [];

    [ObservableProperty]
    public partial Option<CorrespondenceUnit>? SelectedRecipient { get; set; }

    public IReadOnlyList<Option<CorrespondencePreviewWidth>> WidthChoices { get; }

    [ObservableProperty]
    public partial Option<CorrespondencePreviewWidth> Width { get; set; }

    /// <summary>The card shown by the preview control (HTML and images from memory).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInBrowserCommand), nameof(SaveHtmlCommand))]
    public partial HtmlPreviewContent Preview { get; private set; } = HtmlPreviewContent.Empty;

    /// <summary>"obrazków osadzonych: 3 • pola: zwrot, Miejscowość".</summary>
    [ObservableProperty]
    public partial string InfoText { get; private set; } = string.Empty;

    /// <summary>Unknown fields (with the available ones), empty fields over the send list, lint findings, size.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    public partial IReadOnlyList<string> Warnings { get; private set; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>The preview was built by Outlook ("Podgląd z Outlooka").</summary>
    [ObservableProperty]
    public partial bool IsFromOutlook { get; private set; }

    /// <summary>"Wygląda dobrze – …" when there is no warning.</summary>
    [ObservableProperty]
    public partial string OkText { get; private set; } = string.Empty;

    /// <summary>"Podgląd z Outlooka" is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewViaOutlookCommand))]
    public partial bool IsRendering { get; private set; }

    /// <summary>The delay of a scheduled refresh (tests set zero).</summary>
    internal TimeSpan RefreshDelay
    {
        get => Debouncer.Delay;
        set => Debouncer.Delay = value;
    }

    /// <summary>The last refresh (tests await it), or null.</summary>
    internal Task? Refreshing { get; private set; }

    private Debouncer Debouncer { get; }

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private RecipientsPageViewModel Recipients { get; }

    private MessagePageViewModel Message { get; }

    private OutlookConnectionViewModel Outlook { get; }

    private ILocalizer Localizer => Services.Localizer;

    /// <summary>Takes the preview width of the settings.</summary>
    public void LoadFrom(CorrespondenceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _loadingOptions = true;
        try
        {
            Width = WidthChoices.FirstOrDefault(w => w.Value == settings.PreviewWidth) ?? WidthChoices[0];
        }
        finally
        {
            _loadingOptions = false;
        }
    }

    /// <summary>Writes the preview width into the settings.</summary>
    public void SaveTo(CorrespondenceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.PreviewWidth = Width.Value;
    }

    /// <summary>Refreshes after <see cref="RefreshDelay"/> (the tab was entered, the recipient or the width changed); a newer
    /// request replaces this one.</summary>
    public void ScheduleRefresh() => Refreshing = Debouncer.RunAsync(token => RefreshCoreAsync(token));

    partial void OnSelectedRecipientChanged(Option<CorrespondenceUnit>? value)
    {
        if (!_changingRecipients)
        {
            ScheduleRefresh();
        }
    }

    partial void OnWidthChanged(Option<CorrespondencePreviewWidth> value)
    {
        if (!_loadingOptions)
        {
            ScheduleRefresh();
        }
    }

    /// <summary>Rebuilds the preview (also on tab entry and recipient or width change; debounced 200 ms).</summary>
    [RelayCommand]
    private Task RefreshAsync()
    {
        var refresh = RefreshCoreAsync(CancellationToken.None);
        Refreshing = refresh;
        return refresh;
    }

    /// <summary>"Podgląd z Outlooka": the session renders the message (images read into memory).</summary>
    [RelayCommand(CanExecute = nameof(CanPreviewViaOutlook))]
    private async Task PreviewViaOutlookAsync()
    {
        IsRendering = true;
        try
        {
            var session = await Outlook.EnsureConnectedAsync().ConfigureAwait(true);
            if (session is null)
            {
                ShowFailure(Localizer.Format(L.CorrespondenceSend.OutlookUnavailable, Outlook.FailureText ?? Outlook.StateText));
                return;
            }

            var state = await PrepareAsync().ConfigureAwait(true);
            var (message, _) = CorrespondenceSender.Build(state.Options, state.Job);
            OutlookRenderedMessage rendered;
            try
            {
                rendered = await session.RenderAsync(message, CancellationToken.None).ConfigureAwait(true);
            }
            catch (OutlookException ex)
            {
                Outlook.ReportFailure(ex.Failure);
                ShowFailure(Localizer.Format(L.CorrespondencePreview.OutlookFailed, CorrespondenceText.FailureText(Localizer, ex.Failure)));
                return;
            }

            var images = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, image) in rendered.Images)
            {
                if (MessageImage.TryCreate(image.Data, null, image.MediaType, out var created, out _, CorrespondenceLimits.MaxImageBytes))
                {
                    images[id] = created!;
                }
            }

            _refreshes.Cancel();
            var header = new List<PreviewCardLine>
            {
                new(Localizer[L.CorrespondencePreview.To], string.Join(", ", message.To)),
                new(Localizer[L.CorrespondencePreview.Subject], message.Subject),
            };
            Preview = new HtmlPreviewContent(
                PreviewCard.Build(header, Localizer[L.CorrespondencePreview.OutlookNote], rendered.Html, PreviewCard.MaxWidth(Width.Value)),
                images);
            InfoText = Localizer.Format(L.CorrespondencePreview.InfoOutlook, rendered.Images.Count, rendered.Attachments);
            Warnings = [];
            OkText = Localizer[L.CorrespondencePreview.OutlookDone];
            IsFromOutlook = true;
        }
        finally
        {
            IsRendering = false;
        }
    }

    private bool CanPreviewViaOutlook() => !IsRendering && Correspondence.Outlook is not null;

    /// <summary>"Otwórz w przeglądarce": the card through the loopback preview (nothing on disk).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInBrowser))]
    private async Task OpenInBrowserAsync()
    {
        if (Correspondence.BrowserPreview is not { } browser)
        {
            return;
        }

        if (Preview.IsEmpty)
        {
            await RefreshAsync().ConfigureAwait(true);
        }

        try
        {
            await browser.OpenAsync(Preview, Localizer[L.CorrespondencePreview.BrowserTitle]).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            await Services.Dialogs.ShowErrorAsync(Localizer[L.CorrespondencePreview.BrowserTitle],
                Localizer.Format(L.CorrespondencePreview.BrowserFailed, Services.Errors.Translate(ex))).ConfigureAwait(true);
        }
    }

    private bool CanOpenInBrowser() => Correspondence.BrowserPreview is not null;

    /// <summary>"Zapisz jako .html": <c>podglad_wiadomosci.html</c> and the image files next to it in a chosen folder.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveHtml))]
    private async Task SaveHtmlAsync()
    {
        if (Correspondence.Dialogs is not { } dialogs)
        {
            return;
        }

        if (Preview.IsEmpty)
        {
            await RefreshAsync().ConfigureAwait(true);
        }

        var title = Localizer[L.CorrespondencePreview.SaveTitle];
        var folder = await dialogs.PickFolderAsync(title).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var preview = Preview;
        var path = Path.Combine(folder, PreviewCard.FileName);
        try
        {
            if (await Task.Run(() => File.Exists(path)).ConfigureAwait(true)
                && !await Services.Dialogs.ConfirmAsync(title, Localizer.Format(L.CorrespondencePreview.Overwrite, PreviewCard.FileName), destructive: true).ConfigureAwait(true))
            {
                return;
            }

            await Task.Run(async () =>
            {
                var files = ImageFileNames(preview.Images, folder);
                foreach (var (id, image) in preview.Images)
                {
                    await File.WriteAllBytesAsync(Path.Combine(folder, files[id]), image.Data).ConfigureAwait(false);
                }

                await File.WriteAllTextAsync(path, PreviewCard.WithImageFiles(preview.Html, files), new System.Text.UTF8Encoding(true)).ConfigureAwait(false);
            }).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.CorrespondencePreview.Saved, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer.Format(L.CorrespondencePreview.SaveFailed, Services.Errors.Translate(ex))).ConfigureAwait(true);
        }
    }

    private bool CanSaveHtml() => Correspondence.Dialogs is not null;

    /// <summary>
    /// The file names of the images in <paramref name="folder"/>: their own (safe) names, made unique – also against files
    /// of the folder with other content, so an export never replaces a file it did not write (the same image saved again
    /// keeps its name).
    /// </summary>
    private static Dictionary<string, string> ImageFileNames(IReadOnlyDictionary<string, MessageImage> images, string folder)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PreviewCard.FileName };
        var number = 0;
        foreach (var (id, image) in images)
        {
            number++;
            var name = MessageImage.SafeFileName(image.FileName, image.Kind, number);
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            for (var copy = 2; !used.Add(name) || HoldsOtherContent(Path.Combine(folder, name), image.Data); copy++)
            {
                name = $"{stem}-{copy}{extension}";
            }

            names[id] = name;
        }

        return names;
    }

    /// <summary>A file exists at <paramref name="path"/> with content other than <paramref name="data"/> (or cannot be read).</summary>
    private static bool HoldsOtherContent(string path, byte[] data)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && (file.Length != data.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>What the preview renders: the draft, the chosen (or first, or a sample) unit, the options.</summary>
    private async Task<PreviewState> PrepareAsync()
    {
        var draft = await Message.DraftAsync().ConfigureAwait(true);
        var set = CorrespondenceSendSet.Of(Recipients);
        var bcc = Message.IsBcc;
        var group = Message.GroupByAddress;

        // The rows' addresses, names and fields never change: the messages are made off the UI thread (long lists).
        var units = bcc ? [] : await Task.Run(() => set.Units(group)).ConfigureAwait(true);
        RefreshRecipients(units);
        var unit = bcc
            ? CorrespondenceSendSet.BccUnit
            : SelectedRecipient?.Value ?? new CorrespondenceUnit(Localizer[L.CorrespondencePreview.SampleAddress], string.Empty, [new Dictionary<string, string>()]);
        var own = Outlook.OwnAddress;
        var job = bcc
            ? new CorrespondenceSendJob(unit, [own ?? Localizer[L.CorrespondencePreview.OwnAddress]], [], [], [])
            : new CorrespondenceSendJob(unit, [unit.Email], [], [], []);
        var options = new CorrespondenceSendOptions
        {
            Draft = draft,
            KnownColumns = set.KnownColumns,
            Attachments = [.. Message.Attachments.Select(a => new OutlookAttachment(a.Path, a.Name))],
            Account = Outlook.AccountAddress,
            OnBehalfOf = string.IsNullOrWhiteSpace(Outlook.OnBehalfOf) ? null : Outlook.OnBehalfOf.Trim(),
            ReadReceipt = Message.ReadReceipt,
            VotingOptions = Message.UseVoting ? Message.VotingOptionsText : null,
        };
        var attachmentNames = Message.Attachments.Select(a => a.Name).ToList();
        return new PreviewState(options, job, set, units, bcc, attachmentNames, Message.AttachmentsBytes);
    }

    /// <summary>The recipient choice: the first <see cref="MaxRecipients"/> units, the chosen one kept (by address).</summary>
    private void RefreshRecipients(IReadOnlyList<SendUnit> units)
    {
        var keep = SelectedRecipient?.Value is { } current ? CorrespondenceSendSet.Key(current.Email) : null;
        var options = units.Take(MaxRecipients).Select(u => new Option<CorrespondenceUnit>(u.Unit, u.Text)).ToList();
        _changingRecipients = true;
        try
        {
            PreviewRecipients = options;
            SelectedRecipient = options.FirstOrDefault(o => keep is not null && CorrespondenceSendSet.Key(o.Value.Email) == keep) ?? options.FirstOrDefault();
        }
        finally
        {
            _changingRecipients = false;
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var token = _refreshes.Next();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
        var state = await PrepareAsync().ConfigureAwait(true);
        var header = Header(state);
        var width = PreviewCard.MaxWidth(Width.Value);
        var fieldChoices = Message.FieldChoices;
        var listItemFields = Message.GroupByAddress ? FieldTemplate.Parse(state.Options.Draft.ListItemTemplate) : [];
        PreviewResult result;
        try
        {
            result = await Task.Run(() => Build(state, header, width, fieldChoices, listItemFields, linked.Token), linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return;
        }

        if (!_refreshes.IsCurrent(token))
        {
            return;
        }

        Preview = result.Preview;
        InfoText = result.Info;
        Warnings = result.Warnings;
        OkText = Localizer[L.CorrespondencePreview.Ok];
        IsFromOutlook = false;
    }

    /// <summary>The card, the info line and the warnings (off the UI thread).</summary>
    private PreviewResult Build(PreviewState state, PreviewHeader header, int? width, IReadOnlyList<string> available,
        IReadOnlyList<FieldToken> listItemFields, CancellationToken cancellationToken)
    {
        var (_, rendered) = CorrespondenceSender.Build(state.Options, state.Job);
        var images = rendered.Images.DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase).ToDictionary(i => i.ContentId, StringComparer.OrdinalIgnoreCase);
        var preview = new HtmlPreviewContent(PreviewCard.Build(header.Lines(Localizer, rendered.Subject), null, rendered.HtmlDocument, width), images);

        var info = new List<string>();
        if (rendered.Images.Count > 0)
        {
            info.Add(Localizer.Format(L.CorrespondencePreview.InfoImages, rendered.Images.Count));
        }

        var draft = state.Options.Draft;
        var used = FieldTemplate.UsedFields(draft.Subject).Concat(FieldTemplate.UsedFields(draft.BodyHtml))
            .Concat(listItemFields.Select(t => t.Field))
            .DistinctBy(FieldKey.Of, StringComparer.Ordinal)
            .ToList();
        if (used.Count > 0)
        {
            info.Add(Localizer.Format(L.CorrespondencePreview.InfoFields, string.Join(", ", used)));
        }

        var warnings = new List<string>();
        if (rendered.UnknownFields.Count > 0)
        {
            warnings.Add(Localizer.Format(L.CorrespondencePreview.UnknownFields,
                string.Join(", ", rendered.UnknownFields.Select(f => "{{" + f + "}}")), string.Join(", ", available)));
        }

        if (!state.IsBcc)
        {
            warnings.AddRange(EmptyFieldWarnings(state, listItemFields, cancellationToken));
        }

        var attachmentBytes = state.AttachmentBytes;
        warnings.AddRange(MailCompatibilityLint.Check(rendered.HtmlDocument, rendered.ImageBytes + attachmentBytes)
            .Select(f => CorrespondenceText.Lint(Localizer, f)));
        if (rendered.ExceedsImageLimits)
        {
            warnings.Add(Localizer.Format(L.CorrespondenceMessage.ImagesOverLimit, rendered.Images.Count, CorrespondenceText.Size(Localizer, rendered.ImageBytes),
                CorrespondenceLimits.MaxImagesPerMessage, CorrespondenceText.Size(Localizer, CorrespondenceLimits.MaxImagesBytesPerMessage)));
        }

        if (attachmentBytes > CorrespondenceLimits.AttachmentWarningBytes)
        {
            warnings.Add(Localizer.Format(L.CorrespondencePreview.LargeAttachments, CorrespondenceText.Size(Localizer, attachmentBytes)));
        }

        return new PreviewResult(preview, string.Join("  •  ", info), warnings);
    }

    /// <summary>
    /// "Pole {{X}} jest puste u 12 z 95 odbiorców": per field of the subject and the body without a fallback (not
    /// <c>{{email}}</c>, <c>{{nazwa}}</c> – MailerWPiA – and not <c>{{lista}}</c>) over the messages of the send list, and per
    /// field of the list item template over the rows of grouped messages.
    /// </summary>
    private List<string> EmptyFieldWarnings(PreviewState state, IReadOnlyList<FieldToken> listItemFields, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (state.Units.Count == 0)
        {
            return warnings;
        }

        var draft = state.Options.Draft;
        var skipped = new HashSet<string>(StringComparer.Ordinal) { "email", "mail", "adresemail", "nazwa", "name", BuiltInFields.List };
        var fields = FieldTemplate.Parse(draft.Subject).Concat(FieldTemplate.Parse(draft.BodyHtml))
            .GroupBy(t => FieldKey.Of(t.Field), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0 && !skipped.Contains(g.Key) && g.Any(t => t.Fallback is null))
            .Select(g => g.First().Field)
            .ToList();
        var counts = new int[fields.Count];
        foreach (var unit in state.Units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = unit.Unit.Rows.Count > 0 ? unit.Unit.Rows[0] : new Dictionary<string, string>();
            var set = new RecipientFieldSet(unit.Unit.Email, unit.Unit.Name, first, draft.Salutation, draft.CustomFields, state.Set.KnownColumns,
                count: unit.Unit.Rows.Count);
            for (var i = 0; i < fields.Count; i++)
            {
                if (set.Lookup(fields[i]) is { } value && string.IsNullOrWhiteSpace(value))
                {
                    counts[i]++;
                }
            }
        }

        for (var i = 0; i < fields.Count; i++)
        {
            if (counts[i] > 0)
            {
                warnings.Add(Localizer.Format(L.CorrespondencePreview.EmptyField, "{{" + fields[i] + "}}", counts[i], state.Units.Count));
            }
        }

        var itemFields = listItemFields.GroupBy(t => FieldKey.Of(t.Field), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0 && g.Any(t => t.Fallback is null))
            .Select(g => g.First().Field)
            .ToList();
        if (itemFields.Count == 0)
        {
            return warnings;
        }

        var rows = 0;
        var itemCounts = new int[itemFields.Count];
        foreach (var unit in state.Units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in unit.Unit.Rows)
            {
                rows++;
                var set = new RecipientFieldSet(unit.Unit.Email, unit.Unit.Name, row, draft.Salutation, draft.CustomFields, state.Set.KnownColumns);
                for (var i = 0; i < itemFields.Count; i++)
                {
                    if (set.Lookup(itemFields[i]) is { } value && string.IsNullOrWhiteSpace(value))
                    {
                        itemCounts[i]++;
                    }
                }
            }
        }

        for (var i = 0; i < itemFields.Count; i++)
        {
            if (itemCounts[i] > 0)
            {
                warnings.Add(Localizer.Format(L.CorrespondencePreview.EmptyListField, "{{" + itemFields[i] + "}}", itemCounts[i], rows));
            }
        }

        return warnings;
    }

    /// <summary>The card's header without the subject (read on the UI thread; the subject comes from the rendering).</summary>
    private PreviewHeader Header(PreviewState state)
    {
        var from = !string.IsNullOrWhiteSpace(Outlook.OnBehalfOf)
            ? Localizer.Format(L.CorrespondencePreview.OnBehalf, Outlook.OnBehalfOf.Trim())
            : Outlook.SelectedAccount?.ToString() ?? Localizer[L.CorrespondencePreview.DefaultAccount];
        var bcc = state.IsBcc ? Localizer.Format(L.CorrespondencePreview.BccCount, state.Set.AddressCount) : null;
        var attachments = state.AttachmentNames.Count == 0 ? Localizer[L.CorrespondencePreview.None] : string.Join(", ", state.AttachmentNames);
        return new PreviewHeader(from, string.Join(", ", state.Job.To), bcc, attachments);
    }

    private void ShowFailure(string text)
    {
        Warnings = [text];
        OkText = string.Empty;
    }

    private sealed record PreviewState(
        CorrespondenceSendOptions Options,
        CorrespondenceSendJob Job,
        CorrespondenceSendSet Set,
        IReadOnlyList<SendUnit> Units,
        bool IsBcc,
        IReadOnlyList<string> AttachmentNames,
        long AttachmentBytes)
    {
        public override string ToString() => $"{nameof(PreviewState)} ({Units.Count} messages)";
    }

    private sealed record PreviewResult(HtmlPreviewContent Preview, string Info, IReadOnlyList<string> Warnings)
    {
        public override string ToString() => $"{nameof(PreviewResult)} ({Warnings.Count} warnings)";
    }

    /// <summary>The card's header: Od, Do, UDW (a BCC run), Temat, Załączniki.</summary>
    private sealed record PreviewHeader(string From, string To, string? Bcc, string Attachments)
    {
        public IReadOnlyList<PreviewCardLine> Lines(ILocalizer localizer, string subject)
        {
            var lines = new List<PreviewCardLine>
            {
                new(localizer[L.CorrespondencePreview.From], From),
                new(localizer[L.CorrespondencePreview.To], To),
            };
            if (Bcc is not null)
            {
                lines.Add(new(localizer[L.CorrespondencePreview.Bcc], Bcc));
            }

            lines.Add(new(localizer[L.CorrespondencePreview.Subject], subject));
            lines.Add(new(localizer[L.CorrespondencePreview.Attachments], Attachments));
            return lines;
        }

        public override string ToString() => nameof(PreviewHeader);
    }
}
