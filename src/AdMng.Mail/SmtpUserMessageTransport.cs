using System.Net;
using System.Security.Authentication;
using AdMng.Core.Messaging;
using AdMng.Watchdog.Contracts.Api;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AdMng.Mail;

/// <summary>Creates <see cref="SmtpUserMessageTransport"/> instances (registered by the app).</summary>
public sealed class SmtpTransportFactory(TimeProvider? time = null) : IDirectSmtpTransportFactory
{
    public IDirectSmtpTransport Create(DirectSmtpOptions options, NetworkCredential? credential) =>
        new SmtpUserMessageTransport(options, credential, time);
}

/// <summary>
/// Direct SMTP transport of messages to users (design note section 6): TLS per <see cref="SmtpTlsMode"/> (TLS 1.2+),
/// certificate check per <see cref="SmtpCertificatePolicy"/>, authentication none / current Windows account (SASL GSSAPI,
/// else NTLM) / user name + password (only over an encrypted connection). A rejected recipient does not abort the message;
/// failures are classified by <see cref="SmtpFailureClassifier"/>. No protocol logger is ever attached (it would log the
/// content and the AUTH exchange).
/// </summary>
public sealed class SmtpUserMessageTransport : IDirectSmtpTransport
{
    private const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    private readonly DirectSmtpOptions _options;
    private readonly NetworkCredential? _credential;
    private readonly TimeProvider _time;

    /// <param name="options">The connection (from the settings).</param>
    /// <param name="credential">User name and password for <see cref="SmtpAuthentication.Basic"/> (session memory only).</param>
    /// <param name="time">Clock of the Date header.</param>
    public SmtpUserMessageTransport(DirectSmtpOptions options, NetworkCredential? credential, TimeProvider? time = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credential = credential;
        _time = time ?? TimeProvider.System;
    }

    public UserMessageTransportKind Kind => UserMessageTransportKind.DirectSmtp;

    public string Description => _options.Description;

    public Task<UserMessageTransportStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(string.IsNullOrWhiteSpace(_options.Host)
            ? new UserMessageTransportStatus(false, UserMessageTransportStatus.NotConfigured)
            : UserMessageTransportStatus.Available);

