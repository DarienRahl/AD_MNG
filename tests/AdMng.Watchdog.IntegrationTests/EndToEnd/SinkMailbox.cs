using System.Text;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Testing.Certificates;
using AdMng.Watchdog.Testing.Smtp;
using MimeKit;

namespace AdMng.Watchdog.IntegrationTests.EndToEnd;

/// <summary>The in-process SMTP sink of the watchdog test library (when Mailpit is not configured): STARTTLS with the
/// test certificate (pinned by SHA-256 in the service's settings) and Basic authentication against one account.</summary>
public sealed class SinkMailbox : ITestMailbox
{
    private readonly SmtpSink _sink;

    private SinkMailbox(SmtpSink sink) => _sink = sink;

    public static async Task<SinkMailbox> StartAsync(string userName, string password, CancellationToken cancellationToken)
    {
        var sink = new SmtpSink(new SmtpSinkOptions
        {
            Security = SmtpSinkSecurity.StartTls,
            Certificate = TestCertificates.Localhost,
            RequireAuthentication = true,
            Accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [userName] = password },
        });
        await sink.StartAsync(cancellationToken);
        return new SinkMailbox(sink);
    }

    public SmtpSettings Settings(string userName, string password) => new()
    {
        Enabled = true,
        Host = _sink.Host,
        Port = _sink.Port,
        Security = SmtpSecurity.StartTls,
        Authentication = SmtpAuthentication.Basic,
        UserName = userName,
        Password = new SecretField { NewValue = password },
        CertificateValidation = CertificateValidationMode.PinnedSha256,
        PinnedSha256 = TestCertificates.Sha256(TestCertificates.Localhost),
        CheckRevocation = false,
    };

    public Task<IReadOnlyList<ReceivedMail>> ReceivedAsync(string recipient, CancellationToken cancellationToken)
    {
        var mails = new List<ReceivedMail>();
        foreach (var message in _sink.Messages.Where(m => m.Recipients.Contains(recipient, StringComparer.OrdinalIgnoreCase)))
        {
            using var stream = new MemoryStream(message.Data);
            var mime = MimeMessage.Load(stream, cancellationToken);
            mails.Add(new ReceivedMail(mime.Subject ?? string.Empty, [.. mime.To.Mailboxes.Select(m => m.Address)], mime.HtmlBody ?? string.Empty,
                mime.TextBody ?? string.Empty, Encoding.UTF8.GetString(message.Data)));
        }

        return Task.FromResult<IReadOnlyList<ReceivedMail>>(mails);
    }

    public ValueTask DisposeAsync() => _sink.DisposeAsync();
}
