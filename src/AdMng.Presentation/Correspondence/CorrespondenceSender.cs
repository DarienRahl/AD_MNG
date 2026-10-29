using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// One message of a run (design note 6.4): the unit it is rendered with, its recipients and the keys stored with it.
/// </summary>
/// <param name="Unit">The fields of the message (a BCC message: <see cref="CorrespondenceSendSet.BccUnit"/>).</param>
/// <param name="To">The visible recipients (the unit's address; the own address of a BCC message or a test).</param>
/// <param name="Bcc">The hidden recipients of a BCC message.</param>
/// <param name="Keys">The mailbox keys of the message (stored as <c>AdMngKeys</c> with a campaign id).</param>
/// <param name="Addresses">The recipients the message is for (recorded in the journal after a send).</param>
internal sealed record CorrespondenceSendJob(
    CorrespondenceUnit Unit,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Bcc,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> Addresses)
{
    public override string ToString() => $"{nameof(CorrespondenceSendJob)} ({To.Count} to, {Bcc.Count} bcc)";
}

/// <summary>What every message of a run shares (taken on the UI thread before the run starts).</summary>
internal sealed record CorrespondenceSendOptions
{
    public required CorrespondenceDraft Draft { get; init; }

    /// <summary>Every column of the recipient list.</summary>
    public required IReadOnlyCollection<string> KnownColumns { get; init; }

    public OutlookSendMode Mode { get; init; } = OutlookSendMode.Send;

    /// <summary>"[TEST] " for a test to self, else empty.</summary>
    public string SubjectPrefix { get; init; } = string.Empty;

    /// <summary>The files attached to every message.</summary>
    public IReadOnlyList<OutlookAttachment> Attachments { get; init; } = [];

    /// <summary>The SMTP address of the profile account to send from (null: the default account).</summary>
    public string? Account { get; init; }

    public string? OnBehalfOf { get; init; }

    public bool ReadReceipt { get; init; }

    public DateTimeOffset? DeferredDelivery { get; init; }

    /// <summary>"Tak;Nie" – the voting buttons, or null.</summary>
    public string? VotingOptions { get; init; }

    /// <summary>The id of the run stored with every message (the "Odpowiedzi" tab), or null.</summary>
    public string? CampaignId { get; init; }

    /// <summary>The pause between two messages.</summary>
    public TimeSpan Delay { get; init; }

    public override string ToString() => $"{nameof(CorrespondenceSendOptions)} {Mode}";
}

/// <summary>Why a run ended before its last message.</summary>
internal enum SendStopReason
{
    /// <summary>Every message was tried.</summary>
    None,

    /// <summary>The operator stopped it (or closed the window).</summary>
    Cancelled,

    /// <summary><see cref="CorrespondenceSender.MaxFailuresInRow"/> failures in a row (usually Outlook or the connection is gone).</summary>
    FailuresInRow,

    /// <summary>A failure after which nothing can be sent (<see cref="OutlookFailure.StopsRun"/>).</summary>
    OutlookStopped,
}

/// <summary>The answer of one message of a run (<see cref="Index"/> into the jobs).</summary>
internal sealed record SendJobOutcome(int Index, OutlookItemResult Result)
{
    public bool Succeeded => Result.Succeeded;

    /// <summary>Why the message failed (an unclassified failure for a result without one), or null.</summary>
    public OutlookFailure? Failure => Result.Succeeded ? null : Result.Failure ?? CorrespondenceSender.UnknownFailure(null);

    public override string ToString() => $"{nameof(SendJobOutcome)} {Index} {(Succeeded ? "ok" : Failure?.Code)}";
}

/// <summary>Progress of a run after one message.</summary>
/// <param name="Done">Messages tried so far.</param>
/// <param name="Total">Messages of the run.</param>
/// <param name="Succeeded">Messages Outlook accepted.</param>
/// <param name="Failed">Messages that failed.</param>
/// <param name="Remaining">The estimated time left, or null after the last message.</param>
/// <param name="Outcome">The message just tried.</param>
internal sealed record SendProgress(int Done, int Total, int Succeeded, int Failed, TimeSpan? Remaining, SendJobOutcome Outcome)
{
    public override string ToString() => $"{nameof(SendProgress)} {Done}/{Total}";
}

