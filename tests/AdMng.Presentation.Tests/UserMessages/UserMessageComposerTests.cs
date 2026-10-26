using System.Globalization;
using AdMng.Core.Messaging;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using static AdMng.Presentation.Tests.UserMessages.MessageScenario;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// The message window over the fake DC with the sample company (U2 scenario, now = 2026-10-13 12:00 UTC): plan, preview,
/// exclude, test message, confirmation, sending, report, retry, CSV, session log, password in memory only, templates.
/// </summary>
public sealed class UserMessageComposerTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task Plan_RendersOneMessagePerAccount_InPolish_AndSkipsAccountsThatDoNotMeetTheCondition()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        var composer = await scenario.OpenAsync(
            [company.JanKowalski, company.LukaszWisniewski, company.MalgorzataWojcik, company.BartlomiejKozlowski],
            BuiltInMessageTemplates.PasswordExpiring, 14);
        Assert.Equal(UserMessageStage.Compose, composer.Stage);
        Assert.Equal(14, composer.ConditionDays);
        Assert.Contains("smtp.contoso.test:587", composer.TransportText, StringComparison.Ordinal);

        await composer.PlanCommand.ExecuteAsync(null);

        Assert.Null(composer.Error);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        Assert.Equal(4, composer.AllItems.Count);
        var jan = Item(composer, "Jan Kowalski");
        Assert.Equal(PlannedMessageStatus.Ready, jan.Status);
        Assert.Equal("Hasło do konta jan.kowalski wygaśnie 23 października 2026", jan.Subject);
        Assert.StartsWith("Dzień dobry Jan,", jan.TextBody, StringComparison.Ordinal);
        Assert.Contains("(pozostało dni: 10)", jan.TextBody, StringComparison.Ordinal);
        Assert.Equal("Jan Kowalski <jan.kowalski@contoso.test>", jan.ToText);
        Assert.Equal("helpdesk@contoso.test", jan.CcText);
        Assert.Equal("pomoc@contoso.test", jan.ReplyToText);
        Assert.Equal("Dział IT <noreply@contoso.test>", jan.FromText);
        Assert.Equal("PL", jan.LanguageText);
        Assert.Equal("Hasło do konta lukasz.wisniewski wygaśnie 18 października 2026", Item(composer, "Łukasz Wiśniewski").Subject);
        var malgorzata = Item(composer, "Małgorzata Wójcik");
        Assert.Equal(PlannedMessageStatus.Skipped, malgorzata.Status);
        Assert.False(malgorzata.CanInclude);
        Assert.Contains("Password expires within N days", malgorzata.IssuesText, StringComparison.Ordinal);
        Assert.Equal(PlannedMessageStatus.Skipped, Item(composer, "Bartłomiej Kozłowski").Status);
        Assert.Equal(2, composer.SendCount);
        Assert.Equal("Ready: 2 · With warnings: 0 · Errors: 0 · Skipped: 2 · Excluded: 0", composer.SummaryText);
        Assert.Equal("Send (2)", composer.SendButtonText);
    }

    [Fact]
    public async Task Preview_FiltersAndExclusions_AndTheManagerInCopy()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        var composer = await scenario.OpenAsync([company.LukaszWisniewski, scenario.Review.Agnieszka, scenario.Review.Wieslaw], BuiltInMessageTemplates.Custom);
        composer.Subject = "Przerwa serwisowa w sobotę";
        composer.RecipientMode = composer.RecipientModes.Single(m => m.Value == MessageRecipientMode.UserAndManager);

        await composer.PlanCommand.ExecuteAsync(null);

        var lukasz = Item(composer, "Łukasz Wiśniewski");
        Assert.Equal("helpdesk@contoso.test", lukasz.CcText.Split("; ")[^1]);
        Assert.Contains("jan.kowalski@contoso.test", lukasz.CcText, StringComparison.Ordinal);
        var agnieszka = Item(composer, "Agnieszka Żółć");
        Assert.Contains("agnieszka.zolc@contoso.test", agnieszka.ToText, StringComparison.Ordinal);
        Assert.Contains("primary SMTP address agnieszka.zolc@contoso.test", agnieszka.IssuesText, StringComparison.Ordinal);
        // Wiesław has no address: the message goes to his manager, whose account is disabled.
        var wieslaw = Item(composer, "Wiesław Pękala");
        Assert.Equal(PlannedMessageStatus.Warning, wieslaw.Status);
        Assert.Contains("zbigniew.kazmierczak@contoso.test", wieslaw.ToText, StringComparison.Ordinal);
        Assert.Contains("The manager's account Zbigniew Kaźmierczak is disabled.", wieslaw.IssuesText, StringComparison.Ordinal);

        composer.Filter = composer.Filters.Single(f => f.Value == UserMessagePreviewFilter.Warnings);
        Assert.Equal([wieslaw], composer.Items);
        composer.Filter = composer.Filters.Single(f => f.Value == UserMessagePreviewFilter.Errors);
        Assert.Empty(composer.Items);
        lukasz.IsIncluded = false;
        Assert.Equal(2, composer.SendCount);
        composer.Filter = composer.Filters.Single(f => f.Value == UserMessagePreviewFilter.Excluded);
        Assert.Equal([lukasz], composer.Items);
        Assert.Contains("Excluded: 1", composer.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_AboveTheThreshold_NeedsTheTypedNumber_ThenReportsPerRecipient_AndLogsWithoutContent()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        await scenario.Harness.Settings.UpdateAsync(s => s.UserMessages.ConfirmationThreshold = 1, TestContext.Current.CancellationToken);
        var composer = await scenario.OpenAsync(
            [company.JanKowalski, company.LukaszWisniewski, company.MalgorzataWojcik], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);

        // Declined: nothing is sent.
        scenario.Harness.Dialogs.TypedConfirmAnswers.Enqueue(false);
        await composer.SendCommand.ExecuteAsync(null);
        Assert.Empty(scenario.Transport.Attempts);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        Assert.Equal("2", scenario.Harness.Dialogs.TypedConfirmations[^1].Expected);

        scenario.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await composer.SendCommand.ExecuteAsync(null);

        Assert.Equal(UserMessageStage.Report, composer.Stage);
        Assert.Equal(["jan.kowalski@contoso.test", "lukasz.wisniewski@contoso.test"], scenario.Transport.Accepted.Select(m => m.To[0].Address));
        Assert.Contains(TimeSpan.FromSeconds(2), scenario.Delays);
        Assert.Equal(UserMessageReportStatus.Accepted, Item(composer, "Jan Kowalski").ReportStatus);
        Assert.Equal("Accepted by the server", Item(composer, "Jan Kowalski").ReportText);
        Assert.Equal(UserMessageReportStatus.Skipped, Item(composer, "Małgorzata Wójcik").ReportStatus);
        Assert.Equal("Accepted by the server: 2 · Failed: 0 · Not sent: 0 · Not in this send: 1", composer.ReportSummary);
        Assert.False(composer.RetryFailedCommand.CanExecute(null));

        var entry = Assert.Single(scenario.Harness.AuditLog.Entries, e => e.Action == UserMessageComposerViewModel.SessionLogAction);
        Assert.Equal("2/2", entry.NewValue);
        Assert.Equal("Password expires in N days", entry.OldValue);
        Assert.Equal("smtp.contoso.test:587", entry.Server);
        Assert.True(entry.Succeeded);
        var logged = string.Join("|", entry.Operator, entry.Server, entry.DistinguishedName, entry.Attribute, entry.OldValue, entry.NewValue, entry.Error);
        Assert.DoesNotContain("@", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("października", logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejection_IsReportedWithTheCode_AndRetryFailedSendsItAgainAfterAFreshPlan()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        scenario.Transport.Script("lukasz.wisniewski@contoso.test", FakeUserMessageTransport.Rejected(550, "5.1.1"));
        var composer = await scenario.OpenAsync([company.JanKowalski, company.LukaszWisniewski], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.SendCommand.ExecuteAsync(null);

        var lukasz = Item(composer, "Łukasz Wiśniewski");
        Assert.Equal(UserMessageReportStatus.Failed, lukasz.ReportStatus);
        Assert.Equal("smtp.550.5.1.1", lukasz.ReportDetail);
        Assert.True(lukasz.CanRetry);
        Assert.False(scenario.Harness.AuditLog.Entries.Single(e => e.Action == UserMessageComposerViewModel.SessionLogAction).Succeeded);

        await composer.RetryFailedCommand.ExecuteAsync(null);

        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        Assert.Equal("Łukasz Wiśniewski", Assert.Single(composer.AllItems).Name);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.SendCommand.ExecuteAsync(null);
        Assert.Equal(UserMessageReportStatus.Accepted, Assert.Single(composer.AllItems).ReportStatus);
        Assert.Equal(2, scenario.Transport.Accepted.Count);
    }

    [Fact]
    public async Task TestMessage_GoesOnlyToTheOperator_WithTheTestPrefix()
    {
        var scenario = await CreateAsync();
        var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring);
        await composer.PlanCommand.ExecuteAsync(null);
        composer.SelectedItem = Item(composer, "Jan Kowalski");

        await composer.SendTestCommand.ExecuteAsync(null);

        var test = Assert.Single(scenario.Transport.Accepted);
        Assert.Equal(["operator@contoso.test"], test.To.Select(a => a.Address));
        Assert.Empty(test.Cc);
        Assert.StartsWith("[TEST] Hasło do konta jan.kowalski", test.Subject, StringComparison.Ordinal);
        Assert.Equal("The server accepted the test message for operator@contoso.test.", composer.TestResultText);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
    }

    [Fact]
    public async Task SmtpPassword_IsAskedWhenNeeded_KeptOnlyInMemory_AndForgottenWhenRejected()
    {
        var scenario = await CreateAsync();
        await scenario.Harness.Settings.UpdateAsync(s =>
        {
            s.UserMessages.Smtp.Authentication = SmtpAuthentication.Basic;
            s.UserMessages.Smtp.UserName = "helpdesk@contoso.test";
        }, TestContext.Current.CancellationToken);
        var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring);
        Assert.True(composer.NeedsSmtpPassword);
        await composer.PlanCommand.ExecuteAsync(null);

        await composer.SendCommand.ExecuteAsync(null);
        Assert.Equal("Enter the SMTP password (kept in memory only until AD Manager closes).", composer.Error);
        Assert.Empty(scenario.Transport.Attempts);

        var box = new TestPasswordInput("Tajne-Hasło-2026");
        composer.SmtpPasswordInput = box;
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.SendCommand.ExecuteAsync(null);

        Assert.Single(scenario.Transport.Accepted);
        Assert.Equal("Tajne-Hasło-2026", scenario.Transport.LastCredential!.Password);
        Assert.True(box.Cleared);
        Assert.NotNull(scenario.Credentials.Find("smtp.contoso.test", "helpdesk@contoso.test"));
        Assert.DoesNotContain("Tajne-Hasło-2026", JsonSettingsStore.Serialize(scenario.Harness.Settings.Current), StringComparison.Ordinal);
        Assert.DoesNotContain("Tajne", scenario.Credentials.ToString(), StringComparison.Ordinal);

        // The server rejects the password later: it is forgotten and asked again.
        scenario.Transport.DefaultOutcome = FakeUserMessageTransport.AuthenticationFailed();
        Assert.False(composer.RetryFailedCommand.CanExecute(null));
        composer.BackCommand.Execute(null);
        await composer.PlanCommand.ExecuteAsync(null);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.SendCommand.ExecuteAsync(null);
        Assert.Equal(UserMessageReportStatus.Failed, Assert.Single(composer.AllItems).ReportStatus);
        Assert.StartsWith("smtp.535.5.7.139 – ", Assert.Single(composer.AllItems).ReportDetail, StringComparison.Ordinal);
        Assert.True(composer.NeedsSmtpPassword);
        Assert.Null(scenario.Credentials.Find("smtp.contoso.test", "helpdesk@contoso.test"));
    }

    [Fact]
    public async Task Inputs_AreRequired_AndRenderedInTheMessage()
    {
        var scenario = await CreateAsync(Polish);
        var composer = await scenario.OpenAsync([scenario.Company.PawelLewandowski], BuiltInMessageTemplates.InactiveDisable, 90);
        var input = Assert.Single(composer.Inputs);
        Assert.Equal("termin", input.Name);

        await composer.PlanCommand.ExecuteAsync(null);
        Assert.Equal("Wpisz wartość pola „termin”.", composer.Error);
        Assert.Equal(UserMessageStage.Compose, composer.Stage);

        input.Value = "30 listopada 2026";
        await composer.PlanCommand.ExecuteAsync(null);

        var pawel = Item(composer, "Paweł Lewandowski");
        Assert.Equal("Konto pawel.lewandowski zostanie wyłączone 30 listopada 2026", pawel.Subject);
        Assert.Contains("nie było używane od 15 czerwca 2026", pawel.TextBody, StringComparison.Ordinal);
        Assert.Equal("Gotowa", pawel.StatusText);
    }

    [Fact]
    public async Task TemplateErrors_AreShownWhileEditing_AndBlockThePlan()
    {
        var scenario = await CreateAsync();
        var composer = await scenario.OpenAsync([scenario.Company.JanKowalski]);
        Assert.Equal(BuiltInMessageTemplates.Custom, composer.SelectedTemplate!.Value);

        composer.Subject = "Twoje dane";
        composer.Body = "Hasło: {unicodePwd} {$input:nowe hasło}";

        Assert.Contains("{unicodePwd}", composer.TemplateErrors, StringComparison.Ordinal);
        Assert.Contains("{$input:nowe hasło}", composer.TemplateErrors, StringComparison.Ordinal);
        Assert.Empty(composer.Inputs);
        await composer.PlanCommand.ExecuteAsync(null);
        Assert.Equal(UserMessageStage.Compose, composer.Stage);
        Assert.Contains("passwords and secret attributes are never sent", composer.Error, StringComparison.Ordinal);
        Assert.Empty(scenario.Transport.Attempts);
    }

    [Fact]
    public async Task Templates_AreSavedExportedImportedAndDeleted_AsTextOnly()
    {
        var scenario = await CreateAsync();
        var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring);
        Assert.Equal("Password expires in N days (copy)", composer.TemplateName);
        composer.EditLanguage = composer.Languages.Single(l => l.Value == MessageLanguage.English);
        Assert.StartsWith("The password of your account", composer.Subject, StringComparison.Ordinal);
        composer.EditLanguage = composer.Languages.Single(l => l.Value == MessageLanguage.Polish);
        composer.Subject = "Pilne: hasło do {sAMAccountName} wygasa {$passwordExpires:dd.MM.yyyy}";
        composer.TemplateName = "Hasło – wersja helpdesku";

        await composer.SaveTemplateCommand.ExecuteAsync(null);

        var stored = Assert.Single(scenario.Harness.Settings.Current.UserMessages.Templates);
        Assert.Equal("Hasło – wersja helpdesku", stored.Name);
        Assert.Equal(MessageConditionKind.PasswordExpiresWithin, stored.Condition);
        Assert.Equal(stored.Id, composer.SelectedTemplate!.Value);
        Assert.False(composer.IsBuiltInTemplate);
        Assert.Contains(composer.Templates, t => t.Text == "Hasło – wersja helpdesku");

        var path = Path.Combine(Path.GetTempPath(), $"admng-templates-{Guid.NewGuid():N}.json");
        try
        {
            scenario.Harness.Files.NextSavePath = path;
            await composer.ExportTemplatesCommand.ExecuteAsync(null);
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("AdMng.MessageTemplates", json, StringComparison.Ordinal);
            Assert.Contains("{sAMAccountName}", json, StringComparison.Ordinal);
            Assert.DoesNotContain("jan.kowalski", json, StringComparison.Ordinal);

            scenario.Harness.Files.NextOpenPath = path;
            await composer.ImportTemplatesCommand.ExecuteAsync(null);
            Assert.Equal(2, scenario.Harness.Settings.Current.UserMessages.Templates.Count);
            Assert.Equal(2, scenario.Harness.Settings.Current.UserMessages.Templates.Select(t => t.Id).Distinct().Count());
        }
        finally
        {
            File.Delete(path);
        }

        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.DeleteTemplateCommand.ExecuteAsync(null);
        Assert.Single(scenario.Harness.Settings.Current.UserMessages.Templates);
        Assert.Equal(BuiltInMessageTemplates.Custom, composer.SelectedTemplate!.Value);
    }

    [Fact]
    public async Task ReportCsv_HasOneRowPerAccount_WithoutTheMessage()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        var composer = await scenario.OpenAsync([company.JanKowalski, company.MalgorzataWojcik], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.SendCommand.ExecuteAsync(null);
        var path = Path.Combine(Path.GetTempPath(), $"admng-report-{Guid.NewGuid():N}.csv");
        try
        {
            scenario.Harness.Files.NextSavePath = path;
            await composer.ExportReportCommand.ExecuteAsync(null);

            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("Status,Name,Login,DN,To,CC,Code", lines[0].TrimStart('﻿'), StringComparison.Ordinal);
            Assert.StartsWith("Accepted,Jan Kowalski,jan.kowalski,", lines[1], StringComparison.Ordinal);
            Assert.StartsWith("Skipped,Małgorzata Wójcik,", lines[2], StringComparison.Ordinal);
            Assert.DoesNotContain("Hasło do konta", string.Join("\n", lines), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Cancel_StopsTheRun_TheRestIsNotSent()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        var composer = await scenario.OpenAsync([company.JanKowalski, company.LukaszWisniewski], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        scenario.Transport.BeforeSend = _ => composer.CancelSendCommand.Execute(null);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await composer.SendCommand.ExecuteAsync(null);

        Assert.Equal(UserMessageReportStatus.Accepted, Item(composer, "Jan Kowalski").ReportStatus);
        var lukasz = Item(composer, "Łukasz Wiśniewski");
        Assert.Equal(UserMessageReportStatus.NotSent, lukasz.ReportStatus);
        Assert.Equal("Stopped by the operator.", lukasz.ReportDetail);
        Assert.True(composer.RetryFailedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Transport_Automatic_FallsBackToSmtp_WhileTheWatchdogHasMessagesSwitchedOff()
    {
        var scenario = await CreateAsync();
        var composer = await scenario.OpenAsync([scenario.Company.JanKowalski]);
        Assert.Equal("Sending directly via SMTP: smtp.contoso.test:587", composer.TransportText);

        await scenario.Harness.Settings.UpdateAsync(s => s.UserMessages.Transport = UserMessageTransportChoice.Watchdog, TestContext.Current.CancellationToken);
        await composer.InitializeAsync();
        Assert.StartsWith("Messages to users are switched off in AdMng Watchdog", composer.TransportText, StringComparison.Ordinal);

        scenario.Watchdog.Configure(null);
        await composer.InitializeAsync();
        Assert.Equal("AdMng Watchdog is not configured (Watchdog tab).", composer.TransportText);

        await scenario.Harness.Settings.UpdateAsync(s =>
        {
            s.UserMessages.Transport = UserMessageTransportChoice.Automatic;
            s.UserMessages.Smtp.Host = null;
        }, TestContext.Current.CancellationToken);
        await composer.InitializeAsync();
        Assert.StartsWith("Sending is not configured", composer.TransportText, StringComparison.Ordinal);
        Assert.False((await new WatchdogUserMessageTransport(scenario.Watchdog).GetStatusAsync(TestContext.Current.CancellationToken)).IsAvailable);
        var notConfigured = await Assert.ThrowsAsync<UserMessageTransportException>(() => new WatchdogUserMessageTransport(scenario.Watchdog).ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Equal("watchdog.notConfigured", notConfigured.Delivery.Code);
    }

    [Fact]
    public async Task Transport_Automatic_UsesTheWatchdog_OnceItsMessagesAreOn_AndReportsQueuedMessages()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        scenario.Watchdog.UserMessagingStatus = scenario.Watchdog.UserMessagingStatus with { Enabled = true };
        scenario.Watchdog.UserMessageHandler = request => request.To[0].Address.StartsWith("lukasz", StringComparison.Ordinal)
            ? new AdMng.Watchdog.Contracts.Api.UserMessageResult { Id = request.Id, State = AdMng.Watchdog.Contracts.Api.UserMessageState.Queued, Code = "smtp.451.4.7.1", SmtpStatus = 451 }
            : new AdMng.Watchdog.Contracts.Api.UserMessageResult { Id = request.Id, State = AdMng.Watchdog.Contracts.Api.UserMessageState.Delivered, Code = "ok", SmtpStatus = 250 };
        var composer = await scenario.OpenAsync([company.JanKowalski, company.LukaszWisniewski], BuiltInMessageTemplates.PasswordExpiring, 14);
        Assert.Equal("Sending through AdMng Watchdog (https://admng-wd.contoso.com:8443)", composer.TransportText);
        await composer.PlanCommand.ExecuteAsync(null);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await composer.SendCommand.ExecuteAsync(null);

        Assert.Empty(scenario.Transport.Attempts);
        Assert.Equal(["jan.kowalski@contoso.test", "lukasz.wisniewski@contoso.test"], scenario.Watchdog.UserMessages.Select(m => m.To[0].Address));
        var sent = scenario.Watchdog.UserMessages[0];
        Assert.Equal(("pl", "pomoc@contoso.test"), (sent.Language, sent.ReplyTo?.Address));
        Assert.Contains(sent.Cc, c => c.Address == "helpdesk@contoso.test");
        Assert.Contains("hasło", sent.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(UserMessageReportStatus.Accepted, Item(composer, "Łukasz Wiśniewski").ReportStatus);
        Assert.StartsWith("Queued in AdMng Watchdog", Item(composer, "Łukasz Wiśniewski").ReportDetail, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Item(composer, "Jan Kowalski").ReportDetail);
        var entry = Assert.Single(scenario.Harness.AuditLog.Entries, e => e.Action == UserMessageComposerViewModel.SessionLogAction);
        Assert.Equal(("2/2", "AdMng Watchdog (https://admng-wd.contoso.com:8443)"), (entry.NewValue, entry.Server));
    }

    [Fact]
    public async Task Recipients_CanBeAddedWithThePicker_AndRemoved_AndTheWindowEndsWithTheSession()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        var composer = await scenario.OpenAsync([company.JanKowalski]);
        scenario.Harness.Dialogs.PickedObjects.Enqueue([company.LukaszWisniewski, company.JanKowalski]);

        await composer.AddRecipientsCommand.ExecuteAsync(null);
        Assert.Equal(["Jan Kowalski", "Łukasz Wiśniewski"], composer.Recipients.Select(r => r.Name));
        Assert.Equal("Send message to users (2)", composer.WindowTitle);
        composer.SelectedRecipient = composer.Recipients[0];
        composer.RemoveRecipientCommand.Execute(null);
        Assert.Equal("Recipients: 1", composer.RecipientsText);

        await scenario.Harness.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => composer.IsSessionEnded);
        Assert.False(composer.PlanCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(AccountCheck.Locked, BuiltInMessageTemplates.AccountLocked, null)]
    [InlineData(AccountCheck.PasswordExpiring, BuiltInMessageTemplates.PasswordExpiring, 21)]
    [InlineData(AccountCheck.PasswordExpired, BuiltInMessageTemplates.PasswordExpired, null)]
    [InlineData(AccountCheck.AccountExpiring, BuiltInMessageTemplates.AccountExpiring, 45)]
    [InlineData(AccountCheck.Inactive, BuiltInMessageTemplates.InactiveDisable, 60)]
    [InlineData(AccountCheck.NeverLoggedOn, BuiltInMessageTemplates.InactiveDisable, 60)]
    [InlineData(AccountCheck.MustChangePassword, BuiltInMessageTemplates.MustChangePassword, null)]
    [InlineData(AccountCheck.NoMail, BuiltInMessageTemplates.Custom, null)]
    public void ReviewChecks_OpenTheMatchingTemplate_WithTheReviewDays(AccountCheck check, string templateId, int? days)
    {
        var parameters = new AccountReviewParameters { ExpiryWarningDays = 21, AccountExpiryWarningDays = 45, InactiveDays = 60 };

        var request = UserMessageRequest.For(["CN=x,DC=contoso,DC=test"], check, parameters);

        Assert.Equal((templateId, days), (request.TemplateId, request.ConditionDays));
        Assert.Null(UserMessageRequest.For([], null).TemplateId);
    }

    [Fact]
    public void Localization_EveryDynamicKeyExistsInBothLanguages()
    {
        var english = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var polish = new ResxLocalizer(Polish);

        foreach (var key in UserMessageText.AllDynamicKeys())
        {
            Assert.False(english[key].StartsWith('['), key);
            Assert.False(polish[key].StartsWith('['), key);
        }

        Assert.Equal("Wyślij wiadomość...", polish[L.UserMessages.Command]);
        Assert.Equal("Wiadomości e-mail", polish[L.UserMessages.SettingsTab]);
    }
}
