using System.Collections.ObjectModel;
using AdMng.Presentation.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>One line of the send results ("wysłano: …", "błąd – …").</summary>
public sealed record SendResultLine(string Text, bool IsSuccess)
{
    public override string ToString() => nameof(SendResultLine);
}

/// <summary>
/// "4 · Wysyłka" (design note 6.4 and 7.4): the summary, test to self, drafts, send (confirmations of MailerWPiA, typed
/// count above the threshold), stop, retry failed, progress with ETA, results and the text report. Sending runs on the
/// thread pool, Outlook calls on the session's STA thread; one session log entry per run without addresses or subjects.
/// </summary>
public sealed partial class SendPageViewModel : ObservableObject
{
    /// <summary>The action of the session log entry of a run.</summary>
    public const string SessionLogAction = "SendCorrespondence";

    public SendPageViewModel(DialogServices services, CorrespondenceServices correspondence, RecipientsPageViewModel recipients,
        MessagePageViewModel message, PreviewPageViewModel preview, OutlookConnectionViewModel outlook)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        Recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Preview = preview ?? throw new ArgumentNullException(nameof(preview));
        Outlook = outlook ?? throw new ArgumentNullException(nameof(outlook));
    }

    /// <summary>
    /// MailerWPiA's summary: count with skipped/invalid, sender, on behalf, mode, subject, signature, attachments, deferred,
    /// voting, estimated time.
    /// </summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSending { get; private set; }

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial double ProgressMaximum { get; private set; }

    /// <summary>"Wysyłam 12 z 95 · zostało ok. 4 min".</summary>
    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int SentCount { get; private set; }

    [ObservableProperty]
    public partial int FailedCount { get; private set; }

    [ObservableProperty]
    public partial int SkippedCount { get; private set; }

    public ObservableCollection<SendResultLine> Results { get; } = [];

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private RecipientsPageViewModel Recipients { get; }

    private MessagePageViewModel Message { get; }

    private PreviewPageViewModel Preview { get; }

    private OutlookConnectionViewModel Outlook { get; }

    [RelayCommand]
    private void RefreshSummary()
    {
    }

    /// <summary>To the chosen account's address, else the profile's current user, else a prompt; subject "[TEST] …".</summary>
    [RelayCommand]
    private Task SendTestAsync() => Task.CompletedTask;

    /// <summary>Every message saved in "Wersje robocze" (no journal entries).</summary>
    [RelayCommand]
    private Task CreateDraftsAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task SendAsync() => Task.CompletedTask;

    [RelayCommand]
    private void Stop()
    {
    }

    [RelayCommand]
    private Task RetryFailedAsync() => Task.CompletedTask;

    /// <summary>The text report: date, subject, lines.</summary>
    [RelayCommand]
    private Task ExportReportAsync() => Task.CompletedTask;
}