/// <summary>The end of a run.</summary>
internal sealed record SendRunResult(IReadOnlyList<SendJobOutcome> Outcomes, int Total, SendStopReason Stop, OutlookFailure? StopFailure)
{
    public int Succeeded => Outcomes.Count(o => o.Succeeded);

    public int Failed => Outcomes.Count(o => !o.Succeeded);

    /// <summary>Messages never tried (the run stopped before them).</summary>
    public int NotAttempted => Total - Outcomes.Count;

    public override string ToString() => $"{nameof(SendRunResult)} {Succeeded}/{Total} {Stop}";
}

/// <summary>
/// Sends the messages of a run through an Outlook session (design note 6.4 and 7.4, MailerWPiA's <c>Execute</c>): each
/// message is rendered (<see cref="CorrespondenceRenderer"/>) and handed to Outlook in order; a success in send mode is
/// reported to the caller (the journal) before the next one; a run stops when cancelled (between messages and during the
/// pause), after <see cref="MaxFailuresInRow"/> failures in a row, or at once after a failure that
/// <see cref="OutlookFailure.StopsRun"/>. The pause between messages goes through the injectable delay function; the
/// estimated time left is <c>left × (pause + average seconds per message)</c>. Runs on the thread pool; never logs or keeps
/// an address, a subject or a body.
/// </summary>
internal sealed class CorrespondenceSender
{
    /// <summary>MailerWPiA stops after this many failures in a row (Outlook or the connection is usually gone).</summary>
    public const int MaxFailuresInRow = 5;

    /// <summary>The subject prefix of a test to self.</summary>
    public const string TestSubjectPrefix = "[TEST] ";

    /// <summary>The step of a message refused before Outlook saw it (images over the limits).</summary>
    public const string ImagesStep = "images";

    /// <summary>The smallest average time per message of the estimate (MailerWPiA's 0.2 s).</summary>
    private static readonly TimeSpan MinimumAverage = TimeSpan.FromSeconds(0.2);

    private readonly IOutlookSession _session;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;

    public CorrespondenceSender(IOutlookSession session, Func<TimeSpan, CancellationToken, Task> delay, TimeProvider time)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>A new campaign id (32 hex characters, one per run).</summary>
    public static string NewCampaignId() => Guid.NewGuid().ToString("N");

    /// <summary>An unclassified failure (the HRESULT of an exception is kept, never its message).</summary>
    public static OutlookFailure UnknownFailure(int? hresult) => new(OutlookFailureKind.Unknown, OutlookFailureCodes.Unknown, null, hresult);

    /// <summary>The failure of a message whose images exceed the limits of one message.</summary>
    public static OutlookFailure ImagesTooLarge { get; } = new(OutlookFailureKind.ItemTooLarge, OutlookFailureCodes.ItemTooLarge, ImagesStep, null);

