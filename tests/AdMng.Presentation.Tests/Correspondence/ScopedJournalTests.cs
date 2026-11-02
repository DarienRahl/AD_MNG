using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Settings;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The journal "już wysłane" per mailbox: a mailbox owner row is recorded and found with its mailbox key as the scope –
/// one owner with three mailboxes gets three messages, a fourth mailbox later is not skipped – each row's own pair is
/// recorded (never a BCC chunk's addresses crossed with its keys), and unscoped entries (older journals, file rows) keep
/// answering for unscoped rows only.
/// </summary>
public sealed class ScopedJournalTests
{
    private const string Owner = "jan.kowalski@contoso.test";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static CorrespondenceRecipientViewModel Mailbox(string owner, string name, string mailbox, string mailboxAddress) =>
        new(owner, name, "Skrzynki", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Skrzynka"] = mailbox, ["Adres skrzynki"] = mailboxAddress },
            mailboxKeys: [mailboxAddress])
        {
            Origin = RecipientOrigin.Mailboxes,
        };

    private static CorrespondenceRecipientViewModel[] ThreeMailboxesOfJan() =>
    [
        Mailbox(Owner, "Jan Kowalski", "Sekretariat Wydziału Łączności", "sekretariat.lacznosci@contoso.test"),
        Mailbox(Owner, "Jan Kowalski", "Rekrutacja", "rekrutacja@contoso.test"),
        Mailbox(Owner, "Jan Kowalski", "Archiwum Łódź", "archiwum.lodz@contoso.test"),
    ];

    private static async Task<CorrespondenceViewModel> OpenAsync(CorrespondencePagesScenario scenario, params CorrespondenceRecipientViewModel[] rows)
    {
        var window = await scenario.OpenAsync();
        CorrespondencePagesScenario.AddRows(window, rows);
        window.Message.Subject = "Weryfikacja skrzynki pocztowej: {{Skrzynka}}";
        window.Message.BodyHtml = "<p>Szanowni Państwo,</p><p>prosimy o potwierdzenie, czy skrzynka jest nadal potrzebna.</p>";
        return window;
    }

    [Fact]
    public void AMailboxRow_IsScopedByItsMailboxKey_OtherRowsAreNot()
    {
        Assert.Equal("rekrutacja@contoso.test", Mailbox(Owner, "Jan Kowalski", "Rekrutacja", " rekrutacja@contoso.test ").JournalScope);
        Assert.Null(CorrespondencePagesScenario.Row(Owner, "Jan Kowalski").JournalScope);
        Assert.Null(new CorrespondenceRecipientViewModel(Owner, "Jan Kowalski", "Skrzynki", new Dictionary<string, string>(), mailboxKeys: [" "]).JournalScope);
    }

    [Fact]
    public async Task OneOwnerWithThreeMailboxes_GetsThreeMessages_ThreeScopedEntries_AndAFourthMailboxLaterIsNotSkipped()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        var rows = ThreeMailboxesOfJan();
        using var window = await OpenAsync(scenario, rows);
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.Equal(
            ["Weryfikacja skrzynki pocztowej: Sekretariat Wydziału Łączności", "Weryfikacja skrzynki pocztowej: Rekrutacja", "Weryfikacja skrzynki pocztowej: Archiwum Łódź"],
            scenario.Outlook.Succeeded().Select(m => m.Subject));
        Assert.Equal(3, scenario.Journal.Count);
        Assert.All(rows, r => Assert.Equal("Weryfikacja skrzynki pocztowej: {{Skrzynka}}", scenario.Journal.Find(r.Email, r.JournalScope)?.Subject));
        Assert.Null(scenario.Journal.Find(Owner));                                         // never the address alone
        Assert.All(rows, r =>
        {
            Assert.True(r.IsAlreadySent);
            Assert.StartsWith(scenario.Harness.Time.GetLocalNow().ToString("d", scenario.Harness.Localizer.Culture) + " · Weryfikacja", r.PreviouslySentText, StringComparison.Ordinal);
        });

