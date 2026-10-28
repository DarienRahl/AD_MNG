using System.Security.Cryptography;
using AdMng.Core.Directory;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>The tabs of the Korespondencja window.</summary>
public enum CorrespondenceTab
{
    Mailboxes,
    Recipients,
    Message,
    Preview,
    Send,
    Responses,
}

/// <summary>
/// The Korespondencja window (U5, design note docs/dev/u5-outlook-correspondence.md 6.4): MailerWPiA's workflow per
/// connection window – recipients, message, preview, sending through Outlook, responses – and the mailbox owner query.
/// The pages are view models of their own; the XAML binds through the page properties. Directory reads use the window's
/// session (rule 4); after it ended the AD actions are disabled while Outlook sending keeps working. The window keeps
/// recipients and directory values in memory only (rule 1): on close it saves the options, the field definitions and the
/// last draft (operator content) to the settings.
/// </summary>
public sealed partial class CorrespondenceViewModel : WindowViewModelBase
{
    public CorrespondenceViewModel(DialogServices services, CorrespondenceServices correspondence, CorrespondenceRequest request)
        : base(services)
    {
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Outlook = new OutlookConnectionViewModel(services, correspondence);
        Recipients = new RecipientsPageViewModel(services, correspondence, RequireSession);
        Message = new MessagePageViewModel(services, correspondence, Recipients);
        Mailboxes = new MailboxOwnersPageViewModel(services, correspondence, RequireSession, Recipients, Message);
        Preview = new PreviewPageViewModel(services, correspondence, Recipients, Message, Outlook);
        Send = new SendPageViewModel(services, correspondence, Recipients, Message, Preview, Outlook);
        Responses = new ResponsesPageViewModel(services, correspondence, Outlook);
        IsMailboxesTabVisible = request.Start == CorrespondenceStart.MailboxOwners;
        SelectedTab = IsMailboxesTabVisible ? CorrespondenceTab.Mailboxes : CorrespondenceTab.Recipients;
        Mailboxes.RecipientsAdded += OnMailboxRecipientsAdded;
    }

    /// <summary>"Korespondencja – {domain}" ("Korespondencja" for a window opened while disconnected).</summary>
    public override string WindowTitle => string.IsNullOrEmpty(ConfirmationDomain)
        ? Localizer[L.Correspondence.WindowTitleNoDomain]
        : Localizer.Format(L.Correspondence.WindowTitle, ConfirmationDomain);

    /// <summary>What the window was opened with.</summary>
    public CorrespondenceRequest Request { get; }

    [ObservableProperty]
    public partial CorrespondenceTab SelectedTab { get; set; }

    /// <summary>The "Skrzynki" tab is shown (started with MailboxOwners, or after <see cref="ShowMailboxesCommand"/>).</summary>
    [ObservableProperty]
    public partial bool IsMailboxesTabVisible { get; private set; }

    /// <summary>A line for the status bar (e.g. the journal was reset), or empty.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>A problem of the window itself (the journal cannot be used, the request could not be read), or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The window is loading (settings, journal, the request's objects).</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public OutlookConnectionViewModel Outlook { get; }

    public MailboxOwnersPageViewModel Mailboxes { get; }

    public RecipientsPageViewModel Recipients { get; }

    public MessagePageViewModel Message { get; }

    public PreviewPageViewModel Preview { get; }

    public SendPageViewModel Send { get; }

    public ResponsesPageViewModel Responses { get; }

    /// <summary>The initialization (tests await it).</summary>
    internal Task? Initialization { get; private set; }

    private CorrespondenceServices Correspondence { get; }

    /// <summary>
    /// Loads the settings into the pages, the journal (pruned to the retention of the settings), the last draft (with its
    /// images) and the signatures; applies the request (DNs → recipients from AD, or the mailbox scope = the selection and
    /// the "Skrzynki" tab); connects Outlook in the background (status only, no modal error). Never throws: problems are
    /// shown in <see cref="Error"/> and <see cref="StatusText"/>.
    /// </summary>
    public override Task InitializeAsync() => Initialization ??= InitializeCoreAsync();