    /// <summary>
    /// The Outlook item of <paramref name="job"/>: subject (with the prefix) and body rendered with the job's unit, the images
    /// the document references, the attachments, account, "w imieniu", read receipt, deferred delivery, voting buttons and –
    /// with a campaign id – the job's keys.
    /// </summary>
    public static (OutlookMessage Message, RenderedCorrespondence Rendered) Build(CorrespondenceSendOptions options, CorrespondenceSendJob job)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(job);
        var rendered = CorrespondenceRenderer.Render(options.Draft, job.Unit, options.KnownColumns);
        var message = new OutlookMessage
        {
            To = job.To,
            Bcc = job.Bcc,
            Subject = options.SubjectPrefix + rendered.Subject,
            HtmlBody = rendered.HtmlDocument,
            InlineImages = rendered.Images,
            Attachments = options.Attachments,
            SendUsingAccount = options.Account,
            SentOnBehalfOf = options.OnBehalfOf,
            ReadReceipt = options.ReadReceipt,
            DeferredDelivery = options.DeferredDelivery,
            VotingOptions = options.VotingOptions,
            CampaignId = options.CampaignId,
            CampaignKeys = options.CampaignId is null ? [] : job.Keys,
        };
        return (message, rendered);
    }

    /// <summary>Sends (or saves) the messages of <paramref name="jobs"/> in order; see the class remarks.</summary>
    /// <param name="options">What the messages share.</param>
    /// <param name="jobs">The messages.</param>
    /// <param name="sent">Called after each message Outlook accepted in <see cref="OutlookSendMode.Send"/> (the journal);
    /// it must not throw.</param>
    /// <param name="progress">Reported after each message.</param>
    /// <param name="cancellationToken">Stops the run between messages and during the pause.</param>
    public async Task<SendRunResult> RunAsync(
        CorrespondenceSendOptions options,
        IReadOnlyList<CorrespondenceSendJob> jobs,
        Func<CorrespondenceSendJob, Task>? sent,
        IProgress<SendProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(jobs);
        var outcomes = new List<SendJobOutcome>(jobs.Count);
        var stop = SendStopReason.None;
        OutlookFailure? stopFailure = null;
        int succeeded = 0, failed = 0, inRow = 0;
        var busy = TimeSpan.Zero;
        for (var index = 0; index < jobs.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stop = SendStopReason.Cancelled;
                break;
            }

            var job = jobs[index];
            var started = _time.GetUtcNow();
            var result = await SendOneAsync(options, job, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                stop = SendStopReason.Cancelled;
                break;
            }

            busy += _time.GetUtcNow() - started;
            var outcome = new SendJobOutcome(index, result);
            outcomes.Add(outcome);
            if (result.Succeeded)
            {
                succeeded++;
                inRow = 0;
                if (sent is not null && options.Mode == OutlookSendMode.Send)
                {
                    await sent(job).ConfigureAwait(false);
                }
            }
            else
            {
                failed++;
                inRow++;
            }

            var done = index + 1;
            var left = jobs.Count - done;
            var average = TimeSpan.FromTicks(Math.Max(MinimumAverage.Ticks, busy.Ticks / done));
            progress?.Report(new SendProgress(done, jobs.Count, succeeded, failed, left > 0 ? (options.Delay + average) * left : null, outcome));
            if (outcome.Failure is { } failure)
            {
                if (failure.StopsRun)
                {
                    stop = SendStopReason.OutlookStopped;
                    stopFailure = failure;
                    break;
                }

                if (inRow >= MaxFailuresInRow)
                {
                    stop = SendStopReason.FailuresInRow;
                    break;
                }
            }

            if (left > 0 && options.Delay > TimeSpan.Zero)
            {
                try
                {
                    await _delay(options.Delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    stop = SendStopReason.Cancelled;
                    break;
                }
            }
        }

        return new SendRunResult(outcomes, jobs.Count, stop, stopFailure);
    }

    /// <summary>One message: rendered, refused when its images exceed the limits, else handed to Outlook. Null when the
    /// run was cancelled before Outlook took it. Exceptions of the session become failures (HRESULT only).</summary>
    private async Task<OutlookItemResult?> SendOneAsync(CorrespondenceSendOptions options, CorrespondenceSendJob job, CancellationToken cancellationToken)
    {
        var (message, rendered) = Build(options, job);
        if (rendered.ExceedsImageLimits)
        {
            return new OutlookItemResult(false, false, ImagesTooLarge);
        }

        try
        {
            return await _session.SendAsync(message, options.Mode, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            // The window closed and released the session: nothing more can be sent.
            return new OutlookItemResult(false, false, new OutlookFailure(OutlookFailureKind.Disconnected, OutlookFailureCodes.Disconnected, null, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A session reports failures as results; anything else is classified by its HRESULT only (a message may
            // contain an address).
            return new OutlookItemResult(false, false, UnknownFailure(ex.HResult));
        }
    }
}
