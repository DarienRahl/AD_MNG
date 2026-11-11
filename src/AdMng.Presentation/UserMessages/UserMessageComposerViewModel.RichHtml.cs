using System.Collections.ObjectModel;
using System.ComponentModel;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// U1c (design note docs/dev/u1c-u1d-rich-html-and-salutations.md, sections 9.6, 10–12): formatted content pasted from
/// Outlook, Word or a browser with its images, the image strip of the edited template, the paste results with their actions
/// ("Zezwól na domenę …", "Usuń łącze"), the formatting preview, "Wygeneruj tekst z HTML" and the transport bar that stops a
/// send the chosen transport cannot take (images to an older AdMng Watchdog, images over its limits or its storage, HTML
/// longer than it accepts) with the choices that help – never a silent switch or drop. The images of the working template
/// live in memory (<see cref="_images"/>); a saved template's images come from the image store when it is selected.
/// </summary>
/// <remarks>
/// What the editor does and what became of it is said in the status line of the window, through
/// <see cref="EditorStatus"/> (track V, stage V2b): the paste is its operation, with Stop. What is true of the content
/// for as long as it is shown stays next to it: the images that could not be read (<see cref="MissingImagesText"/>) and
/// why the messages do not fit the transport (<see cref="TransportFitText"/>) are notes; the read of a template's
/// images only shows "busy" (<see cref="IsLoadingImages"/>).
/// </remarks>
public sealed partial class UserMessageComposerViewModel
{
    private readonly Dictionary<string, MessageImage> _images = new(StringComparer.OrdinalIgnoreCase);
    private HtmlPasteWorkflow? _pasteWorkflow;
    private OperationScope? _pasting;
    private Task _imagesLoading = Task.CompletedTask;
    private int _imageVersion;
    private bool _richHooked;
    private bool _omitImages;
    private bool _directForRun;
    private bool _fitIsTest;

    // ---- HTML section --------------------------------------------------------------------------------------------

    /// <summary>The images of the edited template (both languages) whose bytes are in memory.</summary>
    public ObservableCollection<MessageImageItemViewModel> TemplateImages { get; } = [];

    public bool HasTemplateImages => TemplateImages.Count > 0;

    /// <summary>Images of the template whose bytes could not be read (file missing or changed): "Usuń brakujące obrazy".</summary>
    public bool HasMissingImages => !IsLoadingImages && (_working.Images ?? []).Any(i => !_images.ContainsKey(i.ContentId));

    /// <summary>
    /// "2 image(s) of the template could not be read (file missing or changed) – remove them or paste them again." while
    /// <see cref="HasMissingImages"/>, else null: a note of the image strip - true for as long as the template has them.
    /// </summary>
    public string? MissingImagesText => HasMissingImages
        ? Localizer.Format(L.UserMessagesRich.ImagesMissing, (_working.Images ?? []).Count(i => !_images.ContainsKey(i.ContentId)))
        : null;

    /// <summary>
    /// The images of the template just loaded are being read from the image store: the "busy" mark of the image strip
    /// (there is nothing to say in the status line - the result is the strip, or the note about missing images).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingImages), nameof(MissingImagesText), nameof(ImagesLoadingText), nameof(ShowTemplateImages))]
    public partial bool IsLoadingImages { get; private set; }

    /// <summary>"Loading the template's images…" while <see cref="IsLoadingImages"/> (what a screen reader finds as the status of the strip), else empty.</summary>
    public string ImagesLoadingText => IsLoadingImages ? Localizer[L.UserMessagesRich.ImagesLoading] : string.Empty;

    /// <summary>The image strip has something to show: images, the "busy" mark, or the note about images that are missing.</summary>
    public bool ShowTemplateImages => HasTemplateImages || IsLoadingImages || HasMissingImages;

    /// <summary>The results of the last paste, grouped, with their actions.</summary>
    public ObservableCollection<HtmlPasteProblemViewModel> PasteProblems { get; } = [];

    public bool HasPasteProblems => PasteProblems.Count > 0;

    /// <summary>Status line of the HTML section (pasting, pasted, loading images, a refused image …).</summary>
    [ObservableProperty]
    public partial string? RichStatusText { get; private set; }

    /// <summary>A paste runs in the background (cancellable).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PasteFormattedCommand), nameof(PasteImageCommand), nameof(CancelPasteCommand))]
    public partial bool IsPasting { get; private set; }

