using AdMng.Core.ActiveDirectory;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Core.Messaging.Salutations;
using AdMng.Core.Model;
using AdMng.Testing;
using AdMng.Testing.RichHtml;
using static AdMng.Core.Tests.Messaging.MessageTestData;

namespace AdMng.Core.Tests.Messaging;

/// <summary>
/// U1c × U1d (design note section 22.4): a message written in Outlook with salutation fields that the spell checker split
/// and a logo is pasted (fields repaired, image read once), and the plan renders the title-aware salutations inside the
/// pasted formatting with the logo as an inline image – guesses and the neutral fallback marked as for a text body.
/// </summary>
public sealed class PastedSalutationTemplateTests
{
    private const string ProfessorDn = "CN=Stanisław Kowalczyk,OU=Wykładowcy,DC=contoso,DC=test";
    private const string SaszaDn = "CN=Sasza Kim,OU=Wykładowcy,DC=contoso,DC=test";
    private const string EwaDn = "CN=Ewa Zielińska,OU=Wykładowcy,DC=contoso,DC=test";

    private static readonly UserMessageFields Fields = new();
    private static readonly UserMessagePlanner Planner = new(Fields);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HtmlImportResult> ImportAsync(string html, IReadOnlyList<MessageImage>? existing = null) =>
        PastedHtmlImporter.ImportAsync(
            new HtmlImportRequest
            {
                Document = CfHtmlParser.Parse(CfHtmlFixture.Build(html)),
                Files = new FakePastedImageSource(),
                AllowedLinkDomains = ["contoso.test"],
                Engine = Fields.Engine,
                ExistingImages = existing ?? [],
            },
            Ct);

    private static DirectoryObject Professor() => User(ProfessorDn,
    [
        (Attr.GivenName, "Stanisław"), (Attr.Sn, "Kowalczyk"), (Attr.DisplayName, "Stanisław Kowalczyk"), ("personalTitle", "prof. dr hab."),
        (Attr.Mail, "stanislaw.kowalczyk@contoso.test"), (Attr.SamAccountName, "stanislaw.kowalczyk"),
    ]);

    private static DirectoryObject Sasza() => User(SaszaDn,
    [
        (Attr.GivenName, "Sasza"), (Attr.Sn, "Kim"), (Attr.DisplayName, "Sasza Kim"), (Attr.Mail, "sasza.kim@contoso.test"), (Attr.SamAccountName, "sasza.kim"),
    ]);

    private static DirectoryObject Ewa() => User(EwaDn,
    [
        (Attr.GivenName, "Ewa"), (Attr.Sn, "Zielińska"), (Attr.DisplayName, "dr hab. Ewa Zielińska, prof. UW"), (Attr.Mail, "ewa.zielinska@contoso.test"),
        (Attr.SamAccountName, "ewa.zielinska"), ("preferredLanguage", "en-US"),
    ]);

