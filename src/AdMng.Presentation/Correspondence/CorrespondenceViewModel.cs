using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
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
/// session (rule 4); after it ended the AD actions are disabled while Outlook sending keeps working.
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
    }

    /// <summary>"Korespondencja – {domain}".</summary>
    public override string WindowTitle => Localizer.Format(L.Correspondence.WindowTitle, ConfirmationDomain);

    /// <summary>What the window was opened with.</summary>
    public CorrespondenceRequest Request { get; }

    [ObservableProperty]
    public partial CorrespondenceTab SelectedTab { get; set; }

    /// <summary>The "Skrzynki" tab is shown (started with MailboxOwners, or after <see cref="ShowMailboxesCommand"/>).</summary>
    [ObservableProperty]
    public partial bool IsMailboxesTabVisible { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public OutlookConnectionViewModel Outlook { get; }

    public MailboxOwnersPageViewModel Mailboxes { get; }

    public RecipientsPageViewModel Recipients { get; }

    public MessagePageViewModel Message { get; }

    public PreviewPageViewModel Preview { get; }

    public SendPageViewModel Send { get; }

    public ResponsesPageViewModel Responses { get; }

    private CorrespondenceServices Correspondence { get; }

    /// <summary>
    /// Loads the settings into the pages, the journal, the signatures and the draft; applies the request (DNs → recipients
    /// from AD, or the mailbox scope); connects Outlook in the background (status only, no modal error).
    /// </summary>
    public override Task InitializeAsync() => Task.CompletedTask;

    /// <summary>While sending asks to stop; saves the last draft and the options.</summary>
    public override Task<bool> CanCloseAsync() => Task.FromResult(true);

    /// <summary>The AD actions are disabled; Outlook sending keeps working.</summary>
    protected override void OnSessionEnded() => Recipients.CanUseDirectory = false;

    [RelayCommand]
    private void ShowMailboxes()
    {
        IsMailboxesTabVisible = true;
        SelectedTab = CorrespondenceTab.Mailboxes;
    }

    /// <summary>The Polish help text in the report window.</summary>
    [RelayCommand]
    private Task HelpAsync() => Task.CompletedTask;

    [RelayCommand]
    private void Close() => RequestClose(null);
}