    /// <summary>"Podgląd formatowania" instead of the HTML source editor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposePreview))]
    public partial bool ShowHtmlPreview { get; set; }

    /// <summary>The rendered preview is allowed by the settings (<c>UserMessages.RenderedPreview</c>).</summary>
    public bool RenderedPreviewEnabled => Services.Settings.Current.UserMessages.RenderedPreview;

    /// <summary>The caret of the HTML source editor (the view keeps it current); "Wklej obraz" inserts there.</summary>
    public int HtmlCaretIndex { get; set; } = -1;

    /// <summary>The edited language's HTML as written (fields not filled in) with the template's images.</summary>
    public HtmlPreviewContent ComposePreview => new(Html, new Dictionary<string, MessageImage>(_images, StringComparer.OrdinalIgnoreCase));

    // ---- Transport bar -------------------------------------------------------------------------------------------

    /// <summary>Why the messages do not fit the chosen transport (the bar above the send button), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransportFitVisible))]
    public partial string? TransportFitText { get; private set; }

    public bool IsTransportFitVisible => TransportFitText is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendDirectForRunCommand))]
    public partial bool CanSendDirectForRun { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendWithoutImagesCommand))]
    public partial bool CanSendWithoutImages { get; private set; }

    /// <summary>The run goes without images (re-planned with <c>OmitImages</c>).</summary>
    public bool IsWithoutImages => _omitImages;

    /// <summary>"bez obrazów" / "bezpośrednio przez SMTP" for the report header and the session log, or null.</summary>
    public string? RunNote => string.Join(", ", new[]
    {
        _omitImages ? Localizer[L.UserMessagesRich.RunWithoutImages] : null,
        _directForRun ? Localizer[L.UserMessagesRich.RunDirect] : null,
    }.OfType<string>()) is { Length: > 0 } note ? note : null;

    // ---- Commands ------------------------------------------------------------------------------------------------

    /// <summary>"Wklej sformatowaną treść" (Ctrl+Shift+V): replaces the HTML of the edited language (asks first when it is
    /// not empty); an empty text body is filled from the HTML. The paste is the operation of the editor: the status line
    /// says "Pasting the formatted content…" with Stop - the button that started it is its Cancel as well - and what
    /// became of it.</summary>
    [RelayCommand(CanExecute = nameof(CanEditHtml))]
    private async Task PasteFormattedAsync()
    {
        var content = Paste.ReadClipboard();
        if (content is null || content.IsEmpty)
        {
            NotPasted(L.UserMessagesRich.ClipboardEmpty, Localizer[L.UserMessagesRich.ClipboardEmpty]);
            return;
        }

        if (!string.IsNullOrWhiteSpace(Html)
            && !await Services.Dialogs.ConfirmAsync(Localizer[L.UserMessagesRich.PasteFormatted], Localizer[L.UserMessagesRich.ReplaceHtmlConfirm], destructive: true).ConfigureAwait(true))
        {
            return;
        }

        using var pasting = EditorStatus.Begin(Localizer[L.UserMessagesRich.Pasting], new PageStatusLog(L.UserMessagesRich.Pasting));
        _pasting = pasting;
        IsPasting = true;
        RichStatusText = Localizer[L.UserMessagesRich.Pasting];
        try
        {
            var result = await Paste.ImportAsync(content, PasteDomains(), OtherLanguageImages(), pasting.Token).ConfigureAwait(true);
            if (pasting.IsCancelling)
            {
                // Stop was pressed and the import finished all the same: the operator said stop, so nothing is pasted.
                PasteStopped(pasting);
            }
            else if (!pasting.IsEnded && !IsDisposed)
            {
                ApplyPaste(result, pasting);
            }
        }
        catch (OperationCanceledException) when (pasting.Token.IsCancellationRequested)
        {
            // Only a paste the operator stopped says so (the window that closes ends it without a word).
            if (pasting.IsCancelling)
            {
                PasteStopped(pasting);
            }
        }
        finally
        {
            if (ReferenceEquals(_pasting, pasting))
            {
                _pasting = null;
                IsPasting = false;
            }
        }
    }

    /// <summary>The Cancel of the paste as a command (the same Stop as in the status line and on the paste button).</summary>
    [RelayCommand(CanExecute = nameof(IsPasting))]
    private void CancelPaste() => _pasting?.Cancel();

    /// <summary>"Wklej obraz": the clipboard's bitmap at the caret of the HTML source (else as a paragraph at the end).</summary>
    [RelayCommand(CanExecute = nameof(CanEditHtml))]
    private void PasteImage()
    {
        var (image, error) = HtmlPasteWorkflow.ImageOf(Paste.ReadClipboard(), _images.Count + 1);
        if (image is null)
        {
            if (error is { } refused)
            {
                NotPasted(L.UserMessagesRich.ImageNotAccepted, Localizer.Format(L.UserMessagesRich.ImageNotAccepted, UserMessageText.Reason(Localizer, refused.ToString())));
            }
            else
            {
                NotPasted(L.UserMessagesRich.ClipboardNoImage, Localizer[L.UserMessagesRich.ClipboardNoImage]);
            }

            return;
        }

        if (!_images.ContainsKey(image.ContentId) && !FitsTemplateLimits([image]))
        {
            NotPasted(L.UserMessagesRich.TemplateImageLimit, Localizer[L.UserMessagesRich.TemplateImageLimit]);
            return;
        }

        _images[image.ContentId] = image;
        var tag = HtmlPasteWorkflow.ImageHtml(image);
        var html = Html;
        var updated = HtmlCaretIndex >= 0 && HtmlCaretIndex <= html.Length
            ? html.Insert(HtmlCaretIndex, tag)
            : html + (html.Length == 0 || html.EndsWith('\n') ? string.Empty : "\n") + "<p>" + tag + "</p>\n";
        SetWorkingImages(updated);
        Html = updated;
        EditorDone(L.UserMessagesRich.ImageInserted, Localizer.Format(L.UserMessagesRich.ImageInserted, image.FileName));
        RefreshImages();
    }

    private bool CanEditHtml() => Stage == UserMessageStage.Compose && !IsPasting;

    /// <summary>"Wygeneruj tekst z HTML": fills the text body (asks when it is not empty).</summary>
    [RelayCommand]
    private async Task GenerateTextAsync()
    {
        if (string.IsNullOrWhiteSpace(Html))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Body)
            && !await Services.Dialogs.ConfirmAsync(Localizer[L.UserMessagesRich.GenerateText], Localizer[L.UserMessagesRich.GenerateTextConfirm], destructive: true).ConfigureAwait(true))
        {
            return;
        }

        Body = HtmlToText.Convert(Html);
    }

    [RelayCommand]
    private void ToggleHtmlPreview() => ShowHtmlPreview = !ShowHtmlPreview;

    /// <summary>"Usuń" in the image strip: the image and its &lt;img&gt; elements in both languages.</summary>
    [RelayCommand]
    private void RemoveImage(MessageImageItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        RemoveImages([item.ContentId]);
    }

    /// <summary>Removes the template's images whose bytes could not be read (and their &lt;img&gt; elements).</summary>
    [RelayCommand]
    private void RemoveMissingImages() =>
        RemoveImages([.. (_working.Images ?? []).Select(i => i.ContentId).Where(id => !_images.ContainsKey(id))]);

    /// <summary>"Zezwól na domenę …": adds the domain to the allowed link domains of the settings.</summary>
    [RelayCommand]
    private async Task AllowLinkDomainAsync(HtmlPasteDomainChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        await Services.Settings.UpdateAsync(s =>
        {
            s.UserMessages.AllowedLinkDomains.Add(choice.Domain);
            s.UserMessages.Normalize();
        }).ConfigureAwait(true);
        var allowed = Services.Settings.Current.UserMessages.AllowedLinkDomains;
        foreach (var problem in PasteProblems.Where(p => p.Kind == HtmlImportProblemKind.LinkOutsideAllowedDomains
                                                         && p.Detail is { } host && UserMessageRules.IsLinkAllowed("https://" + host + "/", allowed)).ToList())
        {
            PasteProblems.Remove(problem);
        }

        OnPropertyChanged(nameof(HasPasteProblems));
        EditorDone(L.UserMessagesRich.DomainAllowed, Localizer.Format(L.UserMessagesRich.DomainAllowed, choice.Domain));
        RefreshTemplateCheck();
    }

    /// <summary>"Usuń łącze": unwraps the links to the problem's host in the HTML of the edited language.</summary>
    [RelayCommand]
    private void RemoveLink(HtmlPasteProblemViewModel? problem)
    {
        if (problem is not { CanRemoveLink: true, Detail: { } host } || string.IsNullOrWhiteSpace(Html))
        {
            return;
        }

        Html = MessageHtmlChecks.RemoveLinks(Html, host);
        foreach (var related in PasteProblems.Where(p => p.CanRemoveLink && string.Equals(p.Detail, host, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            PasteProblems.Remove(related);
        }

        OnPropertyChanged(nameof(HasPasteProblems));
        EditorDone(L.UserMessagesRich.LinkRemoved, Localizer.Format(L.UserMessagesRich.LinkRemoved, host));
    }

    /// <summary>"Wyślij bezpośrednio przez SMTP" (for this run only – the setting stays): the direct transport, with the
    /// password from the window's box when the server needs one, then the blocked action again.</summary>
    [RelayCommand(CanExecute = nameof(CanSendDirectForRun))]
    private async Task SendDirectForRunAsync()
    {
        var password = SmtpPasswordInput?.GetPassword();
        var choice = _messages.Transports.Smtp(Services.Settings.Current.UserMessages.Smtp, string.IsNullOrEmpty(password) ? null : password);
        if (choice.Transport is null)
        {
            TransportFitText = Localizer[UserMessageText.ProblemKey(choice.Problem ?? UserMessageTransportChoiceResult.SmtpNotConfigured)];
            CanSendDirectForRun = false;
            return;
        }

        if (choice.NeedsPassword)
        {
            NeedsSmtpPassword = true;
            NotStarted(
                _fitIsTest ? L.MessageWindowStatus.TestNotSent : L.MessageWindowStatus.NotSent,
                L.UserMessages.PasswordRequired,
                Localizer[L.UserMessages.PasswordRequired]);
            return;
        }

        SmtpPasswordInput?.Clear();
        _transport = choice.Transport;
        NeedsSmtpPassword = false;
        _directForRun = true;
        OnPropertyChanged(nameof(RunNote));
        TransportText = Localizer.Format(L.UserMessagesRich.ViaSmtpForRun, choice.Transport.Description);
        var test = _fitIsTest;
        ClearTransportFit();
        if (test)
        {
            await SendTestCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
        else
        {
            await SendCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    /// <summary>"Wyślij bez obrazów": plans the same accounts again with every image replaced by its description; the
    /// preview then shows exactly what will be sent.</summary>
    [RelayCommand(CanExecute = nameof(CanSendWithoutImages))]
    private async Task SendWithoutImagesAsync()
    {
        _omitImages = true;
        OnPropertyChanged(nameof(IsWithoutImages));
        OnPropertyChanged(nameof(RunNote));
        ClearTransportFit();
        await PlanCoreAsync([.. _items.Select(i => i.Planned.DistinguishedName)]).ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissTransportFit() => ClearTransportFit();

    // ---- Hooks of the main file ----------------------------------------------------------------------------------

    /// <summary>A template was loaded into the editor: its images are read from the store (off the UI thread).</summary>
    private void OnTemplateLoaded(MessageTemplate template)
    {
        if (!_richHooked)
        {
            _richHooked = true;
            PropertyChanged += OnRichPropertyChanged;
        }

        var version = ++_imageVersion;
        _images.Clear();
        PasteProblems.Clear();
        OnPropertyChanged(nameof(HasPasteProblems));
        _omitImages = false;
        RichStatusText = null;

        // What the editor said was about the template shown before (a paste that still runs keeps the line).
        ClearEditorResult();
        if ((template.Images ?? []).Count == 0)
        {
            IsLoadingImages = false;
            _imagesLoading = Task.CompletedTask;
            RefreshImages();
            return;
        }

        RichStatusText = Localizer[L.UserMessagesRich.ImagesLoading];
        IsLoadingImages = true;
        _imagesLoading = LoadTemplateImagesAsync(template, version);
    }

    /// <summary>The images of the plan (waits for a template's images still being read). A new plan ends a "direct SMTP for
    /// this run" choice.</summary>
    private async Task<IReadOnlyDictionary<string, MessageImage>> PlanImagesAsync()
    {
        _directForRun = false;
        ClearTransportFit();
        await _imagesLoading.ConfigureAwait(true);
        return new Dictionary<string, MessageImage>(_images, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Before a send and before the test message: whether the messages fit the transport (design note section 9.6). When not,
    /// the transport bar shows why and the choices that help, and nothing is sent.
    /// </summary>
    private async Task<bool> FitsTransportAsync(IUserMessageTransport transport, IEnumerable<PlannedUserMessage> planned, bool test)
    {
        ClearTransportFit();
        UserMessageTransportCapabilities capabilities;
        if (transport.Kind == UserMessageTransportKind.DirectSmtp)
        {
            capabilities = UserMessageTransportCapabilities.Direct;
        }
        else if (transport.Kind == UserMessageTransportKind.Outlook)
        {
            capabilities = UserMessageTransportCapabilities.Outlook;   // U5: the Korespondencja image limits
        }
        else
        {
            UserMessageTransportStatus status;
            try
            {
                status = await transport.GetStatusAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
            {
                return true;    // the send itself reports the transport's failure
            }

            if (!status.IsAvailable)
            {
                return true;
            }

            capabilities = status.Capabilities;
        }

        var fit = TransportFit.Check(planned, capabilities);
        if (fit.IsFit)
        {
            return true;
        }

        _fitIsTest = test;
        var settings = Services.Settings.Current.UserMessages;
        CanSendDirectForRun = transport.Kind == UserMessageTransportKind.Watchdog && _messages.Transports.Smtp(settings.Smtp).Transport is not null;
        CanSendWithoutImages = fit.OffersWithoutImages;
        TransportFitText = Localizer.Format(fit.Reason switch
        {
            TransportFitReason.ImagesNotSupported => L.UserMessagesRich.FitImagesNotSupported,
            TransportFitReason.ImagesOverLimits => L.UserMessagesRich.FitImagesOverLimits,
            TransportFitReason.ImageStorage => L.UserMessagesRich.FitImageStorage,
            _ => L.UserMessagesRich.FitHtmlTooLong,
        }, fit.Messages);
        return false;
    }

    /// <summary>The template name of the session log with the run's note ("Hasło wygasa (bez obrazów)").</summary>
    private string WithRunNote(string templateName) => RunNote is { } note ? $"{templateName} ({note})" : templateName;

    /// <summary>Imports a templates file; refused images are reported. Returns the number of templates added.</summary>
    private async Task<int> ImportTemplatesWithImagesAsync(string json)
    {
        var result = await _messages.Templates.ImportTemplatesAsync(json).ConfigureAwait(true);
        if (result.ImagesRejected > 0)
        {
            Services.Notifications.Show(NotificationSeverity.Warning, Localizer.Format(L.UserMessagesRich.ImportImagesRejected, result.ImagesRejected));
        }

        return result.Added;
    }

    partial void OnStageChanged(UserMessageStage value)
    {
        if (value == UserMessageStage.Compose)
        {
            _omitImages = false;
            OnPropertyChanged(nameof(IsWithoutImages));
            OnPropertyChanged(nameof(RunNote));
            ClearTransportFit();
        }

        PasteFormattedCommand.NotifyCanExecuteChanged();
        PasteImageCommand.NotifyCanExecuteChanged();
        OnStageShown();
    }

    // ---- What the editor says ------------------------------------------------------------------------------------

    /// <summary>
    /// A result of the editor - an image that was inserted, a link that was removed, why nothing was pasted - unless a
    /// paste runs: that operation keeps the line and says itself how it ended.
    /// </summary>
    private void SayInEditor(PageStatus status)
    {
        if (EditorStatus.Operation is null)
        {
            EditorStatus.Show(status);
        }
    }

    /// <summary>What the editor just did (a sentence), with its resource key as the log copy.</summary>
    private void EditorDone(string key, string text)
    {
        RichStatusText = text;
        SayInEditor(PageStatus.Done(text, new PageStatusLog(key)));
    }

    /// <summary>"Nothing was pasted: The clipboard holds no image." - an attention the operator can close; the log copy is the key of the reason.</summary>
    private void NotPasted(string reasonKey, string reason)
    {
        RichStatusText = reason;
        SayInEditor(NotPastedStatus(reasonKey, reason));
    }

    private PageStatus NotPastedStatus(string reasonKey, string reason) =>
        PageStatus.Attention(PageStatusText.Failure(Localizer[L.MessageWindowStatus.NotPasted], reason), new PageStatusLog(reasonKey));

    /// <summary>"Pasting was stopped: the content was not changed."</summary>
    private void PasteStopped(OperationScope pasting)
    {
        RichStatusText = Localizer[L.UserMessagesRich.PasteCancelled];
        pasting.CompleteAttention(Localizer[L.MessageWindowStatus.PasteStopped], new PageStatusLog(L.MessageWindowStatus.PasteStopped));
    }

    /// <summary>Takes a result of the editor off the line (another template, images removed); a running paste stays.</summary>
    private void ClearEditorResult()
    {
        if (EditorStatus.Operation is null)
        {
            EditorStatus.Clear();
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private HtmlPasteWorkflow Paste => _pasteWorkflow ??= new HtmlPasteWorkflow(Services.Clipboard, _messages.PastedImages, _messages.Planner.Fields.Engine);

    /// <summary>
    /// Takes the result of a paste into the editor and ends the operation with what became of it: nothing could be
    /// pasted and why (attention), the text or the formatted content was pasted (done) - with the remarks that are
    /// listed under the content as an attention when one of them is a warning.
    /// </summary>
    private void ApplyPaste(HtmlPasteResult result, OperationScope pasting)
    {
        switch (result.Kind)
        {
            case HtmlPasteKind.Nothing:
                RichStatusText = Localizer[L.UserMessagesRich.ClipboardEmpty];
                pasting.Complete(NotPastedStatus(L.UserMessagesRich.ClipboardEmpty, RichStatusText));
                return;
            case HtmlPasteKind.TooLarge:
                RichStatusText = Localizer[L.UserMessagesRich.ClipboardTooLarge];
                pasting.Complete(NotPastedStatus(L.UserMessagesRich.ClipboardTooLarge, RichStatusText));
                return;
        }

        PasteProblems.Clear();
        foreach (var problem in HtmlPasteProblemViewModel.Group(result.Problems, Localizer))
        {
            PasteProblems.Add(problem);
        }

        OnPropertyChanged(nameof(HasPasteProblems));
        if (result.Kind == HtmlPasteKind.Image && result.Images.Count == 0)
        {
            RichStatusText = Localizer.Format(L.UserMessagesRich.ImageNotAccepted, UserMessageText.Reason(Localizer, result.Problems.FirstOrDefault()?.Detail));
            pasting.Complete(NotPastedStatus(L.UserMessagesRich.ImageNotAccepted, RichStatusText));
            return;
        }

        foreach (var image in result.Images)
        {
            _images[image.ContentId] = image;
        }

        SetWorkingImages(result.Html);
        Html = result.Html;
        if (string.IsNullOrWhiteSpace(Body) && result.SuggestedText.Length > 0)
        {
            Body = result.SuggestedText;
        }

        if (result.Kind == HtmlPasteKind.Text)
        {
            RichStatusText = Localizer[L.UserMessagesRich.PastedText];
            pasting.CompleteDone(RichStatusText, new PageStatusLog(L.UserMessagesRich.PastedText));
        }
        else
        {
            // "Pasted: 2 image(s), 3 note(s) below." - the notes are the list under the content; one that is a warning
            // makes the result an attention.
            RichStatusText = Localizer.Format(L.UserMessagesRich.Pasted, result.Images.Count, PasteProblems.Count);
            var log = new PageStatusLog(L.UserMessagesRich.Pasted, result.Images.Count, PasteProblems.Count);
            if (PasteProblems.Any(p => p.IsWarning))
            {
                pasting.CompleteAttention(RichStatusText, log);
            }
            else
            {
                pasting.CompleteDone(RichStatusText, log);
            }
        }

        RefreshImages();
    }

    /// <summary>The working template's image metadata: the images in memory referenced by <paramref name="editedHtml"/> or
    /// by the other language (images referenced by neither are dropped).</summary>
    private void SetWorkingImages(string? editedHtml)
    {
        var other = EditLanguage.Value == MessageLanguage.English ? _working.Polish : _working.English;
        var referenced = MessageHtmlChecks.ReferencedImageIds(editedHtml).Concat(MessageHtmlChecks.ReferencedImageIds(other.Html))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = (_working.Images ?? []).Where(i => referenced.Contains(i.ContentId) && !_images.ContainsKey(i.ContentId));
        _working = _working with
        {
            Images = [.. _images.Values.Where(i => referenced.Contains(i.ContentId)).Select(MessageTemplateImage.From).Concat(known)
                .DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase)],
        };
    }

    private void RemoveImages(IReadOnlyCollection<string> contentIds)
    {
        if (contentIds.Count == 0)
        {
            return;
        }

        string? Without(string? html) => html is null ? null : contentIds.Aggregate(html, MessageHtmlChecks.RemoveImage);
        foreach (var id in contentIds)
        {
            _images.Remove(id);
        }

        _working = _working with
        {
            Polish = _working.Polish with { Html = Without(_working.Polish.Html) },
            English = _working.English with { Html = Without(_working.English.Html) },
            Images = [.. (_working.Images ?? []).Where(i => !contentIds.Contains(i.ContentId, StringComparer.OrdinalIgnoreCase))],
        };
        LoadTexts();
        RefreshTemplateCheck();
        RefreshImages();
        RichStatusText = null;
        ClearEditorResult();
    }

    private bool FitsTemplateLimits(IReadOnlyCollection<MessageImage> added)
    {
        var all = _images.Values.Concat(added).DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase).ToList();
        return all.Count <= MessageImageLimits.MaxImagesPerTemplate && all.Sum(i => (long)i.Data.Length) <= MessageImageLimits.MaxImagesBytesPerTemplate;
    }

    /// <summary>Images of the other language (counted against the template's limits by the import).</summary>
    private IReadOnlyList<MessageImage> OtherLanguageImages()
    {
        var other = EditLanguage.Value == MessageLanguage.English ? _working.Polish : _working.English;
        return [.. MessageHtmlChecks.ReferencedImageIds(other.Html).Select(id => _images.GetValueOrDefault(id)).OfType<MessageImage>()];
    }

    /// <summary>Allowed link domains at paste: the settings, the AD DNS domain and the sender's domain.</summary>
    private IReadOnlyList<string> PasteDomains()
    {
        var settings = Services.Settings.Current.UserMessages;
        var domain = Session?.Info.DomainDnsName;
        var domains = new List<string>(settings.AllowedLinkDomains);
        if (!string.IsNullOrWhiteSpace(domain))
        {
            domains.Add(domain);
        }

        var from = UserMessageSenders.From(settings, domain).From.Address;
        if (from.LastIndexOf('@') is var at and >= 0 && at < from.Length - 1)
        {
            domains.Add(from[(at + 1)..]);
        }

        return [.. domains.Select(UserMessageRules.NormalizeDomain).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    private async Task LoadTemplateImagesAsync(MessageTemplate template, int version)
    {
        IReadOnlyDictionary<string, MessageImage> loaded;
        try
        {
            loaded = await _messages.Templates.LoadImagesAsync(template).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            loaded = new Dictionary<string, MessageImage>();
        }

        if (version != _imageVersion || IsDisposed)
        {
            return;
        }

        foreach (var image in loaded.Values)
        {
            _images[image.ContentId] = image;
        }

        IsLoadingImages = false;
        var missing = (template.Images ?? []).Count(i => !_images.ContainsKey(i.ContentId));
        RichStatusText = missing > 0 ? Localizer.Format(L.UserMessagesRich.ImagesMissing, missing) : null;
        RefreshImages();
    }

    private void RefreshImages()
    {
        TemplateImages.Clear();
        foreach (var metadata in _working.Images ?? [])
        {
            if (_images.TryGetValue(metadata.ContentId, out var image))
            {
                TemplateImages.Add(new MessageImageItemViewModel(image, Localizer));
            }
        }

        OnPropertyChanged(nameof(HasTemplateImages));
        OnPropertyChanged(nameof(HasMissingImages));
        OnPropertyChanged(nameof(MissingImagesText));
        OnPropertyChanged(nameof(ShowTemplateImages));
        OnPropertyChanged(nameof(ComposePreview));
    }

    /// <summary>After a template with images was saved: warns when the image files of all templates grow above 64 MB (the
    /// folder is measured off the UI thread).</summary>
    private async Task WarnWhenImageFolderIsLargeAsync()
    {
        if ((_working.Images ?? []).Count > 0 && await Task.Run(() => _messages.Templates.ImageFolderIsLarge).ConfigureAwait(true))
        {
            Services.Notifications.Show(NotificationSeverity.Warning, Localizer[L.UserMessagesRich.ImageFolderLarge]);
        }
    }

    private void ClearTransportFit()
    {
        TransportFitText = null;
        CanSendDirectForRun = false;
        CanSendWithoutImages = false;
    }

    private void OnRichPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Html) or nameof(EditLanguage))
        {
            OnPropertyChanged(nameof(ComposePreview));
        }
    }
}
