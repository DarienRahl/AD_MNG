using System.Text;
using AdMng.Core.Messaging;
using MimeKit;
using static AdMng.Mail.Tests.MailTestData;

namespace AdMng.Mail.Tests;

/// <summary>MIME of a message: UTF-8 / RFC 2047, headers, no attachments, no header injection.</summary>
public class UserMimeMessageBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

    private static (MimeMessage Parsed, string Raw) RoundTrip(OutgoingUserMessage message)
    {
        var mime = UserMimeMessageBuilder.Build(message, "admng.contoso.test", Now);
        using var stream = new MemoryStream();
        mime.WriteTo(FormatOptions.Default, stream);
        var raw = Encoding.UTF8.GetString(stream.ToArray());
        stream.Position = 0;
        return (MimeMessage.Load(stream), raw);
    }

    [Fact]
    public void PolishSubjectAndBody_AreEncoded_AndSurviveTheRoundTrip()
    {
        var message = Message(cc: "helpdesk@contoso.test");

        var (parsed, raw) = RoundTrip(message);

        Assert.Contains("Subject: =?utf-8?", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zażółć", raw.Split("\r\n\r\n")[0], StringComparison.Ordinal);
        Assert.Equal(PolishSubject, parsed.Subject);
        Assert.Equal(PolishBody.ReplaceLineEndings("\n"), parsed.TextBody!.ReplaceLineEndings("\n"));
        Assert.Contains("ZAŻÓŁĆ GĘŚLĄ JAŹŃ", parsed.HtmlBody, StringComparison.Ordinal);
        Assert.Equal("Dział IT", parsed.From.Mailboxes.Single().Name);
        Assert.Equal("lukasz.wisniewski@contoso.test", parsed.To.Mailboxes.Single().Address);
        Assert.Equal("Łukasz Wiśniewski", parsed.To.Mailboxes.Single().Name);
        Assert.Equal("helpdesk@contoso.test", parsed.Cc.Mailboxes.Single().Address);
        Assert.Equal("helpdesk@contoso.test", parsed.ReplyTo.Mailboxes.Single().Address);
        Assert.Equal($"{message.Id:N}@admng.contoso.test", parsed.MessageId);
        Assert.Equal(Now, parsed.Date);
    }

    [Fact]
    public void Headers_MarkTheMessageAsAutomatic_AndThereAreNoAttachments()
    {
        var (parsed, _) = RoundTrip(Message());

        Assert.Equal("auto-generated", parsed.Headers["Auto-Submitted"]);
        Assert.Equal("All", parsed.Headers["X-Auto-Response-Suppress"]);
        Assert.Equal(UserMimeMessageBuilder.MessageKind, parsed.Headers["X-AdMng-Message"]);
        var alternative = Assert.IsType<MultipartAlternative>(parsed.Body);
        Assert.Equal(["text/plain", "text/html"], alternative.Select(p => p.ContentType.MimeType));
        Assert.All(alternative.OfType<TextPart>(), p => Assert.Equal("utf-8", p.ContentType.Charset, ignoreCase: true));
        Assert.Empty(parsed.Attachments);
    }

    [Fact]
    public void CrLfInTheSubjectOrADisplayName_NeverCreatesAHeader()
    {
        var message = Message(subject: "Hasło\r\nBcc: ofiara@example.com") with
        {
            To = [new MessageAddress("lukasz.wisniewski@contoso.test", "Łukasz\r\nBcc: ofiara@example.com")],
        };

        var (parsed, raw) = RoundTrip(message);

        Assert.DoesNotContain("\r\nBcc:", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(parsed.Bcc);
        Assert.Equal("Hasło  Bcc: ofiara@example.com", parsed.Subject);
        Assert.Single(parsed.To.Mailboxes);
    }

    [Fact]
    public void InvalidAddresses_AreRejected()
    {
        Assert.Throws<FormatException>(() => UserMimeMessageBuilder.Build(Message(to: "a@b.test, c@d.test"), "x", Now));
        Assert.Throws<FormatException>(() => UserMimeMessageBuilder.Build(Message(to: "a@b.test\r\nBcc: c@d.test"), "x", Now));
    }
}
