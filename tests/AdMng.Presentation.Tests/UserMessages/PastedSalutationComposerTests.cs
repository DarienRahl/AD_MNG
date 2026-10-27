using AdMng.Core.Audit;
using AdMng.Core.Csv;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using AdMng.Testing.RichHtml;
using static AdMng.Presentation.Tests.UserMessages.MessageScenario;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// U1c × U1d in the message window (design note section 22.4): an Outlook message with salutation fields and a logo is
/// pasted into the template's HTML body (both languages), the preview shows the title-aware salutations inside the pasted
/// formatting with their marks and the logo, a gender chosen in the preview re-plans that message with its logo, and the
/// messages leave through direct SMTP and through AdMng Watchdog with the salutation and the inline logo.
/// </summary>
public sealed class PastedSalutationComposerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(MessageScenario Scenario, SalutationSample Sample, UserMessageComposerViewModel Composer)> PastedAsync(UserMessageTransportChoice transport)
    {
        var scenario = await CreateAsync();
        var sample = scenario.Harness.Builder.AddSalutationSamples(scenario.Company);
        scenario.Watchdog.UserMessagingStatus = scenario.Watchdog.UserMessagingStatus with { Enabled = true };
        await scenario.Harness.Settings.UpdateAsync(s => s.UserMessages.Transport = transport, Ct);
        var harness = scenario.Harness;
        var services = new UserMessageServices(
            new UserMessageTemplateLibrary(harness.Settings, harness.Localizer),
            scenario.Services.Transports,
            new CsvCodec(),
            harness.AuditLog,
            harness.Time,
            (delay, token) =>
            {
                harness.Time.Advance(delay);
                return Task.CompletedTask;
            },
            pastedImages: new FakePastedImageSource());
        var factory = new DialogViewModelFactory(
            harness.Services, harness.Engines, harness.Views, harness.AuditLog, new AuditLogExporter(new CsvCodec()), harness.PowerShell,
            harness.Theme, messages: scenario.Launcher, userMessages: services);
        var composer = factory.CreateUserMessageComposer(new UserMessageRequest([sample.Professor, sample.SaszaKim, sample.Emily], BuiltInMessageTemplates.Custom));
        await composer.InitializeAsync();

        // Both languages pasted from Outlook (an empty text body is filled from the HTML); the English paste reuses the
        // logo of the Polish one.
        foreach (var (language, subject, html) in new[]
        {
            (MessageLanguage.Polish, "Zmiana hasła – {$salutation:vocativeNoTitle}", OutlookSalutationSample.Polish),
            (MessageLanguage.English, "Password change", OutlookSalutationSample.English),
        })
        {
            composer.EditLanguage = composer.Languages.Single(l => l.Value == language);
            composer.Subject = subject;
            composer.Body = string.Empty;
            harness.Clipboard.Formatted = new ClipboardContent(CfHtmlFixture.Build(html), null, null, null);
            if (!string.IsNullOrWhiteSpace(composer.Html))
            {
                harness.Dialogs.ConfirmAnswers.Enqueue(true);
            }

            await composer.PasteFormattedCommand.ExecuteAsync(null);
            Assert.Contains("{$salutation}", composer.Html, StringComparison.Ordinal);
            Assert.Contains("{$salutation}", composer.Body, StringComparison.Ordinal);
        }

        Assert.Single(composer.TemplateImages);
        await composer.PlanCommand.ExecuteAsync(null);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        return (scenario, sample, composer);
    }

    [Fact]
    public async Task ThePreview_ShowsTheSalutationsInsideThePastedFormatting_WithTheLogo()
    {
        var (_, _, composer) = await PastedAsync(UserMessageTransportChoice.DirectSmtp);

        var professor = Item(composer, "Stanisław Kowalczyk");
        Assert.Equal(PlannedMessageStatus.Ready, professor.Status);
        Assert.Equal("≈ Szanowny Panie Profesorze", professor.SalutationColumnText);
        Assert.Contains(OutlookSalutationSample.ProfessorSalutationHtml, professor.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("aby <span style=\"background-color:yellow\">Pan</span> zmienił hasło", professor.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("Szanowny Panie Profesorze,", professor.TextBody, StringComparison.Ordinal);
        var logo = Assert.Single(professor.InlineImages);
        Assert.Contains($"src=\"cid:{logo.ContentId}\"", professor.HtmlPreview.Html, StringComparison.Ordinal);
        Assert.True(professor.HtmlPreview.Images.ContainsKey(logo.ContentId));

        var kim = Item(composer, "Sasza Kim");
        Assert.Equal(PlannedMessageStatus.Warning, kim.Status);
        Assert.Equal("⚠ Szanowna Pani / Szanowny Panie", kim.SalutationColumnText);
        Assert.Contains("Szanowna Pani / Szanowny Panie</span>,", kim.HtmlBody, StringComparison.Ordinal);
        Assert.Single(kim.InlineImages);

        var emily = Item(composer, "Emily Brown");
        Assert.Equal(MessageLanguage.English, emily.Planned.Language);
        Assert.Contains("Dear Ms Brown</span>,", emily.HtmlBody, StringComparison.Ordinal);
        Assert.Equal([logo.ContentId], emily.InlineImages.Select(i => i.ContentId));
    }

    [Fact]
    public async Task AGenderChosenInThePreview_RePlansThePastedMessage_WithItsLogo()
    {
        var (_, sample, composer) = await PastedAsync(UserMessageTransportChoice.DirectSmtp);
        var kim = Item(composer, "Sasza Kim");
        composer.SelectedItem = kim;

        kim.GenderChoice = SalutationGenderChoice.Feminine;

        var replanned = Item(composer, "Sasza Kim");
        Assert.Same(replanned, composer.SelectedItem);
        Assert.Equal(PlannedMessageStatus.Ready, replanned.Status);
        Assert.Contains("Szanowna Pani</span>,", replanned.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("aby <span style=\"background-color:yellow\">Pani</span> zmieniła hasło", replanned.HtmlBody, StringComparison.Ordinal);
        Assert.Single(replanned.InlineImages);
        Assert.Equal(Gender.Feminine, composer.GenderOverrides[sample.SaszaKim]);
    }

    [Theory]
    [InlineData(UserMessageTransportChoice.DirectSmtp)]
    [InlineData(UserMessageTransportChoice.Watchdog)]
    public async Task BothTransports_SendTheSalutationsWithTheInlineLogo(UserMessageTransportChoice transport)
    {
        var (scenario, _, composer) = await PastedAsync(transport);
        var logo = Assert.Single(Item(composer, "Stanisław Kowalczyk").InlineImages);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        scenario.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);

        await composer.SendCommand.ExecuteAsync(null);

        Assert.False(composer.IsTransportFitVisible, composer.TransportFitText);
        var sent = transport == UserMessageTransportChoice.DirectSmtp
            ? scenario.Transport.Accepted.Select(m => (To: m.To[0].Address, m.HtmlBody, m.TextBody, Images: m.InlineImages.Select(i => i.ContentId).ToList())).ToList()
            : scenario.Watchdog.UserMessages.Select(m => (To: m.To[0].Address, HtmlBody: m.HtmlBody ?? string.Empty, m.TextBody, Images: m.InlineImages.Select(i => i.ContentId).ToList())).ToList();
        Assert.Equal(3, sent.Count);
        if (transport == UserMessageTransportChoice.DirectSmtp)
        {
            Assert.Empty(scenario.Watchdog.UserMessages);
        }
        else
        {
            Assert.Empty(scenario.Transport.Attempts);
        }

        var professor = Assert.Single(sent, m => m.To == "stanislaw.kowalczyk@contoso.test");
        Assert.Contains(OutlookSalutationSample.ProfessorSalutationHtml, professor.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("Szanowny Panie Profesorze,", professor.TextBody, StringComparison.Ordinal);
        Assert.All(sent, m => Assert.Equal([logo.ContentId], m.Images));
        Assert.Contains("Dear Ms Brown</span>,", Assert.Single(sent, m => m.To == "emily.brown@contoso.test").HtmlBody, StringComparison.Ordinal);
    }
}
