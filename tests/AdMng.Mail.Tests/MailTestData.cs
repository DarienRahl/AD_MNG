using AdMng.Core.Messaging;
using AdMng.Watchdog.Testing.Certificates;

namespace AdMng.Mail.Tests;

internal static class MailTestData
{
    public const string PolishSubject = "Hasło do konta łukasz.wiśniewski wygaśnie 5 listopada 2026 – zażółć gęślą jaźń";

    public const string PolishBody = "Dzień dobry Łukasz,\n\nhasło do Twojego konta wygaśnie 5 listopada 2026 (pozostało dni: 10).\nZAŻÓŁĆ GĘŚLĄ JAŹŃ\n";

    public static OutgoingUserMessage Message(string to = "lukasz.wisniewski@contoso.test", string? cc = null, string subject = PolishSubject) => new()
    {
        Id = Guid.NewGuid(),
        From = new MessageAddress("noreply@contoso.test", "Dział IT"),
        To = [new MessageAddress(to, "Łukasz Wiśniewski")],
        Cc = cc is null ? [] : [new MessageAddress(cc, "Pomoc techniczna")],
        ReplyTo = new MessageAddress("helpdesk@contoso.test", "Pomoc techniczna"),
        Subject = subject,
        TextBody = PolishBody,
        HtmlBody = "<!DOCTYPE html><html lang=\"pl\"><body><p>Dzień dobry Łukasz,</p><p>ZAŻÓŁĆ GĘŚLĄ JAŹŃ</p></body></html>",
        Language = MessageLanguage.Polish,
    };

    /// <summary>STARTTLS required, the pinned test certificate of 127.0.0.1, no authentication.</summary>
    public static DirectSmtpOptions Options(int port, SmtpTlsMode tls = SmtpTlsMode.StartTlsRequired, SmtpAuthentication auth = SmtpAuthentication.None) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Tls = tls,
        Certificate = SmtpCertificateCheck.PinnedSha256,
        PinnedSha256 = TestCertificates.Sha256(TestCertificates.Localhost),
        Authentication = auth,
        UserName = auth == SmtpAuthentication.Basic ? "helpdesk" : null,
        LocalDomain = "admng.contoso.test",
        Timeout = TimeSpan.FromSeconds(10),
    };
}