    /// <summary>The template the composer builds from the two pastes (images of both languages pooled).</summary>
    private static async Task<(MessageTemplate Template, IReadOnlyList<MessageImage> Images, HtmlImportResult Polish)> PastedTemplateAsync()
    {
        var polish = await ImportAsync(OutlookSalutationSample.Polish);
        var english = await ImportAsync(OutlookSalutationSample.English, polish.Images);
        var images = polish.Images.Concat(english.Images).DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase).ToList();
        var template = new MessageTemplate
        {
            Id = "wklejony-zwrot",
            Name = "Hasło – wklejone z Outlooka",
            Polish = new MessageTemplateText { Subject = "Zmiana hasła – {$salutation:vocativeNoTitle}", Body = string.Empty, Html = polish.Html },
            English = new MessageTemplateText { Subject = "Password change", Body = string.Empty, Html = english.Html },
            Images = [.. images.Select(MessageTemplateImage.From)],
        };
        return (template, images, polish);
    }

    private static UserMessagePlanRequest Request(MessageTemplate template, IReadOnlyList<MessageImage> images) => new()
    {
        Template = template,
        Sender = Sender("contoso.test"),
        Accounts = [new UserMessageAccount(ProfessorDn, Professor()), new UserMessageAccount(SaszaDn, Sasza()), new UserMessageAccount(EwaDn, Ewa())],
        Now = Now,
        Domain = "contoso.test",
        Images = images.ToDictionary(i => i.ContentId, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public async Task ThePaste_RepairsTheSalutationFields_AndEmbedsTheLogo()
    {
        var (template, images, polish) = await PastedTemplateAsync();

        // Every split field is one field again: spell-checker and language spans and a second run of the same style go with
        // the cleanup; a field with a highlight on a part is repaired into the formatting of its longest piece.
        foreach (var field in new[] { "{$salutation}", "{$honorific}", "{$byGender:zmienił/zmieniła}", "{$titledName}", "{sAMAccountName}" })
        {
            Assert.Contains(field, polish.Html, StringComparison.Ordinal);
        }

        Assert.Equal(
            ["{$honorific}"],
            polish.Problems.Where(p => p.Kind == HtmlImportProblemKind.PlaceholderRepaired).Select(p => p.Detail));
        Assert.Contains($"color:{OutlookSalutationSample.SalutationColor}", polish.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<span style=\"font-weight:bold;color:#1F3864\">{$salutation}</span>,", polish.Html, StringComparison.Ordinal);
        Assert.Contains("<span style=\"background-color:yellow\">{$honorific}</span>", polish.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("SpellE", polish.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("mso-", polish.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("file:", polish.Html, StringComparison.Ordinal);

        // One logo for both languages: the VML fallback image, stripped of its metadata, referenced by cid:.
        var logo = Assert.Single(images);
        Assert.Equal("clip_image002.png", logo.FileName);
        Assert.Equal(MessageImageSamples.Png(12, 4, seed: 0x02), logo.Data);
        Assert.Contains($"src=\"cid:{logo.ContentId}\"", polish.Html, StringComparison.Ordinal);
        Assert.Contains($"src=\"cid:{logo.ContentId}\"", template.English!.Html, StringComparison.Ordinal);

        // The template is valid: the salutation fields are known to the HTML check and the planner.
        Assert.DoesNotContain(Planner.CheckTemplate(template), i => i.Severity == MessageIssueSeverity.Error);
        Assert.Contains("personalTitle", Planner.AccountAttributesFor(template, SalutationOptions.Default));
    }

    [Fact]
    public async Task ThePlan_RendersTheSalutationsInsideThePastedFormatting_WithTheInlineLogo()
    {
        var (template, images, _) = await PastedTemplateAsync();
        var logo = Assert.Single(images);

        var plan = Planner.Plan(Request(template, images));

        Assert.False(plan.IsBlocked);
        var professor = plan.Messages[0];
        Assert.Equal(PlannedMessageStatus.Ready, professor.Status);
        var message = professor.Message!;
        Assert.Equal("Zmiana hasła – Panie", message.Subject);
        Assert.Contains("Szanowny Panie Profesorze</span>,", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains($"color:{OutlookSalutationSample.SalutationColor}", message.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "uprzejmie prosimy, aby <span style=\"background-color:yellow\">Pan</span> zmienił hasło do konta <b><span style=\"color:#C00000\">stanislaw.kowalczyk</span></b>",
            message.HtmlBody,
            StringComparison.Ordinal);
        Assert.Contains("Adresat: prof. dr hab. Stanisław Kowalczyk</p>", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("{$", message.HtmlBody, StringComparison.Ordinal);
        Assert.Equal([logo.ContentId], message.InlineImages.Select(i => i.ContentId));
        Assert.Contains($"src=\"cid:{logo.ContentId}\"", message.HtmlBody, StringComparison.Ordinal);

        // The text part comes from the rendered HTML; the preview marks the guessed gender.
        Assert.StartsWith("Szanowny Panie Profesorze,", message.TextBody, StringComparison.Ordinal);
        Assert.Contains(professor.Derived, d => d is { Field: "salutation", Value: "Szanowny Panie Profesorze", IsGuess: true });
        Assert.Contains(new MessageIssue(SalutationIssueCodes.GenderGuessed, MessageIssueSeverity.Info, "Stanisław"), professor.Issues);

        // Unknown gender: the neutral salutation inside the same formatting, with a warning.
        var sasza = plan.Messages[1];
        Assert.Equal(PlannedMessageStatus.Warning, sasza.Status);
        Assert.Contains("Szanowna Pani / Szanowny Panie</span>,", sasza.Message!.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("aby <span style=\"background-color:yellow\">Pan/Pani</span> zmienił/zmieniła hasło", sasza.Message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(new MessageIssue(SalutationIssueCodes.GenderUnknown, MessageIssueSeverity.Warning, "Sasza Kim"), sasza.Issues);
        Assert.Contains(sasza.Derived, d => d is { Field: "salutation", Source: DerivedValueSource.Fallback });
        Assert.Single(sasza.Message.InlineImages);

        // English recipient: the English paste, the title from the display name, the same logo.
        var ewa = plan.Messages[2];
        Assert.Equal(MessageLanguage.English, ewa.Language);
        Assert.Contains("Dear Professor Zielińska</span>,", ewa.Message!.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("Dear Professor Zielińska,", ewa.Message.TextBody, StringComparison.Ordinal);
        Assert.Equal([logo.ContentId], ewa.Message.InlineImages.Select(i => i.ContentId));
    }

    [Fact]
    public async Task AGenderOverride_RePlansThePastedMessage_KeepingTheLogo()
    {
        var (template, images, _) = await PastedTemplateAsync();
        var request = Request(template, images);

        var replanned = Planner.Replan(request with { GenderOverrides = new Dictionary<string, Gender> { [SaszaDn] = Gender.Feminine } }, 1);

        Assert.Equal(PlannedMessageStatus.Ready, replanned.Status);
        Assert.Contains("Szanowna Pani</span>,", replanned.Message!.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("aby <span style=\"background-color:yellow\">Pani</span> zmieniła hasło", replanned.Message.HtmlBody, StringComparison.Ordinal);
        Assert.Single(replanned.Message.InlineImages);

        // "Send without images" keeps the salutations and puts the logo's alt text in its place.
        var omitted = Planner.Replan(request with { OmitImages = true }, 0);
        Assert.Empty(omitted.Message!.InlineImages);
        Assert.Contains("Szanowny Panie Profesorze</span>,", omitted.Message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("[Logo – Dział IT]", omitted.Message.HtmlBody, StringComparison.Ordinal);
    }
}
