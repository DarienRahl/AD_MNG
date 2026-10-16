using System.Net.Http.Json;
using System.Text.Json;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

namespace AdMng.Watchdog.IntegrationTests.EndToEnd;

/// <summary>
/// Mailpit (tools/testlab/watchdog/start-mailpit.sh): SMTP with STARTTLS (self-signed certificate – the Development-only
/// validation mode accepts it), any user/password; messages read through its REST API (<c>/api/v1/search</c>,
/// <c>/api/v1/message/{id}</c>, <c>…/raw</c>). Mailpit is shared, so the test filters by its own unique recipient.
/// </summary>
public sealed class MailpitMailbox(string host, int port, Uri api) : ITestMailbox
{
    private readonly HttpClient _http = new() { BaseAddress = api, Timeout = TimeSpan.FromSeconds(30) };

    public SmtpSettings Settings(string userName, string password) => new()
    {
        Enabled = true,
        Host = host,
        Port = port,
        Security = SmtpSecurity.StartTls,
        Authentication = SmtpAuthentication.Basic,
        UserName = userName,
        Password = new SecretField { NewValue = password },
        CertificateValidation = CertificateValidationMode.AcceptAnyDevelopmentOnly,
        CheckRevocation = false,
    };

    public async Task<IReadOnlyList<ReceivedMail>> ReceivedAsync(string recipient, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"to:\"{recipient}\"");
        using var search = await _http.GetFromJsonAsync<JsonDocument>($"api/v1/search?query={query}&limit=200", cancellationToken)
            ?? throw new InvalidOperationException("Mailpit returned no search result.");
        var mails = new List<ReceivedMail>();
        foreach (var message in search.RootElement.GetProperty("messages").EnumerateArray())
        {
            var id = message.GetProperty("ID").GetString();
            using var details = await _http.GetFromJsonAsync<JsonDocument>($"api/v1/message/{id}", cancellationToken)
                ?? throw new InvalidOperationException("Mailpit returned no message.");
            var raw = await _http.GetStringAsync($"api/v1/message/{id}/raw", cancellationToken);
            var root = details.RootElement;
            mails.Add(new ReceivedMail(
                root.GetProperty("Subject").GetString() ?? string.Empty,
                [.. root.GetProperty("To").EnumerateArray().Select(a => a.GetProperty("Address").GetString() ?? string.Empty)],
                root.GetProperty("HTML").GetString() ?? string.Empty,
                root.GetProperty("Text").GetString() ?? string.Empty,
                raw));
        }

        return mails;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Mailpit when ADMNG_TEST_SMTP_HOST and ADMNG_TEST_MAILPIT_API are set, otherwise null.</summary>
    public static MailpitMailbox? FromEnvironment() =>
        WatchdogLab.SmtpHost is { } smtpHost && WatchdogLab.MailpitApi is { } apiUri ? new MailpitMailbox(smtpHost, WatchdogLab.SmtpPort, apiUri) : null;
}
