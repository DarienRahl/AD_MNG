using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using AdMng.Core.Audit;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
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
/// <remarks>
/// <para>A run takes the send list of the recipients page (<see cref="CorrespondenceSendSet"/>): one message per row or per
/// address (grouping), or BCC messages of at most <see cref="MessagePageViewModel.BccChunkSize"/> recipients with the own
/// address in "Do". The engine is <see cref="CorrespondenceSender"/>. Rows get their status ("wysłano", "wersja robocza",
/// "test wysłany", "błąd – …"); only a sent message (not a draft, not a test) is recorded in the journal "już wysłane",
/// right after Outlook accepted it. The last draft and the options are saved before a run.</para>
/// <para>The results and the report hold addresses (the operator sees and may export them); the session log entry holds
/// only the kind of the run and the counts.</para>
/// </remarks>
public sealed partial class SendPageViewModel : ObservableObject
{
    /// <summary>The action of the session log entry of a run.</summary>
    public const string SessionLogAction = "SendCorrespondence";

    /// <summary>The "Server" of the session log entry of a run.</summary>
    public const string SessionLogServer = "Outlook";

    /// <summary>MailerWPiA's estimate of the time Outlook needs per message (the summary).</summary>
    private const double SecondsPerMessage = 0.8;

    private readonly HashSet<int> _applied = [];
    private CancellationTokenSource? _run;
    private bool _preparing;
    private DateTimeOffset? _lastRunAt;
    private string _lastSubject = string.Empty;
    private SendRunKind _lastRowsKind = SendRunKind.Send;

    public SendPageViewModel(DialogServices services, CorrespondenceServices correspondence, RecipientsPageViewModel recipients,
        MessagePageViewModel message, PreviewPageViewModel preview, OutlookConnectionViewModel outlook)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        Recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Preview = preview ?? throw new ArgumentNullException(nameof(preview));
        Outlook = outlook ?? throw new ArgumentNullException(nameof(outlook));

        // The collections live as long as this page.
        Results.CollectionChanged += (_, _) => ExportReportCommand.NotifyCanExecuteChanged();
        Recipients.Items.CollectionChanged += (_, _) => RetryFailedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// MailerWPiA's summary: count with skipped/invalid, sender, on behalf, mode, subject, signature, attachments, deferred,
    /// voting, estimated time.
    /// </summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendTestCommand), nameof(CreateDraftsCommand), nameof(SendCommand), nameof(StopCommand), nameof(RetryFailedCommand))]
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

    /// <summary>The run in progress or the last one (tests await it), or null.</summary>
    internal Task? CurrentRun { get; private set; }

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private RecipientsPageViewModel Recipients { get; }

    private MessagePageViewModel Message { get; }

    private PreviewPageViewModel Preview { get; }

    private OutlookConnectionViewModel Outlook { get; }

    private ILocalizer Localizer => Services.Localizer;

    private string Title => Localizer[L.CorrespondenceSend.Title];

    /// <summary>"4 min", "40 s" (MailerWPiA: seconds below a minute, rounded up).</summary>
    internal static string EtaText(ILocalizer localizer, TimeSpan remaining) => remaining.TotalMinutes < 1
        ? localizer.Format(L.CorrespondenceSend.EtaSeconds, (int)Math.Ceiling(remaining.TotalSeconds))
        : localizer.Format(L.CorrespondenceSend.EtaMinutes, (int)remaining.TotalMinutes);

