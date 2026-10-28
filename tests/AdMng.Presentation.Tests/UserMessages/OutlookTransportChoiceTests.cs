using System.Globalization;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using AdMng.Testing.RichHtml;
using static AdMng.Presentation.Tests.UserMessages.MessageScenario;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// Messages to users through Outlook (U5, design note docs/dev/u5-outlook-correspondence.md 5): the choice Outlook in the
/// selector (account, "w imieniu", problems from the availability; Automatic never uses Outlook), the transport text and
/// the capabilities in the message window, and the Outlook members of the settings tab.
/// </summary>
public sealed class OutlookTransportChoiceTests
{
    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(14, 6, seed: 0x35), "logo.png");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<MessageScenario> OutlookScenarioAsync(string? account = null, bool onBehalf = false, CultureInfo? culture = null)
    {
        var scenario = await CreateAsync(culture);
        await scenario.Harness.Settings.UpdateAsync(s =>
        {
            s.UserMessages.Transport = UserMessageTransportChoice.Outlook;
            s.UserMessages.OutlookAccount = account;
            s.UserMessages.OutlookOnBehalfOfSender = onBehalf;
            s.UserMessages.FromAddress = "it-noreply@contoso.test";
        }, Ct);
        return scenario;
    }

    [Fact]
    public async Task Selector_Outlook_CreatesTheTransport_WithTheAccountAndOnBehalf()
    {
        var scenario = await OutlookScenarioAsync(" sekretariat.lodz@contoso.test ", onBehalf: true);

        var choice = await scenario.Services.Transports.SelectAsync(Ct);

        Assert.Same(scenario.OutlookTransports.Transport, choice.Transport);
        Assert.Equal(UserMessageTransportKind.Outlook, choice.Transport!.Kind);
        Assert.Null(choice.Problem);
        Assert.False(choice.NeedsPassword);
        Assert.Equal(("sekretariat.lodz@contoso.test", true), Assert.Single(scenario.OutlookTransports.Created));
        Assert.Equal(0, scenario.Outlook.Connections);       // choosing never starts Outlook
        Assert.Same(scenario.Outlook, scenario.Services.Transports.Outlook);
        Assert.Same(scenario.OutlookTransports, scenario.Services.Transports.OutlookTransports);
    }

    [Fact]
    public async Task Selector_Outlook_WithoutAnAccount_UsesTheDefaultAccount()
    {
        var scenario = await OutlookScenarioAsync();

        await scenario.Services.Transports.SelectAsync(Ct);

        Assert.Equal((null, false), Assert.Single(scenario.OutlookTransports.Created));
    }

    [Theory]
    [InlineData(OutlookAvailability.NotInstalled, UserMessageTransportChoiceResult.OutlookNotInstalled)]
    [InlineData(OutlookAvailability.NotSupported, UserMessageTransportChoiceResult.OutlookNotSupported)]
    public async Task Selector_Outlook_ProblemsComeFromTheAvailability(OutlookAvailability availability, string problem)
    {
        var scenario = await OutlookScenarioAsync();
        scenario.Outlook.Availability = availability;

        var choice = await scenario.Services.Transports.SelectAsync(Ct);

        Assert.Null(choice.Transport);
        Assert.Equal(problem, choice.Problem);
        Assert.Empty(scenario.OutlookTransports.Created);
    }

    [Fact]
    public async Task Selector_Outlook_OnAHostWithoutOutlook_IsNotSupported()
    {
        var scenario = await OutlookScenarioAsync();
        var settings = scenario.Harness.Settings;
        var withoutClient = new UserMessageTransportSelector(settings, new SmtpCredentialCache(), new WatchdogUserMessageTransport(), scenario.Transport);
        var withoutFactory = new UserMessageTransportSelector(settings, new SmtpCredentialCache(), new WatchdogUserMessageTransport(), scenario.Transport, scenario.Outlook);

        Assert.Equal(UserMessageTransportChoiceResult.OutlookNotSupported, (await withoutClient.SelectAsync(Ct)).Problem);
        Assert.Equal(UserMessageTransportChoiceResult.OutlookNotSupported, (await withoutFactory.SelectAsync(Ct)).Problem);
        Assert.Null(withoutClient.OutlookTransports);
    }

    [Theory]
    [InlineData(UserMessageTransportChoice.Automatic)]
    [InlineData(UserMessageTransportChoice.DirectSmtp)]
    public async Task Selector_OtherChoices_NeverUseOutlook(UserMessageTransportChoice transport)
    {
        var scenario = await OutlookScenarioAsync("sekretariat.lodz@contoso.test");
        await scenario.Harness.Settings.UpdateAsync(s => s.UserMessages.Transport = transport, Ct);

        var choice = await scenario.Services.Transports.SelectAsync(Ct);

        Assert.Equal(UserMessageTransportKind.DirectSmtp, choice.Transport!.Kind);
        Assert.Empty(scenario.OutlookTransports.Created);
    }

    [Theory]
    [InlineData(null, "en-US", "Sending through Outlook: the profile's default account")]
    [InlineData("sekretariat.lodz@contoso.test", "en-US", "Sending through Outlook: sekretariat.lodz@contoso.test")]
    [InlineData(null, "pl-PL", "Wysyłka przez Outlooka: konto domyślne profilu")]
    public async Task Window_ShowsTheOutlookTransport(string? account, string culture, string expected)
    {
        var scenario = await OutlookScenarioAsync(account, culture: CultureInfo.GetCultureInfo(culture));

        var composer = await scenario.OpenAsync([scenario.Company.JanKowalski]);

        Assert.Equal(expected, composer.TransportText);
    }

    [Fact]
    public async Task Window_WithoutClassicOutlook_ShowsTheProblem()
    {
        var scenario = await OutlookScenarioAsync(culture: CultureInfo.GetCultureInfo("pl-PL"));
        scenario.Outlook.Availability = OutlookAvailability.NotInstalled;

        var composer = await scenario.OpenAsync([scenario.Company.JanKowalski]);

        Assert.Equal("Klasyczny Outlook nie jest zainstalowany na tym komputerze (nowego Outlooka nie można użyć) – wybierz inny sposób wysyłki.", composer.TransportText);
    }

    [Fact]
    public async Task Window_SendsImagesThroughOutlook_WithTheOutlookCapabilities()
    {
        var scenario = await OutlookScenarioAsync();
        // Whatever the transport's status says, Outlook takes the Korespondencja limits (images allowed).
        scenario.OutlookTransports.Transport.Status = UserMessageTransportStatus.Available with { Capabilities = UserMessageTransportCapabilities.Legacy };
        var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring, 14);
        scenario.Harness.Clipboard.Formatted = new ClipboardContent(
            CfHtmlFixture.Build($"<html><body><!--StartFragment--><p>Dzień dobry {{givenName}},</p><p><img src=\"{MessageImageSamples.DataUri(Logo.Data, "image/png")}\" alt=\"Logo – Dział IT\"></p><!--EndFragment--></body></html>"),
            null,
            null,
            null);
        if (!string.IsNullOrWhiteSpace(composer.Html))
        {
            scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        }

        await composer.PasteFormattedCommand.ExecuteAsync(null);
        await composer.PlanCommand.ExecuteAsync(null);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.SendCommand.ExecuteAsync(null);

        Assert.False(composer.IsTransportFitVisible, composer.TransportFitText);
        var sent = Assert.Single(scenario.OutlookTransports.Transport.Accepted);
        Assert.Equal([Logo.ContentId], sent.InlineImages.Select(i => i.ContentId));
        Assert.Empty(scenario.Transport.Attempts);
        Assert.Equal(UserMessageReportStatus.Accepted, Item(composer, "Jan Kowalski").ReportStatus);
    }

    [Fact]
    public async Task Window_ShowsTheOutlookCodes_InTheReport()
    {
        var scenario = await OutlookScenarioAsync(culture: CultureInfo.GetCultureInfo("pl-PL"));
        scenario.OutlookTransports.Transport.DefaultOutcome = new UserMessageDelivery(UserMessageDeliveryOutcome.Rejected, OutlookFailureCodes.RecipientUnresolved);
        var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await composer.SendCommand.ExecuteAsync(null);

        var item = Item(composer, "Jan Kowalski");
        Assert.Equal(UserMessageReportStatus.Failed, item.ReportStatus);
        Assert.Equal("outlook.recipient – Outlook nie rozpoznał adresata.", item.ReportDetail);
    }

    [Fact]
    public async Task SettingsTab_HasTheOutlookMembers_AndSavesThem()
    {
        var scenario = await OutlookScenarioAsync("sekretariat.lodz@contoso.test", onBehalf: true);
        var settings = scenario.Factory.CreateSettings();
        var tab = Assert.IsType<UserMessageSettingsViewModel>(settings.Messages);

        Assert.True(tab.IsOutlookTransport);
        Assert.Equal("sekretariat.lodz@contoso.test", tab.OutlookAccount);
        Assert.True(tab.OutlookOnBehalfOfSender);
        Assert.Equal(["sekretariat.lodz@contoso.test"], tab.OutlookAccounts);
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tab.Transport = tab.TransportChoices.Single(c => c.Value == UserMessageTransportChoice.DirectSmtp);
        Assert.False(tab.IsOutlookTransport);
        Assert.Contains(nameof(UserMessageSettingsViewModel.IsOutlookTransport), changed);
        tab.Transport = tab.TransportChoices.Single(c => c.Value == UserMessageTransportChoice.Outlook);

        tab.OutlookAccount = "nie adres";
        Assert.Equal("Outlook account: not a valid e-mail address.", settings.Error);
        tab.OutlookAccount = "  ";
        Assert.Null(settings.Error);
        tab.OutlookOnBehalfOfSender = false;
        await settings.SaveCommand.ExecuteAsync(null);

        var saved = scenario.Harness.Settings.Current.UserMessages;
        Assert.Equal(UserMessageTransportChoice.Outlook, saved.Transport);
        Assert.Null(saved.OutlookAccount);
        Assert.False(saved.OutlookOnBehalfOfSender);
    }

    [Fact]
    public async Task SettingsTab_LoadsTheAccountsFromOutlook_AndEndsTheSession()
    {
        var scenario = await OutlookScenarioAsync("kadry@contoso.test", culture: CultureInfo.GetCultureInfo("pl-PL"));
        var tab = Assert.IsType<UserMessageSettingsViewModel>(scenario.Factory.CreateSettings().Messages);
        Assert.True(tab.LoadOutlookAccountsCommand.CanExecute(null));

        await tab.LoadOutlookAccountsCommand.ExecuteAsync(null);

        Assert.Equal(["jan.kowalski@contoso.test", "sekretariat.lodz@contoso.test", "kadry@contoso.test"], tab.OutlookAccounts);
        Assert.Equal("kadry@contoso.test", tab.OutlookAccount);
        Assert.Equal("Konta z profilu „Outlook”: jan.kowalski@contoso.test, sekretariat.lodz@contoso.test", tab.OutlookAccountsText);
        Assert.True(Assert.Single(scenario.Outlook.Sessions).IsDisposed);
        Assert.False(tab.IsLoadingOutlookAccounts);
    }

    [Fact]
    public async Task SettingsTab_ShowsWhyTheAccountsCouldNotBeLoaded()
    {
        var scenario = await OutlookScenarioAsync(culture: CultureInfo.GetCultureInfo("pl-PL"));
        scenario.Outlook.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.Elevated, "connect");
        var tab = Assert.IsType<UserMessageSettingsViewModel>(scenario.Factory.CreateSettings().Messages);

        await tab.LoadOutlookAccountsCommand.ExecuteAsync(null);

        Assert.StartsWith("Nie udało się pobrać kont z Outlooka: AD Manager działa jako administrator", tab.OutlookAccountsText, StringComparison.Ordinal);
        Assert.Empty(tab.OutlookAccounts);
    }

    [Fact]
    public async Task SettingsTab_CannotLoadAccounts_WithoutClassicOutlook()
    {
        var scenario = await OutlookScenarioAsync();
        scenario.Outlook.Availability = OutlookAvailability.NotInstalled;
        var tab = Assert.IsType<UserMessageSettingsViewModel>(scenario.Factory.CreateSettings().Messages);

        Assert.False(tab.LoadOutlookAccountsCommand.CanExecute(null));
        Assert.Equal(0, scenario.Outlook.Connections);
    }
}
