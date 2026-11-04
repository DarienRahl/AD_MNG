using AdMng.Core.ActiveDirectory;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;
using static AdMng.Core.Tests.Messaging.MessageTestData;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>Templates with inline images through the planner: references, missing images, limits, "send without images",
/// the text part from the HTML, the HTML length, cid: not a link and the post-render validation.</summary>
public class UserMessageImagesPlannerTests
{
    private static readonly UserMessagePlanner Planner = new(new UserMessageFields());

    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(12, 4, seed: 0x21), "logo-dzialu.png");
    private static readonly MessageImage Screenshot = MessageImage.Create(MessageImageSamples.Jpeg(64, 48), "zrzut.jpg");

    private static MessageTemplate Template(string html, string body = "", params MessageImage[] images) => new()
    {
        Id = "t1",
        Name = "Hasło z logo",
        Polish = new MessageTemplateText { Subject = "Hasło wygasa za {$passwordExpiresInDays} dni", Body = body, Html = html },
        Images = [.. images.Select(MessageTemplateImage.From)],
    };

    private static UserMessagePlanRequest Request(MessageTemplate template, IEnumerable<MessageImage>? images = null, bool omit = false) => new()
    {
        Template = template,
        Sender = Sender("contoso.test"),
        Accounts = [new UserMessageAccount(LukaszDn, Lukasz(), Jan())],
        Now = Now,
        Domain = "contoso.test",
        Images = (images ?? []).ToDictionary(i => i.ContentId, StringComparer.OrdinalIgnoreCase),
        OmitImages = omit,
    };

    [Fact]
    public void ReferencedImages_TravelWithTheMessage_InOrderOfFirstReference()
    {
        var html = $"<p>Dzień dobry {{givenName}},</p>\n<p><img src=\"cid:{Screenshot.ContentId}\" alt=\"Zrzut\"> <img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"> <img src=\"cid:{Screenshot.ContentId}\" alt=\"Zrzut\"></p>";
        var plan = Planner.Plan(Request(Template(html, images: [Logo, Screenshot]), [Logo, Screenshot]));
        Assert.False(plan.IsBlocked);
        var message = Assert.Single(plan.Messages).Message!;
        Assert.Equal([Screenshot.ContentId, Logo.ContentId], message.InlineImages.Select(i => i.ContentId));
        Assert.Contains($"src=\"cid:{Logo.ContentId}\"", message.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(PlannedMessageStatus.Ready, plan.Messages[0].Status);
    }

    [Fact]
    public void CidImages_AreNotLinks()
    {
        Assert.Empty(UserMessageRules.FindLinks($"<img src=\"cid:{Logo.ContentId}\">"));
        var plan = Planner.Plan(Request(Template($"<p><img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"></p>", images: [Logo]), [Logo]));
        Assert.DoesNotContain(plan.Messages[0].Issues, i => i.Code == MessageIssueCodes.LinkNotAllowed);
    }

    [Fact]
    public void AnUnknownCid_IsATemplateError()
    {
        var issues = Planner.CheckTemplate(Template("<p><img src=\"cid:img-ffffffffffffffff@admng\" alt=\"x\"></p>"));
        Assert.Contains(issues, i => i.Code == HtmlIssueCodes.ImageMissing && i.Severity == MessageIssueSeverity.Error && i.Argument == "img-ffffffffffffffff@admng");
    }

    [Fact]
    public void ImageBytesMissingAtPlanTime_AreAMessageError()
    {
        // The template knows the image, the composer could not load its bytes (store file deleted).
        var plan = Planner.Plan(Request(Template($"<p><img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"></p>", images: [Logo]), images: []));
        Assert.Contains(plan.Messages[0].Issues, i => i.Code == HtmlIssueCodes.ImageMissing && i.Severity == MessageIssueSeverity.Error);
        Assert.False(plan.Messages[0].CanSend);
    }

    [Theory]
    [InlineData("<p><img src=\"{$input:obraz}\" alt=\"x\"></p>")]
    [InlineData("<p><img src=\"https://tracker.example/p.gif\"></p>")]
    [InlineData("<p style=\"position:fixed\">nakładka</p>")]
    [InlineData("<p style=\"margin-top:-200px\">na stopkę</p>")]
    [InlineData("<div style=\"display:none\">ukryte</div>")]
    public void SecurityFindings_AreHtmlNotAllowed(string html) =>
        Assert.Contains(Planner.CheckTemplate(Template(html)), i => i.Code == MessageIssueCodes.HtmlNotAllowed && i.Severity == MessageIssueSeverity.Error);

    [Fact]
    public void HtmlNestedTooDeeply_IsATemplateError_AndEditsLeaveItAlone()
    {
        var html = SmallStack.Nested(5_000);
        var issues = SmallStack.Run(() => Planner.CheckTemplate(Template(html)));
        Assert.Contains(issues, i => i.Code == HtmlIssueCodes.HtmlTooDeep && i.Severity == MessageIssueSeverity.Error && i.Argument == "256");
        Assert.Same(html, SmallStack.Run(() => MessageHtmlChecks.RemoveLinks(html, "contoso.pl")));
        Assert.Same(html, SmallStack.Run(() => MessageHtmlChecks.Normalize(Template(html)).Polish.Html));
    }

    [Fact]
    public void FieldLinks_AreAllowedInTemplates()
    {
        var issues = Planner.CheckTemplate(Template("<p><a href=\"{$input:link}\">formularz</a> i <a href=\"https://portal.contoso.test/{sAMAccountName}\">portal</a></p>"));
        Assert.DoesNotContain(issues, i => i.Severity == MessageIssueSeverity.Error);
    }

    [Fact]
    public void TooManyImagesInAMessage_IsAnError()
    {
        var images = Enumerable.Range(1, MessageImageLimits.MaxImagesPerMessage + 1).Select(i => MessageImage.Create(MessageImageSamples.Png(2, 2, seed: (byte)i))).ToList();
        var html = string.Concat(images.Select(i => $"<img src=\"cid:{i.ContentId}\" alt=\"{i.FileName}\">"));
        var plan = Planner.Plan(Request(Template("<p>" + html + "</p>", images: [.. images]), images));
        Assert.Contains(plan.Messages[0].Issues, i => i.Code == HtmlIssueCodes.ImagesTooLarge && i.Severity == MessageIssueSeverity.Error);
    }

    [Fact]
    public void TooManyImagesInATemplate_IsATemplateError()
    {
        var images = Enumerable.Range(1, MessageImageLimits.MaxImagesPerTemplate + 1).Select(i => MessageImage.Create(MessageImageSamples.Png(2, 2, seed: (byte)i))).ToArray();
        Assert.Contains(Planner.CheckTemplate(Template("<p>x</p>", images: images)), i => i.Code == HtmlIssueCodes.ImagesTooLarge);
    }

    [Fact]
    public void SendWithoutImages_ReplacesThemByTheirAltText()
    {
        var html = $"<p>Logo: <img src=\"cid:{Logo.ContentId}\" alt=\"Logo – Dział IT\"> <img src=\"cid:{Screenshot.ContentId}\"></p>";
        var plan = Planner.Plan(Request(Template(html, images: [Logo, Screenshot]), [Logo, Screenshot], omit: true));
        var planned = Assert.Single(plan.Messages);
        Assert.Empty(planned.Message!.InlineImages);
        Assert.DoesNotContain("cid:", planned.Message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Logo: [Logo – Dział IT]", planned.Message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(planned.Issues, i => i.Code == HtmlIssueCodes.ImagesOmitted && i.Severity == MessageIssueSeverity.Info);
        Assert.Equal(PlannedMessageStatus.Ready, planned.Status);
    }

    [Fact]
    public void AnEmptyTextBody_IsGeneratedFromTheHtml()
    {
        var plan = Planner.Plan(Request(Template("<p>Dzień dobry <b>{givenName}</b>,</p><ul><li>zmień hasło w ciągu {$passwordExpiresInDays} dni</li></ul>")));
        var text = plan.Messages[0].Message!.TextBody;
        Assert.StartsWith("Dzień dobry Łukasz,\n\n- zmień hasło w ciągu 10 dni\n\n-- \n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOperatorTextBody_IsKept()
    {
        var plan = Planner.Plan(Request(Template("<p>HTML {givenName}</p>", body: "Tekst {givenName}")));
        Assert.StartsWith("Tekst Łukasz\n", plan.Messages[0].Message!.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LongHtml_IsAnError()
    {
        var html = "<p>" + new string('ą', MessageHtmlLimits.MaxHtmlLength + 1) + "</p>";
        Assert.Contains(Planner.CheckTemplate(Template(html)), i => i.Code == HtmlIssueCodes.HtmlTooLong);
    }

    [Fact]
    public void ALinkTextNamingAnotherHost_IsATemplateWarning()
    {
        var issues = Planner.CheckTemplate(Template("<p><a href=\"https://evil.example/\">haslo.contoso.test</a></p>"));
        Assert.Contains(issues, i => i.Code == HtmlIssueCodes.LinkTextMismatch && i.Severity == MessageIssueSeverity.Warning && i.Argument == "evil.example");
    }

    [Fact]
    public void HandWrittenHtml_GetsTheNormalizationNote_SanitizedHtmlDoesNot()
    {
        Assert.Contains(Planner.CheckTemplate(Template("<P CLASS=x>a</P>")), i => i.Code == HtmlIssueCodes.HtmlNormalized && i.Severity == MessageIssueSeverity.Info);
        Assert.DoesNotContain(Planner.CheckTemplate(Template("<p>a</p>\n")), i => i.Code == HtmlIssueCodes.HtmlNormalized);
    }

    [Fact]
    public void Normalize_SanitizesAndDropsUnreferencedImages()
    {
        var template = Template($"<P class=MsoNormal>Logo <img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"></P>", images: [Logo, Screenshot]);
        var normalized = MessageHtmlChecks.Normalize(template);
        Assert.Equal($"<p>Logo <img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"></p>\n", normalized.Polish.Html);
        Assert.Equal([Logo.ContentId], normalized.Images.Select(i => i.ContentId));
        Assert.Null(normalized.English.Html);
    }

    [Fact]
    public void Normalize_KeepsHtmlWithASecurityFinding_AsItIs()
    {
        var template = Template("<P onclick=\"x()\">Kliknij</P>");
        Assert.Equal("<P onclick=\"x()\">Kliknij</P>", MessageHtmlChecks.Normalize(template).Polish.Html);
    }

    [Fact]
    public void RemoveImage_AndRemoveLinks_ChangeOnlyTheirTargets()
    {
        var html = $"<p>Logo <img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"> i <img src=\"CID:{Screenshot.ContentId}\" alt=\"Zrzut\"></p>\n";
        Assert.Equal($"<p>Logo  i <img src=\"CID:{Screenshot.ContentId}\" alt=\"Zrzut\"></p>\n", MessageHtmlChecks.RemoveImage(html, Logo.ContentId.ToUpperInvariant()));
        Assert.Same(html, MessageHtmlChecks.RemoveImage(html, "img-0000000000000000@admng"));

        var links = "<p><a href=\"https://haslo.contoso.pl/zmien\">zmień hasło</a>, <a href=\"https://www.portal.contoso.pl/\">portal</a>, <a href=\"https://contoso.test/\">intranet</a></p>\n";
        Assert.Equal("<p>zmień hasło, portal, <a href=\"https://contoso.test/\">intranet</a></p>\n", MessageHtmlChecks.RemoveLinks(links, "contoso.pl"));
        var document = MessageHtmlChecks.RemoveLinks("<html><body><p><a href=\"https://evil.example/\">a</a> b</p></body></html>", "evil.example");
        Assert.StartsWith("<!DOCTYPE html>", document, StringComparison.Ordinal);
        Assert.Contains("<p>a b</p>", document, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRenderedDocument_IsValidatedAgain()
    {
        // A field value never becomes markup (it is HTML-encoded): the rendered document has no findings.
        var plan = Planner.Plan(Request(Template("<p>{displayName}</p>")) with
        {
            Accounts = [new UserMessageAccount(LukaszDn, Lukasz((Attr.DisplayName, "<img src=x onerror=alert(1)>")), Jan())],
        });
        Assert.DoesNotContain(plan.Messages[0].Issues, i => i.Code == MessageIssueCodes.HtmlNotAllowed);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", plan.Messages[0].Message!.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSentDocument_IsTheSanitizedOne_WhateverTheTransport()
    {
        // Comments and style rules the inliner skips were only "cosmetic" findings: sent as they were, Outlook classic showed
        // the conditional comment and clients that honour <style> hid the warning.
        // (Braces of a template are doubled.)
        const string html = "<style>@media screen {{ .ostrzezenie {{ display:none }} }} p::after {{ content: ' Zadzwoń: +48 800 000 000' }} div > p {{ position:fixed; top:0 }}</style>"
            + "<!--[if mso]><p>Hasło podaj telefonicznie pod numerem +48 800 000 000.</p><![endif]-->"
            + "<div><p class=\"ostrzezenie\">Nigdy nie podawaj hasła, {givenName}.</p></div>";
        var plan = Planner.Plan(Request(Template(html)));
        Assert.False(plan.IsBlocked);
        var planned = Assert.Single(plan.Messages);
        Assert.DoesNotContain(planned.Issues, i => i.Severity == MessageIssueSeverity.Error);
        var sent = planned.Message!.HtmlBody!;
        Assert.Contains("Nigdy nie podawaj hasła, Łukasz.", sent, StringComparison.Ordinal);
        foreach (var hidden in new[] { "<style", "Hasło podaj", "[if mso]", "position", "content", "ostrzezenie", "display" })
        {
            Assert.DoesNotContain(hidden, sent, StringComparison.Ordinal);
        }

        Assert.StartsWith("<!DOCTYPE html>", sent, StringComparison.Ordinal);
        Assert.Contains("Nigdy nie prosimy o podanie hasła", sent, StringComparison.Ordinal);
        Assert.Equal(sent, MessageHtmlSanitizer.Sanitize(sent, new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Document }).Html);
    }

    [Fact]
    public void FullDocumentTemplates_AreCheckedInDocumentMode()
    {
        var template = Template("<html><body style=\"font-family:Arial\"><p>{givenName}</p></body></html>");
        Assert.Equal(HtmlSanitizeMode.Document, MessageHtmlChecks.ModeOf(template.Polish.Html!));
        Assert.DoesNotContain(Planner.CheckTemplate(template), i => i.Severity == MessageIssueSeverity.Error);
    }

    [Fact]
    public void TemplateImageMetadata_IsChecked()
    {
        var valid = MessageTemplateImage.From(Logo);
        Assert.True(valid.IsValid);
        Assert.False((valid with { ContentId = "img-0000000000000000@admng" }).IsValid);
        Assert.False((valid with { MediaType = "image/svg+xml" }).IsValid);
        Assert.Equal($"MessageTemplateImage({Logo.ContentId})", valid.ToString());
    }
}
