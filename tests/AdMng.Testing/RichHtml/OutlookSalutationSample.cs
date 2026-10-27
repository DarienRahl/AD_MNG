using AdMng.Core.ActiveDirectory;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Core.Model;

namespace AdMng.Testing.RichHtml;

/// <summary>
/// A message written in classic Outlook with the salutation fields of U1d and a logo, as Outlook puts it on the clipboard
/// (the U1c / U1d joint test, design note section 22.4): the class rules of the <c>&lt;style&gt;</c> block, fields split by
/// the spell checker (<c>span.SpellE</c>, <c>span.GramE</c>), by a language change, by two runs of one style and by a
/// highlight on part of a field, the VML shape with its <c>&lt;![if !vml]&gt;</c> fallback image <c>clip_image002.png</c> (in <see cref="FakePastedImageSource"/>) and the
/// <c>o:p</c> elements. <see cref="Polish"/> and <see cref="English"/> are the two language versions of one template.
/// </summary>
public static class OutlookSalutationSample
{
    /// <summary>The colour of the bold salutation line (a class rule Outlook writes as <c>span.Zwrot</c>).</summary>
    public const string SalutationColor = "#1F3864";

    /// <summary>The professor of <see cref="PlanForProfessorAsync"/>.</summary>
    public const string ProfessorDn = "CN=Stanisław Kowalczyk,OU=Wykładowcy,DC=contoso,DC=test";

    /// <summary>What the professor's message says: the salutation inside the pasted bold, coloured run.</summary>
    public const string ProfessorSalutationHtml = "<span style=\"font-weight:bold;color:" + SalutationColor + "\">Szanowny Panie Profesorze</span>,";

    /// <summary>The Polish version: <c>{$salutation}</c>, <c>{$honorific}</c>, <c>{$byGender:…}</c>, <c>{$titledName}</c>.</summary>
    public static string Polish => Document(
        "PL",
        "<p class=MsoNormal><span class=Zwrot>{</span><span class=Zwrot>$<span class=SpellE>salutation</span>}</span>,<o:p></o:p></p>\r\n"
        + "<p class=MsoNormal><o:p>&nbsp;</o:p></p>\r\n"
        + "<p class=MsoNormal>uprzejmie prosimy, aby {$<span style='background:yellow;mso-highlight:yellow'>honorific</span>} {$byGender:<span class=SpellE>zmienił</span>/<span class=GramE>zmieniła</span>} hasło do konta <b><span style='color:#C00000'>{<span class=SpellE>sAMAccountName</span>}</span></b> jeszcze dziś.<o:p></o:p></p>\r\n"
        + "<p class=MsoNormal>Adresat: <span lang=EN-US>{$<span class=SpellE>titledName</span>}</span><o:p></o:p></p>\r\n");

    /// <summary>The English version with the same logo.</summary>
    public static string English => Document(
        "EN-US",
        "<p class=MsoNormal><span class=Zwrot>{$<span class=SpellE>salutation</span>}</span>,<o:p></o:p></p>\r\n"
        + "<p class=MsoNormal><o:p>&nbsp;</o:p></p>\r\n"
        + "<p class=MsoNormal>please change the password of <b><span style='color:#C00000'>{<span class=SpellE>sAMAccountName</span>}</span></b> today.<o:p></o:p></p>\r\n");

