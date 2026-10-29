using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// "4 · Wysyłka" (design note 6.4, 7.4): sending one message per recipient, per address, in BCC chunks, drafts, the test
/// to self, MailerWPiA's confirmations, the stops (5 failures in a row, a failure that stops the run, the operator), the
/// delay through the injected function with the estimate, the journal (send runs only), retry of the failed ones, the
/// report and the session log entry without content.
/// </summary>
public sealed class SendPageTests
{
    private const string Subject = "Zaproszenie na konferencję – {{Miejscowość}}";

    private static readonly string NewLine = Environment.NewLine;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Send_OneMessagePerRecipient_WithTheOptions_StatusesJournalAndLog()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        var offices = CorrespondencePagesScenario.Offices();
        CorrespondencePagesScenario.AddRows(window, offices);
        window.Outlook.SelectedAccount = window.Outlook.Accounts[1];
        window.Outlook.OnBehalfOf = "rektorat@contoso.test";
        var message = window.Message;
        message.ReadReceipt = true;
        message.UseVoting = true;
        var folder = Directory.CreateDirectory(Path.Combine(scenario.Paths.SettingsDirectory, "pliki")).FullName;
        var program = Path.Combine(folder, "program.pdf");
        await File.WriteAllBytesAsync(program, new byte[2048], Token);
        await message.AddAttachmentFilesAsync([program]);
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.Equal(
            "confirm:Wysłać wiadomość do 3 odbiorców?" + NewLine + NewLine
            + "Nadawca: Sekretariat – Łódź <sekretariat.lodz@contoso.test>" + NewLine
            + "W imieniu: rektorat@contoso.test" + NewLine
            + "Tryb: osobna wiadomość do każdego" + NewLine
            + "Załączniki (1): program.pdf" + NewLine + NewLine
            + "Wysyłki nie da się cofnąć.",
            Assert.Single(scenario.Ui.Shown));
        var sent = scenario.Outlook.Succeeded();
        Assert.Equal([["sekretariat@andrespol.pl"], ["ug@brojce.pl"], ["kancelaria@zgierz.pl"]], sent.Select(m => m.To));
        Assert.Equal(
            ["Zaproszenie na konferencję – Andrespol", "Zaproszenie na konferencję – Brójce", "Zaproszenie na konferencję – Zgierz"],
            sent.Select(m => m.Subject));
        Assert.All(sent, m =>
        {
            Assert.Empty(m.Bcc);
            Assert.Equal("sekretariat.lodz@contoso.test", m.SendUsingAccount);
            Assert.Equal("rektorat@contoso.test", m.SentOnBehalfOf);
            Assert.True(m.ReadReceipt);
            Assert.Null(m.DeferredDelivery);
            Assert.Equal(CorrespondenceSettings.DefaultVotingOptions, m.VotingOptions);
            Assert.Equal([program], m.Attachments.Select(a => a.Path));
            Assert.Matches("^[0-9a-f]{32}$", m.CampaignId);
        });
        Assert.Single(sent.Select(m => m.CampaignId).Distinct());
        Assert.Contains("zapraszamy Urząd Gminy Brójce z miejscowości Brójce", sent[1].HtmlBody, StringComparison.Ordinal);

