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
/// XOAUTH2 only over TLS. A rejected recipient does not stop the others; the message fails only when all are rejected.
/// Rate limit: <c>MaxMessagesPerMinute</c> (a denial is <see cref="DeliveryOutcome.Deferred"/>).
/// </summary>
public sealed partial class SmtpEmailTransport : INotificationTransport
{
    private readonly IWatchdogConfigProvider _configuration;
    private readonly DestinationGuard _guard;
    private readonly SmtpOAuthTokenProvider _tokens;
    private readonly ChannelRateLimiters _limiters;
    private readonly ChannelHealthTracker _health;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SmtpEmailTransport> _logger;

    public SmtpEmailTransport(
        IWatchdogConfigProvider configuration,
        DestinationGuard guard,
        SmtpOAuthTokenProvider tokens,
        ChannelRateLimiters limiters,
        ChannelHealthTracker health,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        ILogger<SmtpEmailTransport> logger)
    {
        _configuration = configuration;
        _guard = guard;
        _tokens = tokens;
        _limiters = limiters;
        _health = health;
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
            return Finish(message, ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, "smtp.disabled"));
        }

        var decision = _limiters.TryAcquireEmail(smtp.MaxMessagesPerMinute);
        if (!decision.Acquired)
        {
            return new DeliveryResult { Outcome = DeliveryOutcome.Deferred, ErrorCode = "rateLimit", RetryAfter = decision.RetryAfter };
        }

        var result = await SendAsync(message, smtp, recorder: null, cancellationToken).ConfigureAwait(false);
        return Finish(message, result);
    }

    /// <summary>The delivery code path shared with the test send (<see cref="DeliveryTester"/>).</summary>
    internal async Task<ClassifiedDelivery> SendAsync(OutboxMessage message, SmtpSettings smtp, DeliveryStageRecorder? recorder, CancellationToken cancellationToken)
    {
        if (message.Email is null || message.Recipients.Count == 0)
        {
            return ClassifiedDelivery.Failure(DeliveryOutcome.Permanent, "smtp.noContent");
        }

        IReadOnlyList<MimeMessage> mime;
        var localDomain = MimeMessageFactory.LocalDomain(smtp);
        try
        {
            mime = MimeMessageFactory.Create(message, smtp, localDomain, _timeProvider.GetUtcNow());
        }
        catch (FormatException)
        {
            return ClassifiedDelivery.Failure(DeliveryOutcome.Permanent, "smtp.invalidAddress");
        }

        // Credentials first: nothing is sent (not even a connection is opened) for a refused secret or OAuth host.
        var credential = await ResolveCredentialAsync(smtp, recorder, cancellationToken).ConfigureAwait(false);
        if (credential.Failure is { } refused)
        {
            return refused;
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
        try
        {
            await ConnectAsync(client, smtp, recorder, cancellationToken).ConfigureAwait(false);
            if (await AuthenticateAsync(client, smtp, credential, recorder, cancellationToken).ConfigureAwait(false) is { } authFailure)
            {
                return authFailure;
            }

            foreach (var part in mime)
            {
                var outcome = await SendOneAsync(client, part, recorder, cancellationToken).ConfigureAwait(false);
                if (outcome.Result.Outcome != DeliveryOutcome.Delivered)
                {
                    return outcome;
                }
            }

            return ClassifiedDelivery.Delivered();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var classified = SmtpErrorClassifier.Classify(ex, certificateHint: validation.FailureHint);
            recorder?.AddHints(classified.Hints);
            return classified;
        }
        finally
        {
            await QuitAsync(client).ConfigureAwait(false);
        }
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

    private static async Task<ClassifiedDelivery> SendOneAsync(WatchdogSmtpClient client, MimeMessage message, DeliveryStageRecorder? recorder, CancellationToken cancellationToken)
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
            var (status, text) = client.RejectedRecipients[0];
            var classified = SmtpErrorClassifier.ClassifyCommand(status, text);
            recorder?.AddHints(classified.Hints);
            return classified;
        }
        catch (SmtpCommandException command)
        {
            RecordEnvelope(client, recorder, dataSucceeded: false, failure: command);
            throw;
        }

        RecordEnvelope(client, recorder, dataSucceeded: true, reply: reply);
        return ClassifiedDelivery.Delivered();
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

    private DeliveryResult Finish(OutboxMessage message, ClassifiedDelivery classified)
    {
        _health.Record(DeliveryChannelType.Email, "email", classified.Result);
        if (classified.Result.Outcome == DeliveryOutcome.Delivered)
        {
            Log.Delivered(_logger, message.MessageId, message.Recipients.Count);
        }
        else
        {
            Log.Failed(_logger, message.MessageId, classified.Result.Outcome, classified.Result.ErrorCode ?? "unknown");
        }

        return classified.Result;
    }

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

    private static partial class Log
    {
        [LoggerMessage(EventId = 2200, Level = LogLevel.Information, Message = "E-mail {MessageId} delivered to {RecipientCount} recipient(s).")]
        public static partial void Delivered(ILogger logger, string messageId, int recipientCount);

        [LoggerMessage(EventId = 2201, Level = LogLevel.Information, Message = "E-mail {MessageId} not delivered: {Outcome} ({ErrorCode}).")]
        public static partial void Failed(ILogger logger, string messageId, DeliveryOutcome outcome, string errorCode);
    }
}
