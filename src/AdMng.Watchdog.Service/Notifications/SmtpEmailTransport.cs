using System.Globalization;
using System.Net;
using System.Security.Authentication;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// E-mail over MailKit (spec 15.6): one connection per message (its recipient chunks share it), TLS 1.2/1.3 only,
/// <see cref="SecureSocketOptions"/> mapped from <see cref="SmtpSecurity"/> (never Auto), certificate validation per
/// <see cref="SmtpCertificateValidation"/>, revocation check per settings. Credentials pass the
/// <see cref="DestinationGuard"/> BEFORE the connection is opened (a refused secret means nothing is sent); Basic and
/// XOAUTH2 only over TLS. A rejected recipient does not stop the others (nor does a chunk whose recipients were all
/// rejected stop the other chunks): recipients rejected permanently (5xx) are dropped with a warning – the message fails
/// only when nobody got it –, recipients rejected temporarily (4xx) make the attempt Transient and the retry goes only to
/// the recipients that did not get the message yet (<see cref="SmtpRecipientProgress"/>, also after a failed chunk).
/// Rate limit: <c>MaxMessagesPerMinute</c>, one token per SMTP message (chunk); a denial is
/// <see cref="DeliveryOutcome.Deferred"/> and the chunks already sent are not sent again.
/// </summary>
public sealed partial class SmtpEmailTransport : INotificationTransport
{
    private const string RateLimitCode = "rateLimit";

    private readonly SmtpRecipientProgress _progress = new();
    private readonly IWatchdogConfigProvider _configuration;
    private readonly DestinationGuard _guard;
    private readonly SmtpOAuthTokenProvider _tokens;
    private readonly ChannelRateLimiters _limiters;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SmtpEmailTransport> _logger;

    public SmtpEmailTransport(
        IWatchdogConfigProvider configuration,
        DestinationGuard guard,
        SmtpOAuthTokenProvider tokens,
        ChannelRateLimiters limiters,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        ILogger<SmtpEmailTransport> logger)
    {
        _configuration = configuration;
        _guard = guard;
        _tokens = tokens;
        _limiters = limiters;
        _environment = environment;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public DeliveryChannelType ChannelType => DeliveryChannelType.Email;

    public async Task<DeliveryResult> SendAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var smtp = _configuration.Current.Configuration.Smtp;
        if (!smtp.Enabled || string.IsNullOrWhiteSpace(smtp.Host))
        {
            return Finish(message, SmtpAttempt.Failed(ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, "smtp.disabled")));
        }

        var decision = _limiters.TryAcquireEmail(smtp.MaxMessagesPerMinute);
        if (!decision.Acquired)
        {
            return Deferred(decision);
        }