    [RelayCommand]
    private void RefreshSummary()
    {
        var set = CorrespondenceSendSet.Of(Recipients);
        var bcc = Message.IsBcc;
        var messages = MessageCount(set, bcc, Message.GroupByAddress, Message.BccChunkSize);
        var lines = new List<string>
        {
            Localizer.Format(L.CorrespondenceSend.SummaryCount, set.Rows.Count, set.Skipped, set.Invalid),
            Localizer.Format(L.CorrespondenceSend.SummarySender, SenderText()),
        };
        if (OnBehalfOf() is { } onBehalf)
        {
            lines.Add(Localizer.Format(L.CorrespondenceSend.SummaryOnBehalf, onBehalf));
        }

        lines.Add(Localizer.Format(L.CorrespondenceSend.SummaryMode, ModeText()));
        if (bcc || Message.GroupByAddress)
        {
            lines.Add(Localizer.Format(L.CorrespondenceSend.SummaryMessages, messages));
        }

        lines.Add(Localizer.Format(L.CorrespondenceSend.SummarySubject,
            string.IsNullOrWhiteSpace(Message.Subject) ? Localizer[L.CorrespondenceSend.SubjectEmptyMark] : Message.Subject.Trim()));
        lines.Add(Localizer.Format(L.CorrespondenceSend.SummarySignature, Message.SelectedSignature?.Name ?? Localizer[L.CorrespondenceSend.NoSignature]));
        lines.Add(Localizer.Format(L.CorrespondenceSend.SummaryAttachments, AttachmentsText()));
        if (Message.TryGetDeferredDelivery(out var deliverAt) && deliverAt is { } at)
        {
            lines.Add(Localizer.Format(L.CorrespondenceSend.SummaryDeferred, LocalText(at)));
        }

        if (Message.ReadReceipt)
        {
            lines.Add(Localizer[L.CorrespondenceSend.SummaryReceipt]);
        }

        if (Message.UseVoting && Message.VotingOptionsText is { } voting)
        {
            lines.Add(Localizer.Format(L.CorrespondenceSend.SummaryVoting, voting.Replace(";", "; ", StringComparison.Ordinal)));
        }

        if (messages > 0)
        {
            var estimate = TimeSpan.FromSeconds(messages * (Message.DelaySeconds + SecondsPerMessage));
            lines.Add(Localizer.Format(L.CorrespondenceSend.SummaryEta, EtaText(Localizer, estimate)));
        }

        SummaryText = string.Join(Environment.NewLine, lines);
    }

    /// <summary>The messages a run of <paramref name="set"/> sends: one per row, per address (grouping) or per BCC chunk.</summary>
    internal static int MessageCount(CorrespondenceSendSet set, bool bcc, bool groupByAddress, int bccChunkSize)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (!bcc)
        {
            return groupByAddress ? set.AddressCount : set.Rows.Count;
        }

