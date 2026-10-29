using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Correspondence;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The engine of "4 · Wysyłka" on its own (design note 6.4): the item built for a job, the estimate, the pause through the
/// injected function, the journal callback of send runs only, the stops and the exceptions of a session turned into
/// failures without their message.
/// </summary>
public sealed class CorrespondenceSenderTests
{
    private static readonly string[] Knowns = ["Miejscowość"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Build_TakesTheFieldsOfTheUnit_AndTheOptionsOfTheRun()
    {
        var options = Options() with
        {
            SubjectPrefix = CorrespondenceSender.TestSubjectPrefix,
            Account = "sekretariat.lodz@contoso.test",
            OnBehalfOf = "rektorat@contoso.test",
            ReadReceipt = true,
            DeferredDelivery = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero),
            VotingOptions = "Tak;Nie",
            Attachments = [new OutlookAttachment("/pliki/program.pdf", "program.pdf")],
            CampaignId = "4f1c2a9e0b7d4e6f8a3b5c7d9e1f2a3b",
        };

        var (message, rendered) = CorrespondenceSender.Build(options, Job("ug@brojce.pl", "Urząd Gminy Brójce", "Brójce", keys: ["kadry@contoso.test"]));

        Assert.Equal("[TEST] Zaproszenie – Brójce", message.Subject);
        Assert.Equal("Zaproszenie – Brójce", rendered.Subject);
        Assert.Equal(["ug@brojce.pl"], message.To);
        Assert.Empty(message.Bcc);
        Assert.Contains("Urząd Gminy Brójce", message.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(
            ("sekretariat.lodz@contoso.test", "rektorat@contoso.test", true, options.DeferredDelivery, "Tak;Nie"),
            (message.SendUsingAccount, message.SentOnBehalfOf, message.ReadReceipt, message.DeferredDelivery, message.VotingOptions));
        Assert.Equal(["/pliki/program.pdf"], message.Attachments.Select(a => a.Path));
        Assert.Equal(options.CampaignId, message.CampaignId);
        Assert.Equal(["kadry@contoso.test"], message.CampaignKeys);

        // Without a campaign (a test to self) the keys are not stored either.
        var (test, _) = CorrespondenceSender.Build(options with { CampaignId = null }, Job("ug@brojce.pl", "Urząd Gminy Brójce", "Brójce", keys: ["kadry@contoso.test"]));
        Assert.Null(test.CampaignId);
        Assert.Empty(test.CampaignKeys);
    }

    [Fact]
    public void Build_ABccJob_UsesNoFieldsOfARow()
    {
        var job = new CorrespondenceSendJob(CorrespondenceSendSet.BccUnit, ["jan.kowalski@contoso.test"], ["a@gmina.pl", "b@gmina.pl"], [], ["a@gmina.pl", "b@gmina.pl"]);

        var (message, rendered) = CorrespondenceSender.Build(Options(), job);

        Assert.Equal(["jan.kowalski@contoso.test"], message.To);
        Assert.Equal(["a@gmina.pl", "b@gmina.pl"], message.Bcc);
        Assert.Equal("Zaproszenie –", message.Subject);
        Assert.Contains("Szanowni Państwo", message.HtmlBody, StringComparison.Ordinal);
        Assert.Empty(rendered.UnknownFields);
    }

    [Fact]
    public async Task Run_PausesThroughTheDelayFunction_AndEstimatesTheTimeLeft()
    {
        var time = new ManualTimeProvider();
        var outlook = new FakeOutlookClient { BeforeSend = (_, _) => time.Advance(TimeSpan.FromSeconds(1)) };
        var session = await Connect(outlook);
        var pauses = new List<TimeSpan>();
        var sender = new CorrespondenceSender(session, (span, token) =>
        {
            pauses.Add(span);
            time.Advance(span);
            return Task.CompletedTask;
        }, time);
        var reports = new List<SendProgress>();

        var result = await sender.RunAsync(
            Options() with { Delay = TimeSpan.FromSeconds(2) },
            [Job("a@gmina.pl"), Job("b@gmina.pl"), Job("c@gmina.pl")],
            sent: null,
            new SynchronousProgress<SendProgress>(reports.Add),
            Token);

        Assert.Equal((3, 0, SendStopReason.None, 0), (result.Succeeded, result.Failed, result.Stop, result.NotAttempted));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], pauses);
        Assert.Equal([1, 2, 3], reports.Select(r => r.Done));
        // Left × (pause + average time Outlook took per message: 1 s).
        Assert.Equal([TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(3), (TimeSpan?)null], reports.Select(r => r.Remaining));
    }

    [Fact]
    public async Task Run_RecordsSentMessages_OnlyInSendMode()
    {
        var outlook = new FakeOutlookClient();
        outlook.Script("b@gmina.pl", FakeOutlookClient.Failed(OutlookFailureKind.RecipientUnresolved, "recipients"));
        var session = await Connect(outlook);
        var sender = new CorrespondenceSender(session, NoDelay, new ManualTimeProvider());
        var recorded = new List<string>();
        Task Record(CorrespondenceSendJob job)
        {
            recorded.AddRange(job.Addresses);
            return Task.CompletedTask;
        }

        var sent = await sender.RunAsync(Options(), [Job("a@gmina.pl"), Job("b@gmina.pl"), Job("c@gmina.pl")], Record, null, Token);
        Assert.Equal(["a@gmina.pl", "c@gmina.pl"], recorded);
        Assert.Equal("outlook.recipient", sent.Outcomes[1].Failure?.Code);

        recorded.Clear();
        var drafts = await sender.RunAsync(Options() with { Mode = OutlookSendMode.Draft }, [Job("a@gmina.pl"), Job("c@gmina.pl")], Record, null, Token);
        Assert.Equal(2, drafts.Succeeded);
        Assert.Empty(recorded);
        Assert.All(outlook.Items.TakeLast(2), item => Assert.Equal(OutlookSendMode.Draft, item.Mode));
    }

    [Fact]
    public async Task Run_StopsAfterFiveFailuresInARow_ButASuccessResetsTheCount()
    {
        var outlook = new FakeOutlookClient { DefaultResult = FakeOutlookClient.Failed(OutlookFailureKind.Busy) };
        outlook.Script("ok@gmina.pl", OutlookItemResult.Sent);
        var session = await Connect(outlook);
        var sender = new CorrespondenceSender(session, NoDelay, new ManualTimeProvider());
        CorrespondenceSendJob[] jobs =
        [
            Job("1@gmina.pl"), Job("2@gmina.pl"), Job("3@gmina.pl"), Job("4@gmina.pl"), Job("ok@gmina.pl"),
            Job("5@gmina.pl"), Job("6@gmina.pl"), Job("7@gmina.pl"), Job("8@gmina.pl"), Job("9@gmina.pl"), Job("10@gmina.pl"),
        ];

        var result = await sender.RunAsync(Options(), jobs, null, null, Token);

        Assert.Equal(SendStopReason.FailuresInRow, result.Stop);
        Assert.Equal((1, 9, 1), (result.Succeeded, result.Failed, result.NotAttempted));
        Assert.Null(result.StopFailure);
    }

    [Fact]
    public async Task Run_TheExceptionsOfASession_BecomeFailures_WithoutTheirMessage()
    {
        var session = new ThrowingSession(new InvalidOperationException("Nie można dostarczyć do jan.kowalski@contoso.test") { HResult = unchecked((int)0x80004005) });
        var sender = new CorrespondenceSender(session, NoDelay, new ManualTimeProvider());

        var result = await sender.RunAsync(Options(), [Job("jan.kowalski@contoso.test"), Job("b@gmina.pl")], null, null, Token);

        Assert.Equal(2, result.Failed);
        var failure = result.Outcomes[0].Failure!;
        Assert.Equal((OutlookFailureKind.Unknown, OutlookFailureCodes.Unknown, unchecked((int)0x80004005)), (failure.Kind, failure.Code, failure.HResult));
        Assert.DoesNotContain("@", failure.ToString(), StringComparison.Ordinal);

        // A session the window released: nothing more can be sent.
        var disposed = new CorrespondenceSender(new ThrowingSession(new ObjectDisposedException("session")), NoDelay, new ManualTimeProvider());
        var stopped = await disposed.RunAsync(Options(), [Job("a@gmina.pl"), Job("b@gmina.pl")], null, null, Token);
        Assert.Equal((SendStopReason.OutlookStopped, OutlookFailureKind.Disconnected, 1), (stopped.Stop, stopped.StopFailure?.Kind, stopped.NotAttempted));
    }

    [Fact]
    public async Task Run_CancelledDuringThePause_EndsBeforeTheNextMessage()
    {
        var outlook = new FakeOutlookClient();
        var session = await Connect(outlook);
        using var cancellation = new CancellationTokenSource();
        var sender = new CorrespondenceSender(session, (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, new ManualTimeProvider());

        var result = await sender.RunAsync(Options() with { Delay = TimeSpan.FromSeconds(3) }, [Job("a@gmina.pl"), Job("b@gmina.pl")], null, null, cancellation.Token);

        Assert.Equal((SendStopReason.Cancelled, 1, 1), (result.Stop, result.Succeeded, result.NotAttempted));
        Assert.Single(outlook.Items);
    }

    [Fact]
    public async Task Run_AMessageWithImagesOverTheLimits_IsRefusedBeforeOutlook()
    {
        var outlook = new FakeOutlookClient();
        var session = await Connect(outlook);
        var images = Enumerable.Range(0, CorrespondenceLimits.MaxImagesPerMessage + 1)
            .Select(i => MessageImage.Create(MessageImageSamples.Png(2 + (i % 7), 2 + (i / 7), (byte)i), $"zdjecie{i}.png", maxImageBytes: CorrespondenceLimits.MaxImageBytes))
            .ToList();
        var draft = Options().Draft with
        {
            BodyHtml = string.Concat(images.Select(i => $"<p><img src=\"cid:{i.ContentId}\"></p>")),
            Images = images.ToDictionary(i => i.ContentId, StringComparer.OrdinalIgnoreCase),
        };
        var sender = new CorrespondenceSender(session, NoDelay, new ManualTimeProvider());

        var result = await sender.RunAsync(Options() with { Draft = draft }, [Job("a@gmina.pl")], null, null, Token);

        Assert.Empty(outlook.Items);
        var failure = Assert.Single(result.Outcomes).Failure;
        Assert.Same(CorrespondenceSender.ImagesTooLarge, failure);
        Assert.Equal((OutlookFailureKind.ItemTooLarge, CorrespondenceSender.ImagesStep), (failure!.Kind, failure.Step));
    }

    private static Task NoDelay(TimeSpan span, CancellationToken token) => Task.CompletedTask;

    private static async Task<IOutlookSession> Connect(FakeOutlookClient outlook) =>
        (await outlook.ConnectAsync(Token)).Session ?? throw new InvalidOperationException("The fake did not connect.");

    private static CorrespondenceSendOptions Options() => new()
    {
        Draft = new CorrespondenceDraft
        {
            Subject = "Zaproszenie – {{Miejscowość}}",
            BodyHtml = "<p>{{zwrot}},</p><p>zapraszamy {{nazwa}}.</p>",
        },
        KnownColumns = Knowns,
    };

    private static CorrespondenceSendJob Job(string email, string name = "", string town = "Łódź", IReadOnlyList<string>? keys = null) =>
        new(new CorrespondenceUnit(email, name, [new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Miejscowość"] = town }]),
            [email], [], keys ?? [], [email]);

    /// <summary>A progress that reports on the calling thread (the engine's reports are checked in order).</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>A session whose sends throw (a COM object model error, a released session).</summary>
    private sealed class ThrowingSession(Exception exception) : IOutlookSession
    {
        public OutlookProfile Profile => FakeOutlookClient.SampleProfile;

        public bool IsConnected => true;

        public Task<OutlookItemResult> SendAsync(OutlookMessage message, OutlookSendMode mode, CancellationToken cancellationToken) =>
            Task.FromException<OutlookItemResult>(exception);

        public Task<OutlookRenderedMessage> RenderAsync(OutlookMessage message, CancellationToken cancellationToken) =>
            Task.FromException<OutlookRenderedMessage>(exception);

        public Task<OutlookDiagnostics> DiagnoseAsync(bool createTestItem, CancellationToken cancellationToken) =>
            Task.FromException<OutlookDiagnostics>(exception);

        public Task<IReadOnlyList<OutlookCampaign>> FindCampaignsAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<OutlookCampaign>>(exception);

        public Task<IReadOnlyList<OutlookVoteResponse>> ReadResponsesAsync(string campaignId, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<OutlookVoteResponse>>(exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override string ToString() => nameof(ThrowingSession);
    }
}