    /// <summary>
    /// While sending asks whether to stop (the run is cancelled when the operator agrees); then saves the options, the
    /// field definitions and the last draft (with its images) to the settings. A failing save never keeps the window open.
    /// </summary>
    public override async Task<bool> CanCloseAsync()
    {
        if (Send.IsSending)
        {
            if (!await Services.Dialogs.ConfirmAsync(WindowTitle, Localizer[L.Correspondence.StopOnClose], destructive: true).ConfigureAwait(true))
            {
                return false;
            }

            if (Send.StopCommand.CanExecute(null))
            {
                Send.StopCommand.Execute(null);
            }
        }

        await SaveStateAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Writes the options of the pages and the account into the settings, then the last draft (subject, body, the images it
    /// references, custom fields, list item template, salutation, voting, grouping) with its image bytes.
    /// </summary>
    internal async Task SaveStateAsync()
    {
        try
        {
            await Services.Settings.UpdateAsync(settings =>
            {
                var options = settings.Correspondence;
                Outlook.SaveTo(options);
                Recipients.SaveTo(options);
                Message.SaveTo(options);
                Preview.SaveTo(options);
                Mailboxes.SaveTo(options);
            }).ConfigureAwait(true);
            await Correspondence.Templates.SaveLastDraftAsync(CurrentDraft(), Message.BodyImages.Values).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The settings file cannot be written right now (the settings store reports it); closing goes on.
        }
    }

    /// <summary>The last draft as the message page holds it.</summary>
    internal CorrespondenceTemplate CurrentDraft()
    {
        var draft = CorrespondenceTemplateLibrary.FromDraft(Message.Draft());
        draft.VotingEnabled = Message.UseVoting;
        draft.VotingOptions = string.IsNullOrWhiteSpace(Message.VotingOptions) ? null : Message.VotingOptions.Trim();
        draft.GroupByAddress = Message.GroupByAddress;
        return draft;
    }

    /// <summary>The window's language changed: the Outlook state follows.</summary>
    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        Outlook.RefreshTexts();
    }

    /// <summary>The AD actions are disabled and a running mailbox owner query stops; Outlook sending keeps working.</summary>
    protected override void OnSessionEnded()
    {
        Recipients.CanUseDirectory = false;
        if (Mailboxes.IsRunning && Mailboxes.CancelCommand.CanExecute(null))
        {
            Mailboxes.CancelCommand.Execute(null);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Mailboxes.RecipientsAdded -= OnMailboxRecipientsAdded;
            if (Send.IsSending && Send.StopCommand.CanExecute(null))
            {
                Send.StopCommand.Execute(null);
            }

            _ = Outlook.DisposeAsync().AsTask();
        }

        base.Dispose(disposing);
    }

    private async Task InitializeCoreAsync()
    {
        IsBusy = true;
        try
        {
            var settings = Services.Settings.Current.Correspondence;
            Outlook.LoadFrom(settings);
            Recipients.LoadFrom(settings);
            Message.LoadFrom(settings);
            Preview.LoadFrom(settings);
            Mailboxes.LoadFrom(settings);
            Outlook.ConnectInBackground();
            await LoadJournalAsync(settings.JournalRetentionDays).ConfigureAwait(true);
            await LoadLastDraftAsync().ConfigureAwait(true);
            await RefreshSignaturesAsync().ConfigureAwait(true);
            await ApplyRequestAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The journal (application-wide, read again for every window: other AD Manager processes may have appended).</summary>
    private async Task LoadJournalAsync(int retentionDays)
    {
        try
        {
            var retention = TimeSpan.FromDays(Math.Clamp(retentionDays, 1, CorrespondenceSettings.MaxJournalRetentionDays));
            var load = await Correspondence.Journal.LoadAsync(retention).ConfigureAwait(true);
            if (load.ResetBackup is { } backup)
            {
                StatusText = Localizer.Format(L.Correspondence.JournalReset, backup);
            }
        }
        catch (PlatformNotSupportedException)
        {
            Error = Localizer.Format(L.Correspondence.JournalUnavailable, Localizer[L.Correspondence.JournalNoProtector]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Error = Localizer.Format(L.Correspondence.JournalUnavailable, Errors.Translate(ex));
        }
    }

    /// <summary>
    /// The last draft of the settings (saved on close and before each send): its images are registered with the message
    /// page first (the editor drops images it does not know), then – unless the page already holds a message – the subject,
    /// the body, the salutation, the custom fields, the list item template and the options the draft carries. Missing or
    /// changed image files are left out.
    /// </summary>
    private async Task LoadLastDraftAsync()
    {
        if (Correspondence.Templates.LoadLastDraft() is not { } draft)
        {
            return;
        }

        IReadOnlyDictionary<string, MessageImage> images;
        try
        {
            images = await Correspondence.Templates.LoadImagesAsync(draft).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            images = new Dictionary<string, MessageImage>();
        }

        foreach (var image in images.Values)
        {
            if (!Message.BodyImages.ContainsKey(image.ContentId))
            {
                Message.AddImage(image.Data, image.FileName);
            }
        }

        if (!Message.IsBodyEmpty || !string.IsNullOrEmpty(Message.Subject))
        {
            return;   // the page took the draft from the settings itself
        }

        Message.Subject = draft.Subject;
        Message.ListItemTemplate = draft.ListItemTemplate;
        if (!string.IsNullOrWhiteSpace(draft.Salutation))
        {
            Message.Salutation = draft.Salutation;
        }

        if (Message.CustomFields.Count == 0)
        {
            foreach (var field in draft.CustomFields)
            {
                Message.CustomFields.Add(new CustomFieldViewModel(field.Name, field.Value));
            }
        }

        if (draft.VotingEnabled is { } voting)
        {
            Message.UseVoting = voting;
        }

        if (!string.IsNullOrWhiteSpace(draft.VotingOptions))
        {
            Message.VotingOptions = draft.VotingOptions;
        }

        if (draft.GroupByAddress is { } group)
        {
            Message.GroupByAddress = group;
        }

        Message.BodyHtml = draft.BodyHtml;
    }

    /// <summary>The Outlook signatures of the message page (file system, no Outlook needed).</summary>
    private async Task RefreshSignaturesAsync()
    {
        try
        {
            if (Message.RefreshSignaturesCommand.CanExecute(null))
            {
                await Message.RefreshSignaturesCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = Errors.Translate(ex);
        }
    }

    /// <summary>The request's objects: recipients from AD, or the scope of the mailbox owner query.</summary>
    private async Task ApplyRequestAsync()
    {
        var dns = Request.DistinguishedNames.Where(dn => !string.IsNullOrWhiteSpace(dn)).ToList();
        if (Request.Start == CorrespondenceStart.MailboxOwners)
        {
            if (dns.Count > 0)
            {
                Mailboxes.UseSelection(dns);
            }

            IsMailboxesTabVisible = true;
            SelectedTab = CorrespondenceTab.Mailboxes;
            return;
        }

        if (dns.Count == 0)
        {
            return;
        }

        try
        {
            await Recipients.AddFromDirectoryAsync(dns).ConfigureAwait(true);
        }
        catch (DirectoryException ex)
        {
            Error = Errors.Translate(ex);
        }
        catch (Exception ex) when (ex is SessionEndedException or InvalidOperationException)
        {
            Error = NoSessionMessage;
        }
    }

    private void OnMailboxRecipientsAdded(object? sender, EventArgs e) =>
        _ = Services.Dispatcher.RunAsync(() => SelectedTab = CorrespondenceTab.Recipients);

    [RelayCommand]
    private void ShowMailboxes()
    {
        IsMailboxesTabVisible = true;
        SelectedTab = CorrespondenceTab.Mailboxes;
    }

    /// <summary>The help of the window (MailerWPiA's "Pomoc" adapted to AD Manager) in the report window.</summary>
    [RelayCommand]
    private async Task HelpAsync()
    {
        var title = Localizer[L.Correspondence.HelpTitle];
        var text = Localizer[L.Correspondence.HelpText];
        if (Correspondence.Dialogs is { } dialogs)
        {
            await dialogs.ShowReportAsync(title, text).ConfigureAwait(true);
        }
        else
        {
            await Services.Dialogs.ShowInfoAsync(title, text).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Close() => RequestClose(null);
}
