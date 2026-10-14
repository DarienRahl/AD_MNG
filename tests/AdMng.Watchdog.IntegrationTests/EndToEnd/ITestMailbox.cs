using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.IntegrationTests.EndToEnd;

/// <summary>Where the end-to-end test's e-mails go: Mailpit (ADMNG_TEST_SMTP_*, CI and the lab) or an in-process sink.</summary>
public interface ITestMailbox : IAsyncDisposable
{
    /// <summary>The service's SMTP settings for this mailbox: host, port and TLS validation (Basic authentication with
    /// <paramref name="userName"/> and <paramref name="password"/> over STARTTLS).</summary>
    SmtpSettings Settings(string userName, string password);

    /// <summary>Messages received so far for <paramref name="recipient"/>.</summary>
    Task<IReadOnlyList<ReceivedMail>> ReceivedAsync(string recipient, CancellationToken cancellationToken);
}