        var attempt = await SendCoreAsync(message, smtp, recorder: null, outbox: true, cancellationToken).ConfigureAwait(false);
        return Finish(message, attempt);
    }

    /// <summary>The delivery code path shared with the test send (<see cref="DeliveryTester"/>): no rate limit and no
    /// recipient progress (a test message is never retried).</summary>
    internal async Task<ClassifiedDelivery> SendAsync(OutboxMessage message, SmtpSettings smtp, DeliveryStageRecorder? recorder, CancellationToken cancellationToken) =>
        (await SendCoreAsync(message, smtp, recorder, outbox: false, cancellationToken).ConfigureAwait(false)).Result;

    private async Task<SmtpAttempt> SendCoreAsync(OutboxMessage message, SmtpSettings smtp, DeliveryStageRecorder? recorder, bool outbox, CancellationToken cancellationToken)
    {
        if (message.Email is null || message.Recipients.Count == 0)
        {
            return SmtpAttempt.Failed(ClassifiedDelivery.Failure(DeliveryOutcome.Permanent, "smtp.noContent"));
        }

        IReadOnlyList<MimeMessage> mime;
        var localDomain = MimeMessageFactory.LocalDomain(smtp);
        try
        {
            mime = MimeMessageFactory.Create(message, smtp, localDomain, _timeProvider.GetUtcNow());
        }
        catch (FormatException)
        {
            return SmtpAttempt.Failed(ClassifiedDelivery.Failure(DeliveryOutcome.Permanent, "smtp.invalidAddress"));
        }

        // A retry goes only to the recipients that did not get the message yet (same chunks, same Message-IDs).
        var delivered = outbox ? _progress.Delivered(message.MessageId) : new HashSet<string>();
        var parts = new List<MimeMessage>(mime.Count);
        foreach (var part in mime)
        {
            var remaining = part.To.Mailboxes.Where(m => !delivered.Contains(SmtpRecipientProgress.Key(m.Address))).ToList();
            if (remaining.Count > 0 && remaining.Count < part.To.Count)
            {
                part.To.Clear();
                part.To.AddRange(remaining);
            }

            if (remaining.Count > 0)
            {
                parts.Add(part);
            }
        }

        if (parts.Count == 0)
        {
            _progress.Complete(message.MessageId);
            return new SmtpAttempt(ClassifiedDelivery.Delivered(), 0, 0, 0);
        }

        // Credentials first: nothing is sent (not even a connection is opened) for a refused secret or OAuth host.
        var credential = await ResolveCredentialAsync(smtp, recorder, cancellationToken).ConfigureAwait(false);
        if (credential.Failure is { } refused)
        {
            return SmtpAttempt.Failed(refused);
        }

        var validation = new SmtpCertificateValidation(smtp.CertificateValidation, smtp.PinnedSha256, _environment.IsDevelopment());
        using var client = new WatchdogSmtpClient
        {
            LocalDomain = localDomain,
            Timeout = (int)Math.Clamp(smtp.Timeout.TotalMilliseconds, 1000, int.MaxValue),
            SslProtocols = DeliveryTls.Protocols,
            CheckCertificateRevocation = smtp.CheckRevocation,
            ServerCertificateValidationCallback = validation.Validate,
        };
        var accepted = 0;
        var temporary = new List<ClassifiedDelivery>();
        var permanent = new List<ClassifiedDelivery>();
        try
        {
            await ConnectAsync(client, smtp, recorder, cancellationToken).ConfigureAwait(false);
            if (await AuthenticateAsync(client, smtp, credential, recorder, cancellationToken).ConfigureAwait(false) is { } authFailure)
            {
                return SmtpAttempt.Failed(authFailure);
            }

            for (var i = 0; i < parts.Count; i++)
            {
                // One rate-limit token per SMTP message: the caller took the one of the first chunk.
                if (outbox && i > 0 && _limiters.TryAcquireEmail(smtp.MaxMessagesPerMinute) is { Acquired: false } denied)
                {
                    return new SmtpAttempt(ClassifiedDelivery.Failure(DeliveryOutcome.Deferred, RateLimitCode, retryAfter: denied.RetryAfter), accepted, 0, 0);
                }

                var part = await SendOneAsync(client, parts[i], recorder, cancellationToken).ConfigureAwait(false);
                if (part.Accepted.Count > 0)
                {
                    accepted += part.Accepted.Count;
                    if (outbox)
                    {
                        _progress.Add(message.MessageId, part.Accepted, _timeProvider.GetUtcNow());
                    }
                }

                foreach (var (status, reply) in part.Rejected)
                {
                    var classified = SmtpErrorClassifier.ClassifyCommand(status, reply);
                    (classified.Result.Outcome == DeliveryOutcome.Transient ? temporary : permanent).Add(classified);
                }

                if (part.Accepted.Count == 0 && permanent.FirstOrDefault(p => p.Result.Outcome is DeliveryOutcome.ChannelBroken or DeliveryOutcome.AuthenticationFailed) is { } channel)
                {
                    recorder?.AddHints(channel.Hints);
                    return new SmtpAttempt(channel, accepted, temporary.Count, permanent.Count);    // e.g. 5.7.68: the other chunks would fail alike
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var classified = SmtpErrorClassifier.Classify(ex, certificateHint: validation.FailureHint);
            recorder?.AddHints(classified.Hints);
            return new SmtpAttempt(classified, accepted, temporary.Count, permanent.Count);
        }
        finally
        {
            await QuitAsync(client).ConfigureAwait(false);
        }

        if (temporary.Count > 0)
        {
            // 4xx for some recipients (greylisting, 452 too many recipients): retried later – for them only.
            recorder?.AddHints(temporary[0].Hints);
            return new SmtpAttempt(temporary[0], accepted, temporary.Count, permanent.Count);
        }

        if (permanent.Count > 0 && accepted == 0 && delivered.Count == 0)
        {
            recorder?.AddHints(permanent[0].Hints);
            return new SmtpAttempt(permanent[0], 0, 0, permanent.Count);       // nobody got it
        }

        if (outbox)
        {
            _progress.Complete(message.MessageId);
        }

        return new SmtpAttempt(ClassifiedDelivery.Delivered(), accepted, 0, permanent.Count);
    }

    private async Task<SmtpCredential> ResolveCredentialAsync(SmtpSettings smtp, DeliveryStageRecorder? recorder, CancellationToken cancellationToken)
    {
        switch (smtp.Authentication)
        {
            case SmtpAuthentication.Basic:
                var password = await _guard.SmtpPasswordAsync(smtp, cancellationToken).ConfigureAwait(false);
                return password.IsAvailable
                    ? new SmtpCredential(password.Value, null, null)
                    : SmtpCredential.Refused(ClassifiedDelivery.Failure(DeliveryOutcome.AuthenticationFailed, password.FailureCode ?? "secret.missing", password.Hint), recorder);
            case SmtpAuthentication.OAuth2ClientCredentials:
                recorder?.Begin();
                var token = await _tokens.AcquireAsync(smtp, cancellationToken).ConfigureAwait(false);
                if (!token.IsAvailable)
                {
                    recorder?.Failed(DeliveryStage.Token, token.FailureCode);
                    var outcome = token.IsTransient ? DeliveryOutcome.Transient : DeliveryOutcome.AuthenticationFailed;
                    return SmtpCredential.Refused(ClassifiedDelivery.Failure(outcome, token.FailureCode ?? "auth.token", token.Hint), recorder);
                }

                recorder?.Succeeded(DeliveryStage.Token);
                return new SmtpCredential(null, token.Token, null);
            default:
                return new SmtpCredential(null, null, null);
        }
    }

    private static async Task ConnectAsync(WatchdogSmtpClient client, SmtpSettings smtp, DeliveryStageRecorder? recorder, CancellationToken cancellationToken)
    {
        if (recorder is not null)
        {
            recorder.Begin();
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(smtp.Host!, cancellationToken).ConfigureAwait(false);
                recorder.Succeeded(DeliveryStage.Resolve, string.Create(CultureInfo.InvariantCulture, $"{addresses.Length} address(es)"));
            }
            catch (System.Net.Sockets.SocketException)
            {
                recorder.Failed(DeliveryStage.Resolve);
                throw;
            }

            recorder.Begin();
        }

        try
        {
            await client.ConnectAsync(smtp.Host!, smtp.Port, SecureOptions(smtp.Security), cancellationToken).ConfigureAwait(false);
        }
        catch (SslHandshakeException)
        {
            recorder?.Succeeded(DeliveryStage.Connect);
            recorder?.Failed(DeliveryStage.Tls);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            recorder?.Failed(DeliveryStage.Connect);
            throw;
        }

        recorder?.Succeeded(DeliveryStage.Connect, smtp.Port.ToString(CultureInfo.InvariantCulture));
        if (client.IsSecure)
        {
            recorder?.Succeeded(DeliveryStage.Tls, TlsDetail(client));
        }
        else if (smtp.Security != SmtpSecurity.None)
        {
            recorder?.Failed(DeliveryStage.Tls, "not negotiated");
        }
    }

    private async Task<ClassifiedDelivery?> AuthenticateAsync(
        WatchdogSmtpClient client,
        SmtpSettings smtp,
        SmtpCredential credential,
        DeliveryStageRecorder? recorder,
        CancellationToken cancellationToken)
    {
        if (smtp.Authentication == SmtpAuthentication.None)
        {
            return null;
        }

        if (!client.IsSecure)
        {
            // Never send a password or a token in clear text (validation insecureCredentials; StartTlsWhenAvailable fallback).
            recorder?.Failed(DeliveryStage.Authenticate, "no TLS");
            recorder?.AddHints([DeliveryHints.StartTlsMissing]);
            return ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, "smtp.insecureCredentials", DeliveryHints.StartTlsMissing);
        }

        recorder?.Begin();
        try
        {
            if (credential.Token is { } token)
            {
                await client.AuthenticateAsync(new SaslMechanismOAuth2(smtp.UserName ?? string.Empty, token), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.AuthenticateAsync(new NetworkCredential(smtp.UserName ?? string.Empty, credential.Password!.Reveal()), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var command = SmtpErrorClassifier.CommandOf(ex);
            recorder?.Failed(DeliveryStage.Authenticate, null, command is null ? null : (int)command.StatusCode, SmtpErrorClassifier.EnhancedStatus(command?.Message));
            throw;
        }

        recorder?.Succeeded(DeliveryStage.Authenticate);
        return null;
    }

    /// <summary>One SMTP message: the recipients that got it (DATA accepted) and the replies of the rejected ones. Every
    /// recipient rejected → no exception (MailKit resets the transaction, the connection stays usable for the next chunk);
    /// any other command failure is thrown.</summary>
    private static async Task<PartResult> SendOneAsync(WatchdogSmtpClient client, MimeMessage message, DeliveryStageRecorder? recorder, CancellationToken cancellationToken)
    {
        client.ResetEnvelope();
        recorder?.Begin();
        string reply;
        try
        {
            reply = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (SmtpCommandException) when (client.AcceptedRecipients == 0 && client.RejectedRecipients.Count > 0)
        {
            RecordEnvelope(client, recorder, dataSucceeded: false);
            return new PartResult([], [.. client.RejectedRecipients.Select(r => (r.Status, r.Reply))]);
        }
        catch (SmtpCommandException command)
        {
            RecordEnvelope(client, recorder, dataSucceeded: false, failure: command);
            throw;
        }

        RecordEnvelope(client, recorder, dataSucceeded: true, reply: reply);
        return new PartResult([.. client.AcceptedAddresses], [.. client.RejectedRecipients.Select(r => (r.Status, r.Reply))]);
    }

    private static void RecordEnvelope(WatchdogSmtpClient client, DeliveryStageRecorder? recorder, bool dataSucceeded, string? reply = null, SmtpCommandException? failure = null)
    {
        if (recorder is null)
        {
            return;
        }

        if (client.SenderStatus is { } sender)
        {
            recorder.Succeeded(DeliveryStage.Sender, null, sender);
        }
        else if (failure?.ErrorCode == SmtpErrorCode.SenderNotAccepted)
        {
            recorder.Failed(DeliveryStage.Sender, null, (int)failure.StatusCode, SmtpErrorClassifier.EnhancedStatus(failure.Message));
            return;
        }

        var rejected = client.RejectedRecipients.Count;
        var detail = string.Create(CultureInfo.InvariantCulture, $"{client.AcceptedRecipients} accepted, {rejected} rejected");
        if (client.AcceptedRecipients > 0)
        {
            recorder.Succeeded(DeliveryStage.Recipients, detail);
        }
        else
        {
            recorder.Failed(DeliveryStage.Recipients, detail, rejected > 0 ? client.RejectedRecipients[0].Status : null,
                rejected > 0 ? SmtpErrorClassifier.EnhancedStatus(client.RejectedRecipients[0].Reply) : null);
            return;
        }

        if (dataSucceeded)
        {
            recorder.Succeeded(DeliveryStage.Data, "250", 250, SmtpErrorClassifier.EnhancedStatus(reply));
        }
        else
        {
            recorder.Failed(DeliveryStage.Data, null, failure is null ? null : (int)failure.StatusCode, failure is null ? null : SmtpErrorClassifier.EnhancedStatus(failure.Message));
        }
    }

    private static async Task QuitAsync(SmtpClient client)
    {
        if (!client.IsConnected)
        {
            return;
        }

        try
        {
            using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync(true, quit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or MailKit.ProtocolException or SmtpCommandException or System.Net.Sockets.SocketException)
        {
            // QUIT is a courtesy – the message outcome is already decided.
        }
    }

    private DeliveryResult Finish(OutboxMessage message, SmtpAttempt attempt)
    {
        var result = attempt.Result.Result;
        if (attempt.TemporarilyRejected > 0)
        {
            Log.RecipientsDeferred(_logger, message.MessageId, attempt.TemporarilyRejected);
        }

        if (attempt.PermanentlyRejected > 0 && result.Outcome != DeliveryOutcome.Permanent)
        {
            Log.RecipientsRejected(_logger, message.MessageId, attempt.PermanentlyRejected);
        }

        if (result.Outcome == DeliveryOutcome.Delivered)
        {
            Log.Delivered(_logger, message.MessageId, attempt.Accepted);
        }
        else
        {
            Log.Failed(_logger, message.MessageId, result.Outcome, result.ErrorCode ?? "unknown");
        }

        return result;
    }

    private static DeliveryResult Deferred(RateLimitDecision decision) =>
        new() { Outcome = DeliveryOutcome.Deferred, ErrorCode = RateLimitCode, RetryAfter = decision.RetryAfter };

    private static SecureSocketOptions SecureOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, "Unknown SMTP security."),
    };

    private static string TlsDetail(SmtpClient client) => client.SslCipherSuite is { } suite
        ? string.Create(CultureInfo.InvariantCulture, $"{TlsName(client.SslProtocol)} {suite}")
        : TlsName(client.SslProtocol);

    private static string TlsName(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
        _ => protocol.ToString(),
    };

    /// <summary>What to authenticate with (or why the send stops before connecting).</summary>
    private sealed record SmtpCredential(SecretValue? Password, string? Token, ClassifiedDelivery? Failure)
    {
        public static SmtpCredential Refused(ClassifiedDelivery failure, DeliveryStageRecorder? recorder)
        {
            recorder?.Failed(DeliveryStage.Authenticate, failure.Result.ErrorCode);
            recorder?.AddHints(failure.Hints);
            return new SmtpCredential(null, null, failure);
        }

        public override string ToString() => "SmtpCredential";
    }

    /// <summary>The outcome of one attempt with the recipient counts (no addresses).</summary>
    private sealed record SmtpAttempt(ClassifiedDelivery Result, int Accepted, int TemporarilyRejected, int PermanentlyRejected)
    {
        public static SmtpAttempt Failed(ClassifiedDelivery result) => new(result, 0, 0, 0);
    }

    /// <summary>One SMTP message: accepted addresses (kept only in memory) and the replies of the rejected recipients.</summary>
    private sealed record PartResult(IReadOnlyList<string> Accepted, IReadOnlyList<(int Status, string Reply)> Rejected)
    {
        public override string ToString() => $"PartResult({Accepted.Count} accepted, {Rejected.Count} rejected)";
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 2200, Level = LogLevel.Information, Message = "E-mail {MessageId} delivered to {RecipientCount} recipient(s).")]
        public static partial void Delivered(ILogger logger, string messageId, int recipientCount);

        [LoggerMessage(EventId = 2201, Level = LogLevel.Information, Message = "E-mail {MessageId} not delivered: {Outcome} ({ErrorCode}).")]
        public static partial void Failed(ILogger logger, string messageId, DeliveryOutcome outcome, string errorCode);

        [LoggerMessage(EventId = 2202, Level = LogLevel.Warning,
            Message = "E-mail {MessageId}: {Count} recipient(s) rejected temporarily; the message is retried for them only.")]
        public static partial void RecipientsDeferred(ILogger logger, string messageId, int count);

        [LoggerMessage(EventId = 2203, Level = LogLevel.Warning,
            Message = "E-mail {MessageId}: {Count} recipient(s) rejected permanently; they do not get the message.")]
        public static partial void RecipientsRejected(ILogger logger, string messageId, int count);
    }
}