        // A later query finds a fourth mailbox of the same owner: only that row is sent.
        var fourth = Mailbox(Owner, "Jan Kowalski", "Biuro Rady Wydziału", "biuro.rady@contoso.test");
        CorrespondencePagesScenario.AddRows(window, fourth);
        var set = CorrespondenceSendSet.Of(window.Recipients);
        Assert.Equal([fourth], set.Rows);
        Assert.Equal(3, set.Skipped);
        Assert.False(fourth.IsAlreadySent);
        Assert.Null(fourth.PreviouslySentText);
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.Equal("Weryfikacja skrzynki pocztowej: Biuro Rady Wydziału", scenario.Outlook.Succeeded()[^1].Subject);
        Assert.Equal(4, scenario.Outlook.Succeeded().Count);
        Assert.Equal(4, scenario.Journal.Count);
        Assert.NotNull(scenario.Journal.Find(Owner, "biuro.rady@contoso.test"));
    }

    [Fact]
    public async Task GroupedByOwner_OneMessage_RecordsEveryMailboxOfIt()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        var rows = ThreeMailboxesOfJan();
        using var window = await OpenAsync(scenario, rows);
        window.Message.Subject = "Weryfikacja skrzynek pocztowych";
        window.Message.BodyHtml = "<p>Skrzynki:</p><p>{{lista}}</p>";
        window.Message.ListItemTemplate = "{{Skrzynka}}";
        window.Message.GroupByAddress = true;
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.Single(scenario.Outlook.Succeeded());
        Assert.Equal(3, scenario.Journal.Count);
        Assert.All(rows, r => Assert.NotNull(scenario.Journal.Find(r.Email, r.JournalScope)));
        Assert.All(rows, r => Assert.True(r.IsAlreadySent));
    }

    [Fact]
    public async Task ABccChunk_RecordsEachRowsOwnPair_NeverItsAddressesCrossedWithItsKeys()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        var jan = ThreeMailboxesOfJan()[..2];
        var anna = Mailbox("anna.nowak@contoso.test", "Anna Nowak", "Dziekanat", "dziekanat@contoso.test");
        var file = CorrespondencePagesScenario.Row("zofia.wisniewska@contoso.test", "Zofia Wiśniewska");
        using var window = await OpenAsync(scenario, [.. jan, anna, file]);
        window.Message.SendMode = window.Message.SendModeChoices.Single(m => m.Value == CorrespondenceSendMode.Bcc);
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        var message = Assert.Single(scenario.Outlook.Succeeded());
        Assert.Equal([Owner, "anna.nowak@contoso.test", "zofia.wisniewska@contoso.test"], message.Bcc);
        Assert.Equal(4, scenario.Journal.Count);
        Assert.NotNull(scenario.Journal.Find(Owner, jan[0].JournalScope));
        Assert.NotNull(scenario.Journal.Find(Owner, jan[1].JournalScope));
        Assert.NotNull(scenario.Journal.Find("anna.nowak@contoso.test", "dziekanat@contoso.test"));
        Assert.NotNull(scenario.Journal.Find("zofia.wisniewska@contoso.test"));                 // a file row: the address alone
        Assert.Null(scenario.Journal.Find(Owner, "dziekanat@contoso.test"));                     // no cross product
        Assert.Null(scenario.Journal.Find("anna.nowak@contoso.test", "rekrutacja@contoso.test"));
        Assert.Null(scenario.Journal.Find("zofia.wisniewska@contoso.test", "dziekanat@contoso.test"));
        Assert.Null(scenario.Journal.Find(Owner));
    }

    [Fact]
    public async Task UnscopedEntries_AnswerOnlyForUnscopedRows_AndScopedOnesOnlyForTheirMailbox()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        var mailbox = Mailbox(Owner, "Jan Kowalski", "Rekrutacja", "rekrutacja@contoso.test");
        var invitation = CorrespondencePagesScenario.Row(Owner, "Jan Kowalski", ("Miejscowość", "Łódź"));
        using var window = await OpenAsync(scenario, mailbox, invitation);

        // A journal of an earlier version (or a conference invitation): the address alone.
        await scenario.Journal.AppendAsync(Owner, "Zaproszenie na konferencję", Token);
        window.Recipients.RefreshJournalMarks();
        Assert.True(invitation.IsAlreadySent);
        Assert.Equal(scenario.Harness.Time.GetLocalNow().ToString("d", scenario.Harness.Localizer.Culture) + " · Zaproszenie na konferencję", invitation.PreviouslySentText);
        Assert.False(mailbox.IsAlreadySent);                     // the mailbox was never asked about
        Assert.Null(mailbox.PreviouslySentText);

        // The mailbox query: only the mailbox row is sent and recorded for its mailbox.
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await window.Send.SendCommand.ExecuteAsync(null);

        var sent = Assert.Single(scenario.Outlook.Succeeded());
        Assert.Equal("Weryfikacja skrzynki pocztowej: Rekrutacja", sent.Subject);
        Assert.True(mailbox.IsAlreadySent);
        Assert.Equal("Zaproszenie na konferencję", scenario.Journal.Find(Owner)!.Subject);   // the unscoped entry is untouched
        Assert.Equal("Weryfikacja skrzynki pocztowej: {{Skrzynka}}", scenario.Journal.Find(Owner, "Rekrutacja@Contoso.test")!.Subject);
    }

    [Fact]
    public async Task ScopedEntries_SurviveReloading_AndOldUnscopedLinesStayReadable()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        await scenario.Journal.LoadAsync(TimeSpan.FromDays(365), Token);
        await scenario.Journal.AppendAsync(Owner, "Stary temat", Token);
        await scenario.Journal.AppendAsync(Owner, "Weryfikacja skrzynki", "rekrutacja@contoso.test", Token);

        var reopened = new CorrespondenceJournal(scenario.Paths, scenario.Protector, scenario.Harness.Time);
        await reopened.LoadAsync(TimeSpan.FromDays(365), Token);

        Assert.Equal(2, reopened.Count);
        Assert.Equal("Stary temat", reopened.Find(Owner)!.Subject);
        Assert.Equal("Weryfikacja skrzynki", reopened.Find(Owner, "rekrutacja@contoso.test")!.Subject);
        Assert.Null(reopened.Find(Owner, "archiwum.lodz@contoso.test"));
        Assert.Equal(reopened.Find(Owner), reopened.Find(Owner, "  "));                         // a blank scope is no scope
    }

    [Fact]
    public async Task RetryFailed_RecordsTheRetriedRowForItsMailbox()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        var rows = ThreeMailboxesOfJan();
        using var window = await OpenAsync(scenario, rows);
        scenario.Outlook.Script(Owner, OutlookItemResult.Sent, FakeOutlookClient.Failed(OutlookFailureKind.ItemTooLarge, "save"));
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await window.Send.SendCommand.ExecuteAsync(null);
        Assert.Equal(RecipientStatusKind.Failed, rows[1].StatusKind);
        Assert.Null(scenario.Journal.Find(Owner, rows[1].JournalScope));
        Assert.Equal(2, scenario.Journal.Count);

        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await window.Send.RetryFailedCommand.ExecuteAsync(null);

        Assert.Equal("Weryfikacja skrzynki pocztowej: Rekrutacja", scenario.Outlook.Succeeded()[^1].Subject);
        Assert.Equal(3, scenario.Journal.Count);
        Assert.All(rows, r => Assert.NotNull(scenario.Journal.Find(r.Email, r.JournalScope)));
        Assert.All(rows, r => Assert.True(r.IsAlreadySent));
    }
}