        Assert.All(offices, row =>
        {
            Assert.Equal((RecipientStatusKind.Sent, "wysłano"), (row.StatusKind, row.Status));
            Assert.True(row.IsAlreadySent);
            Assert.Equal(Subject, scenario.Journal.Find(row.Email)?.Subject);   // the subject as typed
        });
        Assert.Equal(
            [
                "✓ sekretariat@andrespol.pl   (Urząd Gminy Andrespol)",
                "✓ ug@brojce.pl   (Urząd Gminy Brójce)",
                "✓ kancelaria@zgierz.pl   (Starostwo Powiatowe w Zgierzu)",
                "Wysyłka zakończona: udane 3, błędy 0.",
                "Adresy dopisano do dziennika „już wysłane”, więc przy kolejnej wysyłce zostaną pominięte.",
            ],
            window.Send.Results.Select(r => r.Text));
        Assert.All(window.Send.Results, r => Assert.True(r.IsSuccess));
        Assert.Equal((3, 0, 0), (window.Send.SentCount, window.Send.FailedCount, window.Send.SkippedCount));
        Assert.Equal((3d, 3d), (window.Send.ProgressValue, window.Send.ProgressMaximum));
        Assert.Equal("Wysyłka zakończona: udane 3, błędy 0.", window.Send.ProgressText);
        Assert.False(window.Send.IsSending);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)], scenario.Delays);
        Assert.Contains((NotificationSeverity.Success, "Wysyłka zakończona: udane 3, błędy 0."), scenario.Harness.Notifications.Items);
        Assert.Equal(Subject, scenario.Templates.LoadLastDraft()?.Subject);   // saved before the run
        Assert.Equal("sekretariat.lodz@contoso.test", scenario.Harness.Settings.Current.Correspondence.AccountAddress);

        var entry = Assert.Single(scenario.Harness.AuditLog.Entries);
        Assert.Equal((SendPageViewModel.SessionLogAction, "Outlook", "Korespondencja (Outlook)", "3/3", true, (string?)null),
            (entry.Action, entry.Server, entry.OldValue, entry.NewValue, entry.Succeeded, entry.Error));
        var logged = string.Join("|", entry.Operator, entry.Server, entry.DistinguishedName, entry.Attribute, entry.OldValue, entry.NewValue, entry.Error);
        Assert.DoesNotContain("@", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("Zaproszenie", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("Andrespol", logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_GroupedByAddress_OneMessagePerOwner_WithTheMailboxKeys()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        CorrespondencePagesScenario.AddRows(window,
            Mailbox("jan.kowalski@contoso.test", "Jan Kowalski", "Sekretariat IT", "sekretariat.it@contoso.test"),
            Mailbox("anna.nowak@contoso.test", "Anna Nowak", "Biuro projektu", "biuro.projektu@contoso.test"),
            Mailbox("Jan.Kowalski@contoso.test", "Jan Kowalski", "Rekrutacja", "rekrutacja@contoso.test"));
        var message = window.Message;
        message.Subject = "Czy skrzynki są nadal potrzebne? ({{liczba}})";
        message.BodyHtml = "<p>{{zwrot}},</p><p>{{lista}}</p>";
        message.ListItemTemplate = "{{Skrzynka}}";
        message.GroupByAddress = true;
        message.UseVoting = true;
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.StartsWith("confirm:Wysłać wiadomość do 2 odbiorców?", scenario.Ui.Shown[0], StringComparison.Ordinal);
        Assert.Contains("Tryb: jedna wiadomość na adres ze wszystkimi jego wierszami", scenario.Ui.Shown[0], StringComparison.Ordinal);
        var sent = scenario.Outlook.Succeeded();
        Assert.Equal(2, sent.Count);
        Assert.Equal("Czy skrzynki są nadal potrzebne? (2)", sent[0].Subject);
        var list = sent[0].HtmlBody.Replace("\n", string.Empty, StringComparison.Ordinal);
        Assert.Contains("<li>Sekretariat IT</li><li>Rekrutacja</li>", list, StringComparison.Ordinal);
        Assert.Equal(["sekretariat.it@contoso.test", "rekrutacja@contoso.test"], sent[0].CampaignKeys);
        Assert.Equal(["biuro.projektu@contoso.test"], sent[1].CampaignKeys);
        Assert.All(window.Recipients.Items, row => Assert.Equal(RecipientStatusKind.Sent, row.StatusKind));
        Assert.Equal(2, window.Send.SentCount);
    }

    [Fact]
    public async Task Send_Bcc_InChunks_ToTheOwnAddress_WithoutTheFieldsOfARow()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        var rows = Enumerable.Range(1, 5).Select(i => CorrespondencePagesScenario.Row($"gmina{i}@lodzkie.pl", $"Gmina {i}", ("Miejscowość", $"Miasto {i}"))).ToArray();
        CorrespondencePagesScenario.AddRows(window, rows);
        window.Message.SendMode = window.Message.SendModeChoices.Single(m => m.Value == CorrespondenceSendMode.Bcc);
        window.Message.BccChunkSize = 2;
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.Contains("Wysłać wiadomość do 5 odbiorców?", scenario.Ui.Shown[0], StringComparison.Ordinal);
        Assert.Contains("Tryb: jedna wiadomość na najwyżej 2 odbiorców w UDW", scenario.Ui.Shown[0], StringComparison.Ordinal);
        var sent = scenario.Outlook.Succeeded();
        Assert.Equal(3, sent.Count);
        Assert.All(sent, m =>
        {
            Assert.Equal(["jan.kowalski@contoso.test"], m.To);
            Assert.Equal("Zaproszenie na konferencję –", m.Subject);
            Assert.Empty(m.CampaignKeys);
        });
        Assert.Equal([["gmina1@lodzkie.pl", "gmina2@lodzkie.pl"], ["gmina3@lodzkie.pl", "gmina4@lodzkie.pl"], ["gmina5@lodzkie.pl"]], sent.Select(m => m.Bcc));
        Assert.Contains("zapraszamy Szanowni Państwo z miejscowości", sent[0].HtmlBody, StringComparison.Ordinal);
        Assert.All(rows, row => Assert.Equal(RecipientStatusKind.Sent, row.StatusKind));
        Assert.All(rows, row => Assert.NotNull(scenario.Journal.Find(row.Email)));
        Assert.Equal(
            ["✓ wiadomość 1 z 3: 2 odbiorców w UDW", "✓ wiadomość 2 z 3: 2 odbiorców w UDW", "✓ wiadomość 3 z 3: 1 odbiorców w UDW"],
            window.Send.Results.Take(3).Select(r => r.Text));
        Assert.Equal("3/3", Assert.Single(scenario.Harness.AuditLog.Entries).NewValue);
    }

    [Fact]
    public async Task CreateDrafts_SavesEveryMessage_WithoutTheJournalOrPauses()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        var offices = CorrespondencePagesScenario.Offices();
        CorrespondencePagesScenario.AddRows(window, offices);
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.CreateDraftsCommand.ExecuteAsync(null);

        var question = Assert.Single(scenario.Ui.Shown);
        Assert.StartsWith("confirm:Utworzyć 3 wersji roboczych?", question, StringComparison.Ordinal);
        Assert.DoesNotContain("Wysyłki nie da się cofnąć.", question, StringComparison.Ordinal);
        Assert.Equal(3, scenario.Outlook.Succeeded(OutlookSendMode.Draft).Count);
        Assert.Empty(scenario.Outlook.Succeeded(OutlookSendMode.Send));
        Assert.All(offices, row =>
        {
            Assert.Equal((RecipientStatusKind.Draft, "wersja robocza", false), (row.StatusKind, row.Status, row.IsAlreadySent));
            Assert.Null(scenario.Journal.Find(row.Email));
        });
        Assert.Equal(0, scenario.Journal.Count);
        Assert.Empty(scenario.Delays);
        Assert.Equal("Utworzono wersje robocze: udane 3, błędy 0.", window.Send.Results[^1].Text);
        Assert.Equal("Korespondencja (Outlook) – wersje robocze", Assert.Single(scenario.Harness.AuditLog.Entries).OldValue);
    }

    [Fact]
    public async Task SendTest_GoesToTheOwnAddress_WithTheFieldsOfTheChosenRecipient()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        var offices = CorrespondencePagesScenario.Offices();
        CorrespondencePagesScenario.AddRows(window, offices);
        await window.Preview.RefreshCommand.ExecuteAsync(null);
        window.Preview.SelectedRecipient = window.Preview.PreviewRecipients[1];

        await window.Send.SendTestCommand.ExecuteAsync(null);

        Assert.Empty(scenario.Ui.Shown);   // no question for a test
        var test = Assert.Single(scenario.Outlook.Succeeded());
        Assert.Equal(["jan.kowalski@contoso.test"], test.To);
        Assert.Equal("[TEST] Zaproszenie na konferencję – Brójce", test.Subject);
        Assert.Null(test.CampaignId);
        Assert.Equal(0, scenario.Journal.Count);
        Assert.Equal((RecipientStatusKind.Test, "test wysłany"), (offices[1].StatusKind, offices[1].Status));
        Assert.Equal(RecipientStatusKind.None, offices[0].StatusKind);
        Assert.Equal(["✓ test do jan.kowalski@contoso.test", "Test: udane 1, błędy 0."], window.Send.Results.Select(r => r.Text));
        Assert.Equal("Korespondencja (Outlook) – test", Assert.Single(scenario.Harness.AuditLog.Entries).OldValue);
        Assert.Empty(scenario.Delays);
    }

    [Fact]
    public async Task SendTest_WithoutAnOwnAddress_AsksForOne()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Outlook.Profile = FakeOutlookClient.SampleProfile with { CurrentUserAddress = null };
        using var window = await scenario.OpenWithMessageAsync();

        scenario.Dialogs.Prompts.Enqueue("to nie adres");
        await window.Send.SendTestCommand.ExecuteAsync(null);
        Assert.Equal(("Test do siebie", "„to nie adres” nie jest poprawnym adresem e-mail."), Assert.Single(scenario.Ui.Errors));
        Assert.Empty(scenario.Outlook.Items);

        scenario.Dialogs.Prompts.Enqueue(" operator@contoso.test ");
        await window.Send.SendTestCommand.ExecuteAsync(null);
        var test = Assert.Single(scenario.Outlook.Succeeded());
        Assert.Equal(["operator@contoso.test"], test.To);
        Assert.StartsWith("[TEST] ", test.Subject, StringComparison.Ordinal);

        await window.Send.SendTestCommand.ExecuteAsync(null);   // the prompt cancelled
        Assert.Single(scenario.Outlook.Items);
    }

    [Fact]
    public async Task FiveFailuresInARow_StopTheRun()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        var rows = Enumerable.Range(1, 9).Select(i => CorrespondencePagesScenario.Row($"urzad{i}@gmina.pl", "", ("Miejscowość", "Łódź"))).ToArray();
        CorrespondencePagesScenario.AddRows(window, rows);
        scenario.Outlook.DefaultResult = FakeOutlookClient.Failed(OutlookFailureKind.RecipientUnresolved, "recipients");
        scenario.Outlook.Script("urzad2@gmina.pl", OutlookItemResult.Sent);   // a success resets the count
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.Equal(7, scenario.Outlook.Items.Count);
        Assert.Equal(RecipientStatusKind.Failed, rows[0].StatusKind);
        Assert.Equal("błąd – Outlook nie rozpoznał adresata.", rows[0].Status);
        Assert.Equal(RecipientStatusKind.Sent, rows[1].StatusKind);
        Assert.All(rows[2..7], row => Assert.Equal(RecipientStatusKind.Failed, row.StatusKind));
        Assert.All(rows[7..], row => Assert.Equal(RecipientStatusKind.None, row.StatusKind));
        Assert.Equal("✗ urzad1@gmina.pl – Outlook nie rozpoznał adresata.", window.Send.Results[0].Text);
        Assert.False(window.Send.Results[0].IsSuccess);
        Assert.Contains("■ przerwano po 5 błędach z rzędu – sprawdź Outlooka i użyj „Ponów nieudane”", window.Send.Results.Select(r => r.Text));
        Assert.Equal("Wysyłka przerwana: udane 1, błędy 6, niewysłane 2.", window.Send.ProgressText);
        Assert.Equal((1, 6), (window.Send.SentCount, window.Send.FailedCount));
        var entry = Assert.Single(scenario.Harness.AuditLog.Entries);
        Assert.Equal(("1/9", false, "błędy: 6, niewysłane: 2"), (entry.NewValue, entry.Succeeded, entry.Error));
        Assert.True(window.Send.RetryFailedCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFailureThatStopsTheRun_StopsAtOnce_AndReportsTheLostOutlook()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        scenario.Outlook.Script("ug@brojce.pl", FakeOutlookClient.Failed(OutlookFailureKind.Disconnected));
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, scenario.Outlook.Items.Count);
        Assert.Contains("■ przerwano: Połączenie z Outlookiem zostało przerwane (Outlook zamknięty?) – uruchom Outlooka i połącz ponownie.",
            window.Send.Results.Select(r => r.Text));
        Assert.Equal("Wysyłka przerwana: udane 1, błędy 1, niewysłane 1.", window.Send.ProgressText);
        Assert.Equal(OutlookState.Failed, window.Outlook.State);
    }

    [Fact]
    public async Task Stop_EndsTheRunBeforeTheNextMessage()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        var offices = CorrespondencePagesScenario.Offices();
        CorrespondencePagesScenario.AddRows(window, offices);
        var sentSoFar = 0;
        scenario.Outlook.BeforeSend = (_, _) =>
        {
            if (++sentSoFar == 2)
            {
                Assert.True(window.Send.StopCommand.CanExecute(null));
                window.Send.StopCommand.Execute(null);
            }
        };
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, scenario.Outlook.Items.Count);
        Assert.Equal(RecipientStatusKind.None, offices[2].StatusKind);
        Assert.Contains("■ zatrzymano", window.Send.Results.Select(r => r.Text));
        Assert.Equal("Wysyłka przerwana: udane 2, błędy 0, niewysłane 1.", window.Send.ProgressText);
        Assert.False(window.Send.IsSending);
        Assert.False(window.Send.StopCommand.CanExecute(null));
        Assert.Equal("2/3", Assert.Single(scenario.Harness.AuditLog.Entries).NewValue);
    }

    [Fact]
    public async Task Delay_GoesThroughTheInjectedFunction_AndTheEstimateIsShown()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, [.. Enumerable.Range(1, 4).Select(i => CorrespondencePagesScenario.Row($"urzad{i}@gmina.pl", $"Urząd {i}", ("Miejscowość", "Łódź")))]);
        var started = scenario.Harness.Time.GetUtcNow();
        var progress = new List<string>();
        window.Send.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SendPageViewModel.ProgressText))
            {
                progress.Add(window.Send.ProgressText);
            }
        };
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.Equal(Enumerable.Repeat(TimeSpan.FromSeconds(3), 3), scenario.Delays);
        Assert.Equal(TimeSpan.FromSeconds(9), scenario.Harness.Time.GetUtcNow() - started);
        Assert.Equal(
            [
                "Przygotowuję wiadomości…",
                "Wysyłam 1 z 4 · zostało ok. 10 s",
                "Wysyłam 2 z 4 · zostało ok. 7 s",
                "Wysyłam 3 z 4 · zostało ok. 4 s",
                "Wysyłam 4 z 4",
                "Wysyłka zakończona: udane 4, błędy 0.",
            ],
            progress);
    }

    [Fact]
    public async Task Estimate_AboveAMinute_IsInMinutes()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        Assert.Equal("1 min", SendPageViewModel.EtaText(scenario.Harness.Localizer, TimeSpan.FromSeconds(90.6)));
        Assert.Equal("4 min", SendPageViewModel.EtaText(scenario.Harness.Localizer, TimeSpan.FromMinutes(4.9)));
        Assert.Equal("1 s", SendPageViewModel.EtaText(scenario.Harness.Localizer, TimeSpan.FromSeconds(0.2)));
    }

    [Fact]
    public async Task RetryFailed_SendsOnlyTheFailedOnesAgain()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        var offices = CorrespondencePagesScenario.Offices();
        CorrespondencePagesScenario.AddRows(window, offices);
        scenario.Outlook.Script("ug@brojce.pl", FakeOutlookClient.Failed(OutlookFailureKind.ItemTooLarge, "save", savedAsDraft: true));
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await window.Send.SendCommand.ExecuteAsync(null);
        Assert.Equal("błąd – Wiadomość jest za duża dla skrzynki lub serwera. (zapisana w Wersjach roboczych)", offices[1].Status);
        Assert.Contains("Nieudane wiadomości zapisano w Outlooku jako wersje robocze – możesz je tam obejrzeć.", window.Send.Results.Select(r => r.Text));
        Assert.True(window.Send.RetryFailedCommand.CanExecute(null));

        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await window.Send.RetryFailedCommand.ExecuteAsync(null);

        Assert.Equal("confirm:Ponowić wysyłkę do 1 odbiorców, u których wystąpił błąd?", scenario.Ui.Shown[^1]);
        Assert.Equal(4, scenario.Outlook.Items.Count);
        Assert.Equal(["ug@brojce.pl"], scenario.Outlook.Items.Last().Message.To);
        Assert.All(offices, row => Assert.Equal(RecipientStatusKind.Sent, row.StatusKind));
        Assert.Equal(["✓ ug@brojce.pl   (Urząd Gminy Brójce)", "Wysyłka zakończona: udane 1, błędy 0.",
            "Adresy dopisano do dziennika „już wysłane”, więc przy kolejnej wysyłce zostaną pominięte."], window.Send.Results.Select(r => r.Text));
        Assert.False(window.Send.RetryFailedCommand.CanExecute(null));
        Assert.Equal(2, scenario.Harness.AuditLog.Entries.Count);
    }

    [Fact]
    public async Task RetryFailed_AfterADraftRun_SavesTheDraftsAgain_AndNeverSends()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        var offices = CorrespondencePagesScenario.Offices();
        CorrespondencePagesScenario.AddRows(window, offices);
        scenario.Outlook.Script("kancelaria@zgierz.pl", FakeOutlookClient.Failed(OutlookFailureKind.AttachmentMissing, "attachments"));
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await window.Send.CreateDraftsCommand.ExecuteAsync(null);
        Assert.Equal(RecipientStatusKind.Failed, offices[2].StatusKind);

        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await window.Send.RetryFailedCommand.ExecuteAsync(null);

        Assert.Equal("confirm:Ponowić tworzenie wersji roboczych dla 1 odbiorców, u których wystąpił błąd?", scenario.Ui.Shown[^1]);
        Assert.Empty(scenario.Outlook.Succeeded(OutlookSendMode.Send));
        Assert.Equal(OutlookSendMode.Draft, scenario.Outlook.Items.Last().Mode);
        Assert.Equal(["kancelaria@zgierz.pl"], scenario.Outlook.Items.Last().Message.To);
        Assert.All(offices, row => Assert.Equal(RecipientStatusKind.Draft, row.StatusKind));
        Assert.Null(scenario.Journal.Find("kancelaria@zgierz.pl"));
    }

    [Fact]
    public async Task Summary_CountsTheMessagesOfEachMode()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window,
            CorrespondencePagesScenario.Row("jan.kowalski@contoso.test", "Jan Kowalski", ("Miejscowość", "Łódź")),
            CorrespondencePagesScenario.Row("JAN.KOWALSKI@contoso.test", "Jan Kowalski", ("Miejscowość", "Zgierz")),
            CorrespondencePagesScenario.Row("anna.nowak@contoso.test", "Anna Nowak", ("Miejscowość", "Brójce")));
        var set = CorrespondenceSendSet.Of(window.Recipients);

        Assert.Equal(3, SendPageViewModel.MessageCount(set, bcc: false, groupByAddress: false, bccChunkSize: 500));
        Assert.Equal(2, SendPageViewModel.MessageCount(set, bcc: false, groupByAddress: true, bccChunkSize: 500));
        Assert.Equal(1, SendPageViewModel.MessageCount(set, bcc: true, groupByAddress: false, bccChunkSize: 500));
        Assert.Equal(2, SendPageViewModel.MessageCount(set, bcc: true, groupByAddress: true, bccChunkSize: 1));
        Assert.Equal(set.Units(groupByAddress: true).Count, SendPageViewModel.MessageCount(set, false, true, 500));
        Assert.Equal(set.BccChunks(1).Count, SendPageViewModel.MessageCount(set, true, false, 1));
    }

    [Fact]
    public async Task Confirmation_NamesSkippedAndInvalidRows_AndTheDelayWarning_AndIsTypedAboveTheThreshold()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        var rows = Enumerable.Range(1, 51).Select(i => CorrespondencePagesScenario.Row($"urzad{i}@gmina.pl", "", ("Miejscowość", "Łódź"))).ToList();
        rows[0].IsExcluded = true;
        rows[1].IsAlreadySent = true;
        rows.Add(new CorrespondenceRecipientViewModel("to-nie-adres", "Błędny", "kontakty.csv", new Dictionary<string, string>(), isValid: false));
        var notIncluded = CorrespondencePagesScenario.Row("pominiety@gmina.pl", "");
        notIncluded.Include = false;
        rows.Add(notIncluded);
        rows.AddRange(Enumerable.Range(52, 2).Select(i => CorrespondencePagesScenario.Row($"urzad{i}@gmina.pl", "", ("Miejscowość", "Łódź"))));
        CorrespondencePagesScenario.AddRows(window, [.. rows]);
        window.Message.DelaySeconds = 1.5;
        window.Message.DeliverLater = true;
        window.Message.DeliverTime = "07:30";
        scenario.Ui.TypedConfirmAnswers.Enqueue(false);

        await window.Send.SendCommand.ExecuteAsync(null);

        var (title, text, expected) = Assert.Single(scenario.Ui.TypedConfirmations);
        Assert.Equal(("Wysyłka przez Outlooka", "51"), (title, expected));
        Assert.Equal(
            "Wysłać wiadomość do 51 odbiorców?" + NewLine + NewLine
            + "Nadawca: konto domyślne profilu Outlooka" + NewLine
            + "Tryb: osobna wiadomość do każdego" + NewLine
            + "Pominięci (wykluczenia / już wysłane): 2" + NewLine
            + "Błędne adresy: 1" + NewLine
            + "Dostarczenie wstrzymane do: 14.10.2026 07:30" + NewLine
            + "UWAGA: przy odstępie 1,5 s i 51 wiadomościach serwer pocztowy może zacząć je odrzucać (limit ok. 30 na minutę). Zalecany odstęp: 3 s." + NewLine
            + NewLine + "Wysyłki nie da się cofnąć." + NewLine
            + NewLine + "Aby potwierdzić, wpisz liczbę odbiorców (51).",
            text);
        Assert.Empty(scenario.Outlook.Items);
        Assert.Empty(scenario.Harness.AuditLog.Entries);
        Assert.Empty(window.Send.Results);
    }

    [Fact]
    public async Task Send_DeferredDelivery_GoesIntoEveryMessage_AndSkippedRowsAreCounted()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        var offices = CorrespondencePagesScenario.Offices();
        offices[2].IsAlreadySent = true;
        CorrespondencePagesScenario.AddRows(window, offices);
        window.Message.DeliverLater = true;
        window.Message.DeliverTime = "8:15";
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        var sent = scenario.Outlook.Succeeded();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, m => Assert.Equal(new DateTimeOffset(2026, 10, 14, 8, 15, 0, TimeSpan.Zero), m.DeferredDelivery));
        Assert.Equal(1, window.Send.SkippedCount);
        Assert.Equal(RecipientStatusKind.None, offices[2].StatusKind);

        // Without skipping, the journal's rows are sent too.
        window.Recipients.SkipAlreadySent = false;
        window.Send.RefreshSummaryCommand.Execute(null);
        Assert.StartsWith("Do wysłania: 3    (pominięci: 0, błędne adresy: 0)", window.Send.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_NoRecipients_AndOutlookUnavailable_StopBeforeSending()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Outlook.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.NotResponding, "connect");
        using var window = await scenario.OpenAsync();

        await window.Send.SendCommand.ExecuteAsync(null);
        Assert.Equal(
            "Wiadomości nie można jeszcze wysłać:" + NewLine + "• Wpisz temat wiadomości." + NewLine + "• Treść wiadomości jest pusta.",
            scenario.Ui.Errors[^1].Message);

        window.Message.Subject = Subject;
        window.Message.BodyHtml = "<p>Treść</p>";
        await window.Send.SendCommand.ExecuteAsync(null);
        Assert.Equal("Nie ma do kogo wysłać – sprawdź zaznaczenia i listę pominięć.", scenario.Ui.Errors[^1].Message);

        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        await window.Send.SendCommand.ExecuteAsync(null);
        Assert.StartsWith("Nie można użyć Outlooka: ", scenario.Ui.Errors[^1].Message, StringComparison.Ordinal);
        Assert.Contains("Outlook nie odpowiada", scenario.Ui.Errors[^1].Message, StringComparison.Ordinal);
        Assert.Empty(scenario.Outlook.Items);
        Assert.Empty(scenario.Ui.Shown);
        Assert.Empty(scenario.Harness.AuditLog.Entries);
    }

    [Fact]
    public async Task WithoutOutlook_SendingIsDisabled()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync(withOutlook: false);
        using var window = await scenario.OpenWithMessageAsync();

        Assert.False(window.Send.SendCommand.CanExecute(null));
        Assert.False(window.Send.SendTestCommand.CanExecute(null));
        Assert.False(window.Send.CreateDraftsCommand.CanExecute(null));
        Assert.False(window.Send.RetryFailedCommand.CanExecute(null));
        Assert.False(window.Send.ExportReportCommand.CanExecute(null));
    }

    [Fact]
    public async Task WithoutTheJournal_SendingWorks_AndSaysTheAddressesWereNotRecorded()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Protector.Failure = new PlatformNotSupportedException("brak DPAPI");
        using var window = await scenario.OpenWithMessageAsync();
        var offices = CorrespondencePagesScenario.Offices();
        CorrespondencePagesScenario.AddRows(window, offices);
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.Equal(3, scenario.Outlook.Succeeded().Count);
        Assert.All(offices, row => Assert.Equal((RecipientStatusKind.Sent, false), (row.StatusKind, row.IsAlreadySent)));
        Assert.Equal("Dziennik „już wysłane” jest niedostępny – wysłanych adresów nie zapisano.", window.Send.Results[^1].Text);
        Assert.False(window.Send.Results[^1].IsSuccess);
    }

    [Fact]
    public async Task ExportReport_WritesTheDateTheSubjectAndTheLines()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        scenario.Outlook.Script("kancelaria@zgierz.pl", FakeOutlookClient.Failed(OutlookFailureKind.Unknown, "send", hresult: unchecked((int)0x80004005)));
        Assert.False(window.Send.ExportReportCommand.CanExecute(null));
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await window.Send.SendCommand.ExecuteAsync(null);
        Directory.CreateDirectory(scenario.Paths.SettingsDirectory);
        var file = Path.Combine(scenario.Paths.SettingsDirectory, "raport.txt");
        scenario.Harness.Files.NextSavePath = file;

        await window.Send.ExportReportCommand.ExecuteAsync(null);

        Assert.Equal("raport_wysylki_2026-10-13_1200.txt", scenario.Harness.Files.LastDefaultFileName);
        var lines = await File.ReadAllLinesAsync(file, Token);
        Assert.Equal("Raport wysyłki 2026-10-13 12:00", lines[0]);
        Assert.Equal($"Temat: {Subject}", lines[1]);
        Assert.Equal(string.Empty, lines[2]);
        Assert.Equal("✓ sekretariat@andrespol.pl   (Urząd Gminy Andrespol)", lines[3]);
        Assert.Equal("✗ kancelaria@zgierz.pl – Nieznany błąd Outlooka. (kod 0x80004005)", lines[5]);
        Assert.Equal("Wysyłka zakończona: udane 2, błędy 1.", lines[6]);
        Assert.Contains((NotificationSeverity.Success, $"Zapisano raport: {file}"), scenario.Harness.Notifications.Items);
    }

    [Fact]
    public async Task Summary_FollowsMailerWPiA()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        var offices = CorrespondencePagesScenario.Offices();
        offices[0].IsExcluded = true;
        CorrespondencePagesScenario.AddRows(window, offices);
        window.Outlook.OnBehalfOf = "rektorat@contoso.test";
        window.Message.SelectedSignature = window.Message.Signatures[1];
        window.Message.ReadReceipt = true;
        window.Message.UseVoting = true;
        window.Message.VotingOptions = "Tak;Nie";

        window.SelectedTab = CorrespondenceTab.Send;

        Assert.Equal(
            [
                "Do wysłania: 2    (pominięci: 1, błędne adresy: 0)",
                "Nadawca: konto domyślne profilu Outlooka",
                "W imieniu: rektorat@contoso.test",
                "Tryb: osobna wiadomość do każdego",
                "Temat: Zaproszenie na konferencję – {{Miejscowość}}",
                "Podpis: Jan Kowalski – podpis",
                "Załączniki: brak",
                "Potwierdzenie przeczytania: tak",
                "Przyciski głosowania: Tak; Nie",
                "Szacowany czas: ok. 8 s",
            ],
            window.Send.SummaryText.Split(NewLine));

        window.Message.SendMode = window.Message.SendModeChoices[1];
        window.Message.BccChunkSize = 1;
        window.Send.RefreshSummaryCommand.Execute(null);
        Assert.Contains("Tryb: jedna wiadomość na najwyżej 1 odbiorców w UDW", window.Send.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Wiadomości: 2", window.Send.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingTheWindowWhileSending_AsksAndStops()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        var window = await scenario.OpenWithMessageAsync();
        using var dispose = window;
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        scenario.Outlook.BeforeSend = (_, _) =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        var run = window.Send.SendCommand.ExecuteAsync(null);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.True(window.Send.IsSending);

        scenario.Ui.ConfirmAnswers.Enqueue(false);
        Assert.False(await window.CanCloseAsync());
        Assert.True(window.Send.IsSending);

        scenario.Ui.ConfirmAnswers.Enqueue(true);
        var closing = window.CanCloseAsync();
        release.Release();
        Assert.True(await closing);
        await run;

        Assert.False(window.Send.IsSending);
        Assert.Single(scenario.Outlook.Items);
        Assert.Contains("■ zatrzymano", window.Send.Results.Select(r => r.Text));
        Assert.Equal(
            "confirm:Trwa wysyłka przez Outlooka. Przerwać wysyłkę i zamknąć okno? Wiadomości, które Outlook już wysłał, nie da się wycofać.",
            scenario.Ui.Shown[^1]);
    }

    private static CorrespondenceRecipientViewModel Mailbox(string owner, string name, string mailbox, string mailboxAddress) =>
        new(owner, name, "Skrzynki", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Skrzynka"] = mailbox, ["Adres skrzynki"] = mailboxAddress },
            mailboxKeys: [mailboxAddress]);
}