    public async Task<IUserMessageConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var (client, result) = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            throw new UserMessageTransportException(result.Failure ?? new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "smtp.error"));
        }

        return new Connection(client, UserMimeMessageBuilder.LocalDomain(_options.LocalDomain), _time);
    }

    public async Task<SmtpConnectionTest> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var (client, result) = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (client is not null)
        {
            await CloseAsync(client).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<(EnvelopeSmtpClient? Client, SmtpConnectionTest Result)> OpenAsync(CancellationToken cancellationToken)
    {
        var certificate = new SmtpCertificatePolicy(_options.Certificate, _options.PinnedSha256);
        var client = new EnvelopeSmtpClient
        {
            LocalDomain = UserMimeMessageBuilder.LocalDomain(_options.LocalDomain),
            Timeout = (int)Math.Clamp(_options.Timeout.TotalMilliseconds, 1000, int.MaxValue),
            SslProtocols = Protocols,
            CheckCertificateRevocation = _options.Certificate == SmtpCertificateCheck.SystemTrust,
            ServerCertificateValidationCallback = certificate.Validate,
        };
        var stage = SmtpTestStage.Connect;
        try
        {
            if (string.IsNullOrWhiteSpace(_options.Host) || _options.Port is < 1 or > 65535)
            {
                throw Failure(UserMessageDeliveryOutcome.TransportFailed, "config.host");
            }

            if (_options.Certificate == SmtpCertificateCheck.PinnedSha256 && SmtpCertificatePolicy.NormalizePin(_options.PinnedSha256) is null)
            {
                throw Failure(UserMessageDeliveryOutcome.TransportFailed, "config.pin");
            }

            try
            {
                await client.ConnectAsync(_options.Host.Trim(), _options.Port, SocketOptions(_options.Tls), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SslHandshakeException or NotSupportedException or System.Security.Authentication.AuthenticationException)
            {
                stage = SmtpTestStage.Tls;
                throw;
            }

            stage = SmtpTestStage.Authenticate;
            var mechanism = await AuthenticateAsync(client, cancellationToken).ConfigureAwait(false);
            return (client, new SmtpConnectionTest
            {
                Success = true,
                Stage = SmtpTestStage.Done,
                IsEncrypted = client.IsSecure,
                StartTlsOffered = client.IsSecure || client.Capabilities.HasFlag(SmtpCapabilities.StartTLS),
                TlsProtocol = client.IsSecure ? client.SslProtocol.ToString() : null,
                CertificateSha256 = certificate.SeenSha256,
                AuthenticationMechanisms = [.. client.AuthenticationMechanisms.Order(StringComparer.Ordinal)],
                AuthenticatedWith = mechanism,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var delivery = ex is UserMessageTransportException transport ? transport.Delivery : SmtpFailureClassifier.Classify(ex, certificate.FailureHint);
            var result = new SmtpConnectionTest
            {
                Success = false,
                Stage = stage,
                Failure = delivery,
                IsEncrypted = client.IsConnected && client.IsSecure,
                StartTlsOffered = client.IsConnected && (client.IsSecure || client.Capabilities.HasFlag(SmtpCapabilities.StartTLS)),
                CertificateSha256 = certificate.SeenSha256,
                AuthenticationMechanisms = client.IsConnected ? [.. client.AuthenticationMechanisms.Order(StringComparer.Ordinal)] : [],
            };
            await CloseAsync(client).ConfigureAwait(false);
            return (null, result);
        }
    }

    /// <summary>Authenticates as configured; returns the mechanism used (null = none).</summary>
    private async Task<string?> AuthenticateAsync(EnvelopeSmtpClient client, CancellationToken cancellationToken)
    {
        switch (_options.Authentication)
        {
            case SmtpAuthentication.Basic:
                if (_credential is null || string.IsNullOrEmpty(_credential.UserName))
                {
                    throw Failure(UserMessageDeliveryOutcome.AuthenticationFailed, "auth.noCredential");
                }

                if (!client.IsSecure)
                {
                    // Credentials are never sent without encryption (same rule and hint as the Watchdog).
                    throw Failure(UserMessageDeliveryOutcome.TransportFailed, "auth.unencrypted", DeliveryHints.StartTlsMissing);
                }

                await client.AuthenticateAsync(_credential, cancellationToken).ConfigureAwait(false);
                return "USER";
            case SmtpAuthentication.CurrentWindowsAccount:
            {
                SaslMechanism mechanism = client.AuthenticationMechanisms.Contains("GSSAPI")
                    ? new SaslMechanismGssapi()
                    : client.AuthenticationMechanisms.Contains("NTLM")
                        ? new SaslMechanismNtlmNative()
                        : throw Failure(UserMessageDeliveryOutcome.AuthenticationFailed, "auth.mechanismNotOffered");
                await client.AuthenticateAsync(mechanism, cancellationToken).ConfigureAwait(false);
                return mechanism.MechanismName;
            }

            default:
                return null;
        }
    }

    private static UserMessageTransportException Failure(UserMessageDeliveryOutcome outcome, string code, string? hint = null) =>
        new(new UserMessageDelivery(outcome, code, Hint: hint));

    private static SecureSocketOptions SocketOptions(SmtpTlsMode mode) => mode switch
    {
        SmtpTlsMode.ImplicitTls => SecureSocketOptions.SslOnConnect,
        SmtpTlsMode.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
        SmtpTlsMode.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };

    private static async Task CloseAsync(EnvelopeSmtpClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.DisconnectAsync(true, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A broken connection cannot be closed politely.
        }
        finally
        {
            client.Dispose();
        }
    }

    private sealed class Connection(EnvelopeSmtpClient client, string localDomain, TimeProvider time) : IUserMessageConnection
    {
        public async Task<UserMessageDelivery> SendAsync(OutgoingUserMessage message, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            MimeMessage mime;
            try
            {
                mime = UserMimeMessageBuilder.Build(message, localDomain, time.GetUtcNow());
            }
            catch (FormatException)
            {
                return new UserMessageDelivery(UserMessageDeliveryOutcome.Rejected, "address");
            }

            client.ResetEnvelope();
            try
            {
                await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            }
            catch (SmtpCommandException) when (client.AcceptedAddresses.Count == 0 && client.RejectedRecipients.Count > 0)
            {
                var first = client.RejectedRecipients[0];
                return SmtpFailureClassifier.ClassifyReply(first.Status, first.Reply);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return SmtpFailureClassifier.Classify(ex);
            }

            var rejected = client.RejectedRecipients;
            var toRejected = rejected.Where(r => message.To.Any(t => string.Equals(t.Address, r.Address, StringComparison.OrdinalIgnoreCase))).ToList();
            if (toRejected.Count > 0 && toRejected.Count >= message.To.Count)
            {
                return SmtpFailureClassifier.ClassifyReply(toRejected[0].Status, toRejected[0].Reply);
            }

            return UserMessageDelivery.Accepted() with
            {
                RejectedCopies = [.. rejected.Select(r => r.Address).Except(toRejected.Select(r => r.Address), StringComparer.OrdinalIgnoreCase)],
            };
        }

        public async ValueTask DisposeAsync() => await CloseAsync(client).ConfigureAwait(false);
    }
}

/// <summary>
/// MailKit's client with the Watchdog's envelope handling: a rejected recipient does not abort the message and the
/// replies of RCPT TO are recorded (never logged).
/// </summary>
internal sealed class EnvelopeSmtpClient : SmtpClient
{
    /// <summary>Status, reply text and address of every rejected recipient of the current message.</summary>
    public List<(int Status, string Reply, string Address)> RejectedRecipients { get; } = [];

    public List<string> AcceptedAddresses { get; } = [];

    public void ResetEnvelope()
    {
        RejectedRecipients.Clear();
        AcceptedAddresses.Clear();
    }

    protected override void OnRecipientAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
    {
        AcceptedAddresses.Add(mailbox.Address);
        base.OnRecipientAccepted(message, mailbox, response);
    }

    protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response) =>
        RejectedRecipients.Add(((int)response.StatusCode, response.Response ?? string.Empty, mailbox.Address));
}