        var size = Math.Clamp(bccChunkSize, 1, CorrespondenceLimits.MaxBccPerMessage);
        return (set.AddressCount + size - 1) / size;
    }

    /// <summary>To the chosen account's address, else the profile's current user, else a prompt; subject "[TEST] …".</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task SendTestAsync() => CurrentRun = GuardAsync(TestCoreAsync);

    /// <summary>Every message saved in "Wersje robocze" (no journal entries).</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task CreateDraftsAsync() => CurrentRun = GuardAsync(() => StartAsync(SendRunKind.Drafts, retry: null));

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task SendAsync() => CurrentRun = GuardAsync(() => StartAsync(SendRunKind.Send, retry: null));

    private bool CanRun() => !IsSending && !_preparing && Correspondence.Outlook is not null;

    [RelayCommand(CanExecute = nameof(IsSending))]
    private void Stop() => _run?.Cancel();

    /// <summary>
    /// The rows whose message failed, again (after a confirmation; in the chosen mode): sent again after a send run, saved
    /// as drafts again after a draft run (the failed statuses come from the last run of the rows – MailerWPiA always sent).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryFailedAsync() => CurrentRun = GuardAsync(async () =>
    {
        var failed = CorrespondenceSendSet.Of(Recipients).Rows.Where(r => r.StatusKind == RecipientStatusKind.Failed).ToList();
        if (failed.Count == 0)
        {
            return;
        }

        var kind = _lastRowsKind;
        var addresses = failed.Select(r => CorrespondenceSendSet.Key(r.Email)).Distinct(StringComparer.Ordinal).Count();
        var question = kind == SendRunKind.Drafts ? L.CorrespondenceSend.RetryConfirmDrafts : L.CorrespondenceSend.RetryConfirm;
        if (await Services.Dialogs.ConfirmAsync(Title, Localizer.Format(question, addresses), destructive: kind == SendRunKind.Send).ConfigureAwait(true))
        {
            await StartAsync(kind, failed).ConfigureAwait(true);
        }
    });

    private bool CanRetry() => CanRun() && Recipients.Items.Any(r => r.StatusKind == RecipientStatusKind.Failed);

    /// <summary>One run at a time: while a run is prepared (validation, connection, confirmation) or sent, no other starts.</summary>
    private async Task GuardAsync(Func<Task> run)
    {
        if (IsSending || _preparing)
        {
            return;
        }

        _preparing = true;
        NotifyRunCommands();
        try
        {
            await run().ConfigureAwait(true);
        }
        finally
        {
            _preparing = false;
            NotifyRunCommands();
        }
    }

    private void NotifyRunCommands()
    {
        SendTestCommand.NotifyCanExecuteChanged();
        CreateDraftsCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The text report: date, subject, lines.</summary>
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportAsync()
    {
        var now = Services.Time.GetLocalNow();
        var title = Localizer[L.CorrespondenceSend.ReportTitle];
        var name = string.Create(CultureInfo.InvariantCulture, $"raport_wysylki_{now:yyyy-MM-dd_HHmm}.txt");
        var path = await Services.Files.SaveFileAsync(title, name, [new FileFilter(Localizer[L.CorrespondenceSend.TextFilter], "*.txt")]).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var text = new StringBuilder()
            .AppendLine(Localizer.Format(L.CorrespondenceSend.ReportHeader, (_lastRunAt ?? now).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
            .AppendLine(Localizer.Format(L.CorrespondenceSend.ReportSubject, _lastSubject))
            .AppendLine();
        foreach (var line in Results)
        {
            text.AppendLine(line.Text);
        }

        try
        {
            var content = text.ToString();
            await Task.Run(() => File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.CorrespondenceSend.ReportSaved, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer.Format(L.CorrespondenceSend.SaveFailed, Services.Errors.Translate(ex))).ConfigureAwait(true);
        }
    }

    private bool CanExportReport() => Results.Count > 0;

    /// <summary>
    /// A send or draft run: the message validated, the send list (or the rows to retry) made into messages, Outlook
    /// connected, MailerWPiA's confirmation (the count typed above the threshold; none for a retry, which asked already).
    /// </summary>
    private async Task StartAsync(SendRunKind kind, IReadOnlyList<CorrespondenceRecipientViewModel>? retry)
    {
        if (!await ValidateAsync().ConfigureAwait(true))
        {
            return;
        }

        var set = CorrespondenceSendSet.Of(Recipients);
        var rows = retry ?? set.Rows;
        if (rows.Count == 0)
        {
            await Services.Dialogs.ShowErrorAsync(Title, Localizer[L.CorrespondenceSend.NothingToSend]).ConfigureAwait(true);
            return;
        }

        var session = await ConnectAsync().ConfigureAwait(true);
        if (session is null)
        {
            return;
        }

        var bcc = Message.IsBcc;
        var group = Message.GroupByAddress;
        var chunkSize = Message.BccChunkSize;
        var own = Outlook.OwnAddress;
        var (jobs, jobRows) = await Task.Run(() => Jobs(rows, bcc, group, chunkSize, own)).ConfigureAwait(true);
        var recipients = bcc ? jobs.Sum(j => j.Bcc.Count) : jobs.Count;
        Message.TryGetDeferredDelivery(out var deliverAt);
        if (retry is null && !await ConfirmAsync(kind, set, recipients, jobs.Count, deliverAt).ConfigureAwait(true))
        {
            return;
        }

        if (retry is not null)
        {
            foreach (var row in retry)
            {
                row.Status = string.Empty;
                row.StatusKind = RecipientStatusKind.None;
            }
        }

        var draft = await Message.DraftAsync().ConfigureAwait(true);
        await SaveDraftAsync().ConfigureAwait(true);
        await RunAsync(kind, Options(kind, draft, set.KnownColumns, deliverAt), jobs, jobRows, session, bcc, retry is null ? set.Skipped + set.Invalid : 0)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// The messages of a run and the rows of each (off the UI thread: the rows' addresses, names and fields never change):
    /// one per row or per address, or BCC chunks with the own address in "Do" (none when it is unknown, as MailerWPiA).
    /// </summary>
    private static (IReadOnlyList<CorrespondenceSendJob> Jobs, IReadOnlyList<IReadOnlyList<CorrespondenceRecipientViewModel>> Rows) Jobs(
        IReadOnlyList<CorrespondenceRecipientViewModel> rows, bool bcc, bool groupByAddress, int chunkSize, string? ownAddress)
    {
        var jobs = new List<CorrespondenceSendJob>();
        var jobRows = new List<IReadOnlyList<CorrespondenceRecipientViewModel>>();
        if (bcc)
        {
            IReadOnlyList<string> to = string.IsNullOrWhiteSpace(ownAddress) ? [] : [ownAddress.Trim()];
            foreach (var chunk in CorrespondenceSendSet.ChunksOf(rows, chunkSize))
            {
                jobs.Add(new CorrespondenceSendJob(CorrespondenceSendSet.BccUnit, to, chunk.Addresses, [], chunk.Addresses));
                jobRows.Add(chunk.Rows);
            }
        }
        else
        {
            foreach (var unit in CorrespondenceSendSet.UnitsOf(rows, groupByAddress))
            {
                jobs.Add(new CorrespondenceSendJob(unit.Unit, [unit.Unit.Email], [], unit.Keys, [unit.Unit.Email]));
                jobRows.Add(unit.Rows);
            }
        }

        return (jobs, jobRows);
    }

    /// <summary>A test to self: one message to the own address (or one typed), with the fields of the recipient chosen on the
    /// preview (else the first message) – or, in the BCC mode, as the BCC message is sent.</summary>
    private async Task TestCoreAsync()
    {
        if (!await ValidateAsync().ConfigureAwait(true))
        {
            return;
        }

        var session = await ConnectAsync().ConfigureAwait(true);
        if (session is null)
        {
            return;
        }

        var address = Outlook.OwnAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            if (Correspondence.Dialogs is not { } dialogs
                || await dialogs.PromptTextAsync(Localizer[L.CorrespondenceSend.TestTitle], Localizer[L.CorrespondenceSend.TestPrompt], string.Empty, multiline: false)
                    .ConfigureAwait(true) is not { } typed
                || string.IsNullOrWhiteSpace(typed))
            {
                return;
            }

            address = typed;
        }

        address = address.Trim();
        if (!EmailAddressText.IsValid(address))
        {
            await Services.Dialogs.ShowErrorAsync(Localizer[L.CorrespondenceSend.TestTitle], Localizer.Format(L.CorrespondenceSend.InvalidAddress, address)).ConfigureAwait(true);
            return;
        }

        var set = CorrespondenceSendSet.Of(Recipients);
        CorrespondenceUnit unit;
        IReadOnlyList<CorrespondenceRecipientViewModel> rows = [];
        if (Message.IsBcc)
        {
            unit = CorrespondenceSendSet.BccUnit;
        }
        else
        {
            var units = set.Units(Message.GroupByAddress);
            var chosen = Preview.SelectedRecipient?.Value is { } selected
                ? units.FirstOrDefault(u => CorrespondenceSendSet.Key(u.Unit.Email) == CorrespondenceSendSet.Key(selected.Email))
                : null;
            if ((chosen ?? units.FirstOrDefault()) is { } found)
            {
                unit = found.Unit;
                rows = found.Rows;
            }
            else
            {
                unit = new CorrespondenceUnit(address, string.Empty, [new Dictionary<string, string>()]);
            }
        }

        Message.TryGetDeferredDelivery(out var deliverAt);
        var draft = await Message.DraftAsync().ConfigureAwait(true);
        await SaveDraftAsync().ConfigureAwait(true);
        var job = new CorrespondenceSendJob(unit, [address], [], [], [address]);
        await RunAsync(SendRunKind.Test, Options(SendRunKind.Test, draft, set.KnownColumns, deliverAt), [job], [rows], session, bcc: false, skipped: 0)
            .ConfigureAwait(true);
    }

    /// <summary>The run itself (on the thread pool), its live progress and statuses, the final lines and the session log.</summary>
    private async Task RunAsync(
        SendRunKind kind,
        CorrespondenceSendOptions options,
        IReadOnlyList<CorrespondenceSendJob> jobs,
        IReadOnlyList<IReadOnlyList<CorrespondenceRecipientViewModel>> jobRows,
        IOutlookSession session,
        bool bcc,
        int skipped)
    {
        using var cancellation = new CancellationTokenSource();
        _run = cancellation;
        _applied.Clear();
        IsSending = true;
        Results.Clear();
        SentCount = 0;
        FailedCount = 0;
        SkippedCount = skipped;
        ProgressMaximum = jobs.Count;
        ProgressValue = 0;
        ProgressText = Localizer[L.CorrespondenceSend.ProgressStarting];
        _lastRunAt = Services.Time.GetLocalNow();
        _lastSubject = Message.Subject?.Trim() ?? string.Empty;
        if (kind != SendRunKind.Test)
        {
            _lastRowsKind = kind;   // a test changes no row's status
        }

        var journal = new JournalRecorder(Correspondence.Journal, _lastSubject);
        var progress = Services.Dispatcher.CreateProgress<SendProgress>(p =>
        {
            if (!ReferenceEquals(_run, cancellation))
            {
                return;   // a late report of a run that has ended (its outcomes were applied at its end)
            }

            ApplyOutcome(kind, p.Outcome, jobs, jobRows, bcc, journal.IsLoaded);
            SentCount = p.Succeeded;
            FailedCount = p.Failed;
            ProgressValue = p.Done;
            ProgressText = ProgressTextOf(kind, p.Done, p.Total, p.Remaining);
        });

        SendRunResult result;
        try
        {
            var sender = new CorrespondenceSender(session, Correspondence.Delay, Services.Time);
            result = await Task.Run(
                () => sender.RunAsync(options, jobs, kind == SendRunKind.Send ? journal.RecordAsync : null, progress, cancellation.Token),
                CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            _run = null;
        }

        foreach (var outcome in result.Outcomes)
        {
            ApplyOutcome(kind, outcome, jobs, jobRows, bcc, journal.IsLoaded);
        }

        SentCount = result.Succeeded;
        FailedCount = result.Failed;
        ProgressValue = result.Outcomes.Count;
        Finish(kind, result, journal);
        IsSending = false;
        RetryFailedCommand.NotifyCanExecuteChanged();
        RefreshSummary();
    }

    /// <summary>The status of the rows of one message and its result line (once per message).</summary>
    private void ApplyOutcome(SendRunKind kind, SendJobOutcome outcome, IReadOnlyList<CorrespondenceSendJob> jobs,
        IReadOnlyList<IReadOnlyList<CorrespondenceRecipientViewModel>> jobRows, bool bcc, bool journalLoaded)
    {
        if (!_applied.Add(outcome.Index))
        {
            return;
        }

        var job = jobs[outcome.Index];
        var rows = jobRows[outcome.Index];
        if (outcome.Succeeded)
        {
            foreach (var row in rows)
            {
                switch (kind)
                {
                    case SendRunKind.Send:
                        SetStatus(row, RecipientStatusKind.Sent, Localizer[L.CorrespondenceSend.StatusSent]);
                        if (journalLoaded)
                        {
                            row.IsAlreadySent = true;
                        }

                        break;
                    case SendRunKind.Drafts:
                        SetStatus(row, RecipientStatusKind.Draft, Localizer[L.CorrespondenceSend.StatusDraft]);
                        break;
                    case SendRunKind.Test when row.StatusKind is RecipientStatusKind.None or RecipientStatusKind.Test:
                        SetStatus(row, RecipientStatusKind.Test, Localizer[L.CorrespondenceSend.StatusTest]);
                        break;
                }
            }

            Results.Add(new SendResultLine(SuccessLine(kind, outcome.Index, jobs, rows, bcc), true));
            return;
        }

        var failure = outcome.Failure!;
        Outlook.ReportFailure(failure);
        var text = CorrespondenceText.FailureText(Localizer, failure);
        if (kind != SendRunKind.Test)
        {
            // A failed test changes no row: "Ponów nieudane" must never send a real message because a test failed.
            var status = outcome.Result.SavedAsDraft
                ? Localizer.Format(L.CorrespondenceSend.StatusFailedDraft, text)
                : Localizer.Format(L.CorrespondenceSend.StatusFailed, text);
            foreach (var row in rows)
            {
                SetStatus(row, RecipientStatusKind.Failed, status);
            }
        }

        Results.Add(new SendResultLine(
            bcc
                ? Localizer.Format(L.CorrespondenceSend.LineBccFailed, outcome.Index + 1, jobs.Count, job.Bcc.Count, text)
                : Localizer.Format(L.CorrespondenceSend.LineFailed, string.Join(", ", job.To), text),
            false));
    }

    private string SuccessLine(SendRunKind kind, int index, IReadOnlyList<CorrespondenceSendJob> jobs, IReadOnlyList<CorrespondenceRecipientViewModel> rows, bool bcc)
    {
        var job = jobs[index];
        if (kind == SendRunKind.Test)
        {
            return Localizer.Format(L.CorrespondenceSend.LineTest, string.Join(", ", job.To));
        }

        if (bcc)
        {
            return Localizer.Format(L.CorrespondenceSend.LineBcc, index + 1, jobs.Count, job.Bcc.Count);
        }

        var name = job.Unit.Name;
        return string.IsNullOrWhiteSpace(name)
            ? Localizer.Format(L.CorrespondenceSend.LineSent, job.Unit.Email)
            : Localizer.Format(L.CorrespondenceSend.LineSentNamed, job.Unit.Email, name);
    }

    /// <summary>The closing lines (why it stopped, the totals, MailerWPiA's notes), the session log entry and the toast.</summary>
    private void Finish(SendRunKind kind, SendRunResult result, JournalRecorder journal)
    {
        switch (result.Stop)
        {
            case SendStopReason.Cancelled:
                Results.Add(new SendResultLine(Localizer[L.CorrespondenceSend.LineStopped], false));
                break;
            case SendStopReason.FailuresInRow:
                Results.Add(new SendResultLine(Localizer.Format(L.CorrespondenceSend.LineFailuresInRow, CorrespondenceSender.MaxFailuresInRow), false));
                break;
            case SendStopReason.OutlookStopped when result.StopFailure is { } failure:
                Results.Add(new SendResultLine(Localizer.Format(L.CorrespondenceSend.LineOutlookStopped, CorrespondenceText.FailureText(Localizer, failure)), false));
                break;
        }

        var ok = result.Succeeded;
        var failed = result.Failed;
        var head = result.Stop != SendStopReason.None
            ? Localizer.Format(L.CorrespondenceSend.DoneStopped, ok, failed, result.NotAttempted)
            : Localizer.Format(kind switch
            {
                SendRunKind.Drafts => L.CorrespondenceSend.DoneDrafts,
                SendRunKind.Test => L.CorrespondenceSend.DoneTest,
                _ => L.CorrespondenceSend.DoneSend,
            }, ok, failed);
        var clean = failed == 0 && result.NotAttempted == 0;
        ProgressText = head;
        Results.Add(new SendResultLine(head, clean));
        if (result.Outcomes.Any(o => !o.Succeeded && o.Result.SavedAsDraft))
        {
            Results.Add(new SendResultLine(Localizer[L.CorrespondenceSend.NoteSavedDrafts], false));
        }

        if (kind == SendRunKind.Send && ok > 0)
        {
            Results.Add(!journal.IsLoaded
                ? new SendResultLine(Localizer[L.CorrespondenceSend.NoteNoJournal], false)
                : journal.Failures > 0
                    ? new SendResultLine(Localizer.Format(L.CorrespondenceSend.NoteJournalFailed, journal.Failures,
                        journal.Error is { } error ? Services.Errors.Translate(error) : string.Empty), false)
                    : new SendResultLine(Localizer[L.CorrespondenceSend.NoteJournal], true));
        }

        var session = Services.Sessions.Current;
        Correspondence.AuditLog.Record(new AuditEntry(
            Services.Time.GetUtcNow(),
            session?.Info.BoundIdentity ?? string.Empty,
            SessionLogServer,
            session?.Info.DefaultNamingContext ?? string.Empty,
            SessionLogAction,
            Attribute: null,
            OldValue: Localizer[kind switch
            {
                SendRunKind.Drafts => L.CorrespondenceSend.SessionLogDrafts,
                SendRunKind.Test => L.CorrespondenceSend.SessionLogTest,
                _ => L.CorrespondenceSend.SessionLog,
            }],
            NewValue: string.Create(CultureInfo.InvariantCulture, $"{ok}/{result.Total}"),
            Succeeded: clean,
            Error: clean ? null : Localizer.Format(L.CorrespondenceSend.SessionLogFailures, failed, result.NotAttempted)));
        Services.Notifications.Show(clean ? NotificationSeverity.Success : NotificationSeverity.Warning, head);
    }

    private string ProgressTextOf(SendRunKind kind, int done, int total, TimeSpan? remaining) =>
        kind == SendRunKind.Drafts
            ? Localizer.Format(L.CorrespondenceSend.ProgressDrafts, done, total)
            : remaining is { } left
                ? Localizer.Format(L.CorrespondenceSend.ProgressSendEta, done, total, EtaText(Localizer, left))
                : Localizer.Format(L.CorrespondenceSend.ProgressSend, done, total);

    /// <summary>The message can be sent (the attachment files read again first); else the errors in a dialog.</summary>
    private async Task<bool> ValidateAsync()
    {
        await Message.RefreshAttachmentsAsync().ConfigureAwait(true);
        var errors = Message.ValidateForSend();
        if (errors.Count == 0)
        {
            return true;
        }

        await Services.Dialogs.ShowErrorAsync(Title,
            Localizer[L.CorrespondenceSend.CannotSend] + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "• " + e))).ConfigureAwait(true);
        return false;
    }

    /// <summary>The Outlook session (connected now when needed); null with the reason shown.</summary>
    private async Task<IOutlookSession?> ConnectAsync()
    {
        var session = await Outlook.EnsureConnectedAsync().ConfigureAwait(true);
        if (session is null)
        {
            await Services.Dialogs.ShowErrorAsync(Title,
                Localizer.Format(L.CorrespondenceSend.OutlookUnavailable, Outlook.FailureText ?? Outlook.StateText)).ConfigureAwait(true);
        }

        return session;
    }

    /// <summary>MailerWPiA's question before a send or draft run (the count typed above the threshold of the settings).</summary>
    private async Task<bool> ConfirmAsync(SendRunKind kind, CorrespondenceSendSet set, int recipients, int messages, DateTimeOffset? deliverAt)
    {
        var lines = new List<string>
        {
            Localizer.Format(kind == SendRunKind.Drafts ? L.CorrespondenceSend.ConfirmDrafts : L.CorrespondenceSend.ConfirmSend,
                kind == SendRunKind.Drafts ? messages : recipients),
            string.Empty,
            Localizer.Format(L.CorrespondenceSend.SummarySender, SenderText()),
        };
        if (OnBehalfOf() is { } onBehalf)
        {
            lines.Add(Localizer.Format(L.CorrespondenceSend.SummaryOnBehalf, onBehalf));
        }

        lines.Add(Localizer.Format(L.CorrespondenceSend.SummaryMode, ModeText()));
        if (Message.Attachments.Count > 0)
        {
            lines.Add(Localizer.Format(L.CorrespondenceSend.ConfirmAttachments, Message.Attachments.Count, string.Join(", ", Message.Attachments.Select(a => a.Name))));
        }

        if (set.Skipped > 0)
        {
            lines.Add(Localizer.Format(L.CorrespondenceSend.ConfirmSkipped, set.Skipped));
        }

        if (set.Invalid > 0)
        {
            lines.Add(Localizer.Format(L.CorrespondenceSend.ConfirmInvalid, set.Invalid));
        }

        if (deliverAt is { } at)
        {
            lines.Add(Localizer.Format(L.CorrespondenceSend.SummaryDeferred, LocalText(at)));
        }

        if (kind == SendRunKind.Send && !Message.IsBcc && Message.DelaySeconds < 2 && messages > 50)
        {
            lines.Add(Localizer.Format(L.CorrespondenceSend.ConfirmDelayWarning, Message.DelaySeconds, messages));
        }

        if (kind == SendRunKind.Send)
        {
            lines.Add(string.Empty);
            lines.Add(Localizer[L.CorrespondenceSend.ConfirmIrreversible]);
        }

        var count = kind == SendRunKind.Drafts ? messages : recipients;
        var threshold = Math.Max(1, Services.Settings.Current.Correspondence.ConfirmationThreshold);
        if (count > threshold)
        {
            lines.Add(string.Empty);
            lines.Add(Localizer.Format(L.CorrespondenceSend.ConfirmTyped, count));
            return await Services.Dialogs.ConfirmTypedAsync(Title, string.Join(Environment.NewLine, lines), count.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(true);
        }

        return await Services.Dialogs.ConfirmAsync(Title, string.Join(Environment.NewLine, lines), destructive: kind == SendRunKind.Send).ConfigureAwait(true);
    }

    /// <summary>What every message of the run shares.</summary>
    private CorrespondenceSendOptions Options(SendRunKind kind, CorrespondenceDraft draft, IReadOnlyCollection<string> knownColumns, DateTimeOffset? deliverAt) => new()
    {
        Draft = draft,
        KnownColumns = knownColumns,
        Mode = kind == SendRunKind.Drafts ? OutlookSendMode.Draft : OutlookSendMode.Send,
        SubjectPrefix = kind == SendRunKind.Test ? CorrespondenceSender.TestSubjectPrefix : string.Empty,
        Attachments = [.. Message.Attachments.Select(a => new OutlookAttachment(a.Path, a.Name))],
        Account = Outlook.AccountAddress,
        OnBehalfOf = OnBehalfOf(),
        ReadReceipt = Message.ReadReceipt,
        DeferredDelivery = deliverAt,
        VotingOptions = Message.UseVoting ? Message.VotingOptionsText : null,
        CampaignId = kind == SendRunKind.Test ? null : CorrespondenceSender.NewCampaignId(),
        Delay = kind == SendRunKind.Drafts ? TimeSpan.Zero : TimeSpan.FromSeconds(Message.DelaySeconds),
    };

    /// <summary>The last draft and the message options before a run (a failing save does not stop it).</summary>
    private async Task SaveDraftAsync()
    {
        try
        {
            await Services.Settings.UpdateAsync(settings =>
            {
                Message.SaveTo(settings.Correspondence);
                Outlook.SaveTo(settings.Correspondence);
            }).ConfigureAwait(true);
            await Correspondence.Templates.SaveLastDraftAsync(Message.ToTemplate(), Message.BodyImages.Values).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The settings file cannot be written right now; sending goes on.
        }
    }

    private string SenderText() => Outlook.SelectedAccount?.ToString() ?? Localizer[L.CorrespondencePreview.DefaultAccount];

    private string? OnBehalfOf() => string.IsNullOrWhiteSpace(Outlook.OnBehalfOf) ? null : Outlook.OnBehalfOf.Trim();

    private string ModeText() => Message.IsBcc
        ? Localizer.Format(L.CorrespondenceSend.ModeBcc, Message.BccChunkSize)
        : Localizer[Message.GroupByAddress ? L.CorrespondenceSend.ModeGrouped : L.CorrespondenceSend.ModeIndividual];

    private string AttachmentsText() => Message.Attachments.Count == 0
        ? Localizer[L.CorrespondenceSend.NoAttachments]
        : Localizer.Format(L.CorrespondenceSend.AttachmentList, Message.Attachments.Count, string.Join(", ", Message.Attachments.Select(a => a.Name)));

    private string LocalText(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Services.Time.LocalTimeZone).ToString("g", Localizer.Culture);

    private static void SetStatus(CorrespondenceRecipientViewModel row, RecipientStatusKind kind, string text)
    {
        row.StatusKind = kind;
        row.Status = text;
    }

    /// <summary>The kind of a run.</summary>
    private enum SendRunKind
    {
        Send,
        Drafts,
        Test,
    }

    /// <summary>
    /// Records sent addresses in the journal "już wysłane" (from the thread pool, right after Outlook accepted the message):
    /// the subject as typed, never the merged one. A journal that is not loaded records nothing; a write that fails is
    /// counted (the message was sent all the same).
    /// </summary>
    private sealed class JournalRecorder(CorrespondenceJournal journal, string subject)
    {
        private int _failures;
        private Exception? _error;

        public bool IsLoaded { get; } = journal.IsLoaded;

        public int Failures => Volatile.Read(ref _failures);

        /// <summary>The last failed write (translated for the operator when the run ends), or null.</summary>
        public Exception? Error => Volatile.Read(ref _error);

        public async Task RecordAsync(CorrespondenceSendJob job)
        {
            if (!IsLoaded)
            {
                return;
            }

            foreach (var address in job.Addresses)
            {
                try
                {
                    await journal.AppendAsync(address, subject).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    Interlocked.Increment(ref _failures);
                    Volatile.Write(ref _error, ex);
                }
            }
        }

        public override string ToString() => nameof(JournalRecorder);
    }
}
