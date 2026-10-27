using System.Collections.ObjectModel;
using System.ComponentModel;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
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
public sealed partial class UserMessageComposerViewModel
{
    private readonly Dictionary<string, MessageImage> _images = new(StringComparer.OrdinalIgnoreCase);
    private HtmlPasteWorkflow? _pasteWorkflow;
    private CancellationTokenSource? _pasteCancellation;
    private Task _imagesLoading = Task.CompletedTask;
    private int _imageVersion;
    private bool _richHooked;
    private bool _omitImages;
    private bool _directForRun;
    private bool _fitIsTest;
    private bool _loadingImages;

    // ---- HTML section --------------------------------------------------------------------------------------------

    /// <summary>The images of the edited template (both languages) whose bytes are in memory.</summary>
    public ObservableCollection<MessageImageItemViewModel> TemplateImages { get; } = [];

    public bool HasTemplateImages => TemplateImages.Count > 0;

    /// <summary>Images of the template whose bytes could not be read (file missing or changed): "Usuń brakujące obrazy".</summary>
    public bool HasMissingImages => !_loadingImages && (_working.Images ?? []).Any(i => !_images.ContainsKey(i.ContentId));

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
    /// not empty); an empty text body is filled from the HTML.</summary>
    [RelayCommand(CanExecute = nameof(CanEditHtml))]
    private async Task PasteFormattedAsync()
    {
        var content = Paste.ReadClipboard();
        if (content is null || content.IsEmpty)
        {
            RichStatusText = Localizer[L.UserMessagesRich.ClipboardEmpty];
            return;
        }

        if (!string.IsNullOrWhiteSpace(Html)
            && !await Services.Dialogs.ConfirmAsync(Localizer[L.UserMessagesRich.PasteFormatted], Localizer[L.UserMessagesRich.ReplaceHtmlConfirm], destructive: true).ConfigureAwait(true))
        {
            return;
        }

        _pasteCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _pasteCancellation = cancellation;
        IsPasting = true;
        RichStatusText = Localizer[L.UserMessagesRich.Pasting];
        try
        {
            var result = await Paste.ImportAsync(content, PasteDomains(), OtherLanguageImages(), cancellation.Token).ConfigureAwait(true);
            if (!cancellation.IsCancellationRequested && !IsDisposed)
            {
                ApplyPaste(result);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RichStatusText = Localizer[L.UserMessagesRich.PasteCancelled];
        }
        finally
        {
            if (ReferenceEquals(_pasteCancellation, cancellation))
            {
                _pasteCancellation = null;
                IsPasting = false;
            }

            cancellation.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(IsPasting))]
    private void CancelPaste() => _pasteCancellation?.Cancel();

    /// <summary>"Wklej obraz": the clipboard's bitmap at the caret of the HTML source (else as a paragraph at the end).</summary>
    [RelayCommand(CanExecute = nameof(CanEditHtml))]
    private void PasteImage()
    {
        var (image, error) = HtmlPasteWorkflow.ImageOf(Paste.ReadClipboard(), _images.Count + 1);
        if (image is null)
        {
            RichStatusText = error is { } refused
                ? Localizer.Format(L.UserMessagesRich.ImageNotAccepted, UserMessageText.Reason(Localizer, refused.ToString()))
                : Localizer[L.UserMessagesRich.ClipboardNoImage];
            return;
        }

        if (!_images.ContainsKey(image.ContentId) && !FitsTemplateLimits([image]))
        {
            RichStatusText = Localizer[L.UserMessagesRich.TemplateImageLimit];
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
        RichStatusText = Localizer.Format(L.UserMessagesRich.ImageInserted, image.FileName);
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
        RichStatusText = Localizer.Format(L.UserMessagesRich.DomainAllowed, choice.Domain);
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
        RichStatusText = Localizer.Format(L.UserMessagesRich.LinkRemoved, host);
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
            Error = Localizer[L.UserMessages.PasswordRequired];
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
        if ((template.Images ?? []).Count == 0)
        {
            _loadingImages = false;
            _imagesLoading = Task.CompletedTask;
            RefreshImages();
            return;
        }

        RichStatusText = Localizer[L.UserMessagesRich.ImagesLoading];
        _loadingImages = true;
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
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private HtmlPasteWorkflow Paste => _pasteWorkflow ??= new HtmlPasteWorkflow(Services.Clipboard, _messages.PastedImages, _messages.Planner.Fields.Engine);

    private void ApplyPaste(HtmlPasteResult result)
    {
        switch (result.Kind)
        {
            case HtmlPasteKind.Nothing:
                RichStatusText = Localizer[L.UserMessagesRich.ClipboardEmpty];
                return;
            case HtmlPasteKind.TooLarge:
                RichStatusText = Localizer[L.UserMessagesRich.ClipboardTooLarge];
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

        RichStatusText = result.Kind == HtmlPasteKind.Text
            ? Localizer[L.UserMessagesRich.PastedText]
            : Localizer.Format(L.UserMessagesRich.Pasted, result.Images.Count, PasteProblems.Count);
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

        _loadingImages = false;
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