    /// <summary>
    /// The message both transports get for <see cref="Polish"/>: pasted (fields repaired, the logo read once by
    /// <see cref="FakePastedImageSource"/>) and planned for prof. dr hab. Stanisław Kowalczyk (personalTitle) on Monday
    /// 2026-10-26, with the inline logo.
    /// </summary>
    public static async Task<(OutgoingUserMessage Message, MessageImage Logo)> PlanForProfessorAsync(CancellationToken cancellationToken)
    {
        var fields = new UserMessageFields();
        var pasted = await PastedHtmlImporter.ImportAsync(
            new HtmlImportRequest
            {
                Document = CfHtmlParser.Parse(CfHtmlFixture.Build(Polish)),
                Files = new FakePastedImageSource(),
                AllowedLinkDomains = ["contoso.test"],
                Engine = fields.Engine,
            },
            cancellationToken).ConfigureAwait(false);
        var logo = pasted.Images.Single();
        var template = new MessageTemplate
        {
            Id = "wklejony-zwrot",
            Name = "Hasło – wklejone z Outlooka",
            Polish = new MessageTemplateText { Subject = "Zmiana hasła – {$salutation:vocativeNoTitle}", Body = string.Empty, Html = pasted.Html },
            Images = [MessageTemplateImage.From(logo)],
        };
        string[] Values(string value) => [value];
        var professor = new DirectoryObject(ProfessorDn,
        [
            new AttributeValues(Attr.ObjectClass, ["top", "person", "organizationalPerson", "user"]),
            new AttributeValues(Attr.GivenName, Values("Stanisław")),
            new AttributeValues(Attr.Sn, Values("Kowalczyk")),
            new AttributeValues(Attr.DisplayName, Values("Stanisław Kowalczyk")),
            new AttributeValues("personalTitle", Values("prof. dr hab.")),
            new AttributeValues(Attr.Mail, Values("stanislaw.kowalczyk@contoso.test")),
            new AttributeValues(Attr.SamAccountName, Values("stanislaw.kowalczyk")),
        ]);
        var plan = new UserMessagePlanner(fields).Plan(new UserMessagePlanRequest
        {
            Template = template,
            Sender = new UserMessageSender
            {
                From = new MessageAddress("helpdesk@contoso.test", "Dział IT"),
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"),
            },
            Accounts = [new UserMessageAccount(ProfessorDn, professor)],
            Now = new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero),
            Domain = "contoso.test",
            Images = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase) { [logo.ContentId] = logo },
        });
        var planned = plan.Messages.Single();
        return (planned.Message ?? throw new InvalidOperationException(string.Join("; ", plan.TemplateIssues.Concat(planned.Issues))), logo);
    }

    private static string Document(string language, string paragraphs) =>
        "<html xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:w=\"urn:schemas-microsoft-com:office:word\" xmlns:m=\"http://schemas.microsoft.com/office/2004/12/omml\" xmlns=\"http://www.w3.org/TR/REC-html40\">\r\n"
        + "<head><meta http-equiv=Content-Type content=\"text/html; charset=utf-8\"><meta name=ProgId content=Word.Document><meta name=Generator content=\"Microsoft Word 15\"><meta name=Originator content=\"Microsoft Word 15\">\r\n"
        + "<style><!--\r\n"
        + "p.MsoNormal, li.MsoNormal, div.MsoNormal\r\n\t{margin:0cm;\r\n\tfont-size:11.0pt;\r\n\tfont-family:\"Aptos\",sans-serif;\r\n\tmso-ligatures:standardcontextual;\r\n\tmso-fareast-language:EN-US;}\r\n"
        + "span.Zwrot\r\n\t{mso-style-name:Zwrot;\r\n\tfont-weight:bold;\r\n\tcolor:" + SalutationColor + ";}\r\n"
        + "span.SpellE\r\n\t{mso-style-name:\"\";\r\n\tmso-spl-e:yes;}\r\n"
        + "span.GramE\r\n\t{mso-style-name:\"\";\r\n\tmso-gram-e:yes;}\r\n"
        + "--></style>\r\n"
        + "</head>\r\n"
        + "<body lang=" + language + " link=\"#467886\" vlink=\"#96607D\" style='word-wrap:break-word'>\r\n"
        + "<!--StartFragment-->" + paragraphs
        + "<p class=MsoNormal><o:p>&nbsp;</o:p></p>\r\n"
        + "<p class=MsoNormal><span style='mso-no-proof:yes'><!--[if gte vml 1]><v:shape id=\"Obraz_x0020_1\" o:spid=\"_x0000_i1025\" type=\"#_x0000_t75\" style='width:90pt;height:30pt;visibility:visible;mso-wrap-style:square'>\r\n"
        + " <v:imagedata src=\"file:///C:/Users/JKOWAL~1/AppData/Local/Temp/msohtmlclip1/01/clip_image001.png\" o:title=\"Logo\"/>\r\n"
        + "</v:shape><![endif]--><![if !vml]><img width=120 height=40 style='width:1.25in;height:.4166in' src=\"file:///C:/Users/JKOWAL~1/AppData/Local/Temp/msohtmlclip1/01/clip_image002.png\" alt=\"Logo – Dział IT\" v:shapes=\"Obraz_x0020_1\"><![endif]></span><o:p></o:p></p>\r\n"
        + "<p class=MsoNormal><span style='font-size:10.0pt;color:#595959'>Zespół Wsparcia IT – Żółć i Gęś sp. z o.o.<o:p></o:p></span></p>\r\n"
        + "<!--EndFragment-->\r\n"
        + "</body>\r\n"
        + "</html>";
}
