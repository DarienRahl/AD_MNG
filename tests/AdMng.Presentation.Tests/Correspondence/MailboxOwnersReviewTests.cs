using System.Globalization;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Ldap;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using static AdMng.Presentation.Tests.Correspondence.PageTests;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// "Skrzynki", the review and the hand-off (design note 6.4): a candidate chosen in the row's box, any user picked, an
/// address typed, exclude and restore, disabled owners allowed, then "Dodaj właścicieli do odbiorców" – recipient rows with
/// the mailbox and owner fields and the mailbox keys, one message per owner, the built-in template for an empty message
/// and the switch to the recipients tab.
/// </summary>
public sealed class MailboxOwnersReviewTests
{
    private static async Task<(CorrespondenceShellScenario Scenario, CorrespondenceViewModel Window, MailboxOwnerSample Sample)> RunAsync(
        Action<MailboxOwnersPageViewModel>? before = null, CultureInfo? culture = null)
    {
        var scenario = await CorrespondenceShellScenario.CreateAsync(culture ?? Polish);
        var sample = MailboxSample(scenario);
        var window = await scenario.OpenAsync(new CorrespondenceRequest([], CorrespondenceStart.MailboxOwners));
        await UseSampleOuAsync(scenario, window.Mailboxes, sample);
        before?.Invoke(window.Mailboxes);
        await window.Mailboxes.FindOwnersCommand.ExecuteAsync(null);
        return (scenario, window, sample);
    }

    [Fact]
    public async Task ChoosingACandidate_MakesTheRowManual_AndTheAutomaticOneTakesItBack()
    {
        var (scenario, window, sample) = await RunAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        var laboratorium = Row(page, sample.Laboratorium);
        Assert.Null(laboratorium.SelectedCandidate);
        Assert.False(laboratorium.IsReady);

        var sales = laboratorium.Candidates.Single(c => DistinguishedName.AreEqual(c.Person.DistinguishedName, sample.JanNowakSales));
        laboratorium.SelectedCandidate = sales;

        Assert.Equal(MailboxOwnerRowState.Manual, laboratorium.RowState);
        Assert.Equal("jan.nowak2@contoso.test", laboratorium.OwnerAddress);
        Assert.Equal("Sprzedaż", laboratorium.OwnerDepartment);
        Assert.Equal("Wybrano ręcznie", laboratorium.StatusText);
        Assert.Equal(MailboxRowStatusKind.Ok, laboratorium.StatusKind);
        Assert.Equal("wybrano ręcznie · zgodne imię i nazwisko", laboratorium.DetailText);
        Assert.True(laboratorium.IsReady);
        Assert.False(laboratorium.NeedsReview);
        Assert.Equal("Dopasowane: 9 · do sprawdzenia: 2 · nieznalezione: 2 · wykluczone: 0", page.SummaryText);

        // A recycled grid row clears its box: ignored.
        laboratorium.SelectedCandidate = null;
        Assert.Equal("jan.nowak2@contoso.test", laboratorium.OwnerAddress);

        var faktury = Row(page, sample.Faktury);
        var automatic = faktury.SelectedCandidate!;
        scenario.Harness.Dialogs.PickedObjects.Enqueue([scenario.Company.GrazynaDabrowska]);
        await page.PickOwnerCommand.ExecuteAsync(faktury);
        Assert.Equal(MailboxOwnerRowState.Manual, faktury.RowState);
        Assert.Equal(2, faktury.Candidates.Count);
        Assert.Equal("grazyna.dabrowska@contoso.test", faktury.OwnerAddress);
        faktury.SelectedCandidate = automatic;
        Assert.Equal(MailboxOwnerRowState.Automatic, faktury.RowState);
        Assert.Equal("malgorzata.wojcik@contoso.test", faktury.OwnerAddress);
        Assert.Equal("Prawdopodobne", faktury.StatusText);
        Assert.Equal("zamienione imię i nazwisko", faktury.DetailText);
    }

    [Fact]
    public async Task PickOwner_AddsAnyUserAsTheOwner()
    {
        var (scenario, window, sample) = await RunAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        var projekt = Row(page, sample.ProjektUe);
        Assert.False(projekt.HasCandidates);
        scenario.Harness.Dialogs.PickedObjects.Enqueue([scenario.Company.PawelLewandowski]);
        var changed = new List<string?>();
        projekt.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await page.PickOwnerCommand.ExecuteAsync(projekt);

        Assert.True(projekt.HasCandidates);
        Assert.Contains(nameof(MailboxOwnerRowViewModel.HasCandidates), changed);
        var chosen = Assert.Single(projekt.Candidates);
        Assert.Same(chosen, projekt.SelectedCandidate);
        Assert.Equal("Paweł Lewandowski – Sprzedaż, pawel.lewandowski@contoso.test", chosen.Text);
        Assert.Equal(OwnerMatchLevel.Partial, chosen.Level);   // no level: another name
        Assert.Equal(MailboxOwnerRowState.Manual, projekt.RowState);
        Assert.Equal("pawel.lewandowski@contoso.test", projekt.OwnerAddress);
        Assert.True(projekt.IsReady);

        // A candidate of the row picked through the picker is the candidate itself.
        var laboratorium = Row(page, sample.Laboratorium);
        scenario.Harness.Dialogs.PickedObjects.Enqueue([sample.JanNowakIt]);
        await page.PickOwnerCommand.ExecuteAsync(laboratorium);
        Assert.Equal(2, laboratorium.Candidates.Count);
        Assert.True(DistinguishedName.AreEqual(sample.JanNowakIt, laboratorium.Owner!.DistinguishedName));
        Assert.Equal(OwnerMatchLevel.Exact, laboratorium.SelectedCandidate!.Level);

        // Cancelled picker: nothing changes.
        await page.PickOwnerCommand.ExecuteAsync(Row(page, sample.PomocTechniczna));
        Assert.Equal(MailboxOwnerRowState.Automatic, Row(page, sample.PomocTechniczna).RowState);

        // Picking the automatic owner again (after another one) returns the row to the matcher's result.
        var sekretariat = Row(page, sample.SekretariatIt);
        scenario.Harness.Dialogs.PickedObjects.Enqueue([scenario.Company.GrazynaDabrowska]);
        await page.PickOwnerCommand.ExecuteAsync(sekretariat);
        Assert.Equal(MailboxOwnerRowState.Manual, sekretariat.RowState);
        scenario.Harness.Dialogs.PickedObjects.Enqueue([scenario.Company.JanKowalski]);
        await page.PickOwnerCommand.ExecuteAsync(sekretariat);
        Assert.Equal(MailboxOwnerRowState.Automatic, sekretariat.RowState);
        Assert.Same(sekretariat.AutomaticCandidate, sekretariat.SelectedCandidate);
        Assert.Equal("Dopasowano", sekretariat.StatusText);
    }

    [Fact]
    public async Task TypeOwnerAddress_IsValidated()
    {
        var (scenario, window, sample) = await RunAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        var pomoc = Row(page, sample.PomocTechniczna);
        scenario.Dialogs.Prompts.Enqueue("helpdesk@");
        scenario.Dialogs.Prompts.Enqueue(" Pomoc.IT@Contoso.test ");

        await page.TypeOwnerAddressCommand.ExecuteAsync(pomoc);
        Assert.Equal(("Adres właściciela", "„helpdesk@” nie jest poprawnym adresem e-mail."), Assert.Single(scenario.Harness.Dialogs.Errors));
        Assert.Equal(MailboxOwnerRowState.Automatic, pomoc.RowState);

        await page.TypeOwnerAddressCommand.ExecuteAsync(pomoc);
        Assert.Equal("pomoc.it@contoso.test", pomoc.OwnerAddress);
        Assert.True(pomoc.HasTypedAddress);
        Assert.Equal(MailboxOwnerRowState.Manual, pomoc.RowState);
        Assert.Equal("adres wpisany ręcznie", pomoc.DetailText);
        Assert.True(pomoc.IsReady);
        Assert.Equal(MailboxRowStatusKind.Ok, pomoc.StatusKind);

        // A typed address wins over the chosen person's address.
        var stara = Row(page, sample.StaraSkrzynka);
        stara.SelectedCandidate = stara.Candidates[0];
        scenario.Dialogs.Prompts.Enqueue("kadry@contoso.test");
        await page.TypeOwnerAddressCommand.ExecuteAsync(stara);
        Assert.Equal("kadry@contoso.test", stara.OwnerAddress);
        Assert.Equal("Zbigniew Kaźmierczak", stara.OwnerDisplayName);
        Assert.StartsWith("adres wpisany ręcznie · zgodne imię i nazwisko · konto właściciela jest wyłączone", stara.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExcludeAndRestore()
    {
        var (scenario, window, sample) = await RunAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        var sekretariat = Row(page, sample.SekretariatIt);
        var laboratorium = Row(page, sample.Laboratorium);
        laboratorium.SelectedCandidate = laboratorium.Candidates[0];

        page.ExcludeCommand.Execute(sekretariat);
        page.ExcludeCommand.Execute(laboratorium);

        Assert.Equal(MailboxOwnerRowState.Excluded, sekretariat.RowState);
        Assert.False(sekretariat.IsReady);
        Assert.Equal("Wykluczona", sekretariat.StatusText);
        Assert.Equal("wykluczona z wysyłki", sekretariat.DetailText);
        Assert.Equal("Dopasowane: 7 · do sprawdzenia: 2 · nieznalezione: 2 · wykluczone: 2", page.SummaryText);
        page.Filter = page.FilterChoices.Single(c => c.Value == MailboxRowFilter.Excluded);
        Assert.Equal(2, page.View.Count);

        page.RestoreCommand.Execute(sekretariat);
        page.RestoreCommand.Execute(laboratorium);
        Assert.Equal(MailboxOwnerRowState.Automatic, sekretariat.RowState);
        Assert.Equal(MailboxOwnerRowState.Manual, laboratorium.RowState);
        Assert.Equal("Dopasowano", sekretariat.StatusText);
        page.Filter = page.FilterChoices.Single(c => c.Value == MailboxRowFilter.All);
        Assert.Equal(page.Rows.Count, page.View.Count);
    }

    [Fact]
    public async Task DisabledOwnersAllowed_TheDisabledOwnerIsChosen()
    {
        var (scenario, window, sample) = await RunAsync(page => page.EnabledOwnersOnly = false);
        using var _ = scenario;
        using var __ = window;
        var stara = Row(window.Mailboxes, sample.StaraSkrzynka);

        Assert.Equal(OwnerMatchStatus.OwnerDisabled, stara.Status);
        Assert.Equal("zbigniew.kazmierczak@contoso.test", stara.OwnerAddress);
        Assert.True(stara.IsReady);
        Assert.True(stara.NeedsReview);
        Assert.Equal(MailboxRowStatusKind.Warning, stara.StatusKind);
        Assert.Equal("zgodne imię i nazwisko · konto właściciela jest wyłączone", stara.DetailText);
    }

    [Fact]
    public async Task AddToRecipients_OneMessagePerOwner_WithTheMailboxAndOwnerFields()
    {
        var (scenario, window, sample) = await RunAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        var recipients = window.Recipients;
        recipients.AddRows([new ImportedContact("anna.zielinska@urzad.pl", "Anna Zielińska", new Dictionary<string, string>())], "kontakty.csv");
        var laboratorium = Row(page, sample.Laboratorium);
        scenario.Harness.Dialogs.PickedObjects.Enqueue([scenario.Company.JanKowalski]);
        await page.PickOwnerCommand.ExecuteAsync(laboratorium);   // Jan Kowalski now owns two mailboxes
        Row(page, sample.Faktury).Include = false;
        window.SelectedTab = CorrespondenceTab.Mailboxes;
        var added = 0;
        page.RecipientsAdded += (_, _) => added++;

        await page.AddToRecipientsCommand.ExecuteAsync(null);

        Assert.Equal(1, added);
        Assert.Equal(CorrespondenceTab.Recipients, window.SelectedTab);
        var mailboxRows = recipients.Items.Where(r => r.Origin == RecipientOrigin.Mailboxes).ToList();
        Assert.Equal(9, mailboxRows.Count);   // 8 matched (Faktury left out) + Laboratorium
        Assert.All(mailboxRows, r => Assert.Equal("Skrzynki", r.Source));
        var jan = mailboxRows.Where(r => r.Email == "jan.kowalski@contoso.test").ToList();
        Assert.Equal(["sekretariat.it@contoso.test", "lab@contoso.test"], jan.Select(r => Assert.Single(r.MailboxKeys)));
        var first = jan[0];
        Assert.Equal("Jan Kowalski", first.Name);
        Assert.True(DistinguishedName.AreEqual(scenario.Company.JanKowalski, first.DistinguishedName));
        Assert.Equal("Sekretariat IT", first["Skrzynka"]);
        Assert.Equal("sekretariat.it@contoso.test", first["Adres skrzynki"]);
        Assert.Equal("Skrzynka współdzielona: Sekretariat IT", first["Opis skrzynki"]);
        Assert.Equal("Skrzynki", first["Jednostka"]);
        Assert.Equal("13 października 2026", first["Utworzona"]);
        Assert.Equal(string.Empty, first["Ostatnie logowanie"]);
        Assert.Equal("Jan", first["Imię"]);
        Assert.Equal("Administrator systemów", first["Stanowisko"]);
        Assert.Equal("Szanowny Panie", first["Zwrot osobowy"]);
        Assert.Equal("Pana", first["Pana/Panią"]);
        Assert.Equal("Laboratorium", jan[1]["Skrzynka"]);
        var dr = Assert.Single(mailboxRows, r => r.MailboxKeys.Contains("biuro.projektu@contoso.test"));
        Assert.Equal("Szanowna Pani Doktor", dr["Zwrot osobowy"]);
        Assert.Equal("Panią", dr["Pana/Panią"]);
        Assert.Contains("Skrzynka", recipients.Columns);
        Assert.Contains("Zwrot osobowy", recipients.Columns);

        // Grouping by owner: one message for Jan with both mailboxes.
        Assert.True(window.Message.GroupByAddress);
        var units = recipients.RecipientUnits(window.Message.GroupByAddress);
        var janUnit = Assert.Single(units, u => u.Unit.Email == "jan.kowalski@contoso.test");
        Assert.Equal(2, janUnit.Unit.Rows.Count);
        Assert.Equal(["sekretariat.it@contoso.test", "lab@contoso.test"], janUnit.Keys);
        Assert.Equal(9, units.Count);   // 8 owners + the imported row

        // The empty message gets the built-in template "is the mailbox still needed?".
        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededId, window.Message.SelectedTemplate?.Id);
        Assert.Equal("Dodano do odbiorców: skrzynki – 9, właściciele – 8.", page.StatusText);
        Assert.Equal(page.StatusText, recipients.StatusText);
        Assert.Equal(page.StatusText, window.StatusText);

        // Again: the earlier mailbox rows are replaced, the other rows stay.
        page.ExcludeCommand.Execute(Row(page, sample.Wnioski));
        await page.AddToRecipientsCommand.ExecuteAsync(null);
        Assert.Equal(8, recipients.Items.Count(r => r.Origin == RecipientOrigin.Mailboxes));
        Assert.Single(recipients.Items, r => r.Email == "anna.zielinska@urzad.pl");
        Assert.Equal(2, added);
    }

    [Fact]
    public async Task AddToRecipients_WithoutGrouping_AndAMessage_KeepsTheMessage()
    {
        var (scenario, window, _) = await RunAsync(page => page.GroupByOwner = false);
        using var __ = scenario;
        using var ___ = window;
        window.Message.BodyHtml = "<p>Mój tekst</p>";
        window.Message.GroupByAddress = true;

        await window.Mailboxes.AddToRecipientsCommand.ExecuteAsync(null);

        Assert.False(window.Message.GroupByAddress);
        Assert.Null(window.Message.SelectedTemplate);
        Assert.Equal(9, window.Recipients.Units(window.Message.GroupByAddress).Count);
    }

    [Fact]
    public async Task AddToRecipients_ATypedAddressWithoutAPerson_HasTheMailboxFields_AndEmptyOwnerFields()
    {
        var (scenario, window, sample) = await RunAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        var pomoc = Row(page, sample.PomocTechniczna);
        foreach (var other in page.Rows.Where(r => !ReferenceEquals(r, pomoc)))
        {
            page.ExcludeCommand.Execute(other);
        }

        scenario.Dialogs.Prompts.Enqueue("helpdesk@contoso.test");
        await page.TypeOwnerAddressCommand.ExecuteAsync(pomoc);

        await page.AddToRecipientsCommand.ExecuteAsync(null);

        var recipient = Assert.Single(window.Recipients.Items);
        Assert.Equal("helpdesk@contoso.test", recipient.Email);
        Assert.Equal(string.Empty, recipient.Name);
        Assert.Null(recipient.DistinguishedName);
        Assert.Equal(["pomoc@contoso.test"], recipient.MailboxKeys);
        Assert.Equal("Pomoc techniczna", recipient["Skrzynka"]);
        Assert.Equal(string.Empty, recipient.Fields["Imię"]);   // known, so {{Imię}} renders empty without a warning
        Assert.Contains("Imię", window.Recipients.KnownColumns);
        Assert.Equal("Dodano do odbiorców: skrzynki – 1, właściciele – 1.", page.StatusText);
        Assert.False(page.IsRunning);
    }

    [Fact]
    public async Task AddToRecipients_FieldsAddedAfterTheQuery_AreReadToo()
    {
        var (scenario, window, _) = await RunAsync();
        using var __ = scenario;
        using var ___ = window;
        var page = window.Mailboxes;
        page.AddMailboxFieldCommand.Execute(null);
        page.MailboxFields[^1].Name = "Nazwa obiektu";
        page.MailboxFields[^1].Expression = "{cn}";
        var mailboxReads = new List<string>();
        scenario.Harness.Session.BeforeOperation = operation =>
        {
            if (operation.Search is { } search && search.Filter.Contains("distinguishedName=", StringComparison.OrdinalIgnoreCase)
                && search.Filter.Contains("Skrzynki", StringComparison.Ordinal))
            {
                mailboxReads.Add(string.Join(",", search.Attributes));
            }
        };

        await page.AddToRecipientsCommand.ExecuteAsync(null);

        Assert.Contains("cn", Assert.Single(mailboxReads).Split(','), StringComparer.OrdinalIgnoreCase);
        var sekretariat = Assert.Single(window.Recipients.Items, r => r.MailboxKeys.Contains("sekretariat.it@contoso.test"));
        Assert.Equal("Sekretariat IT", sekretariat["Nazwa obiektu"]);
        Assert.Equal("Sekretariat IT", sekretariat["Skrzynka"]);
        Assert.Equal("Jan", sekretariat["Imię"]);
        Assert.Contains("Nazwa obiektu", window.Recipients.Columns);
    }

    [Fact]
    public async Task AddToRecipients_CanBeCancelled_WhileTheOwnersAreRead()
    {
        var (scenario, window, _) = await RunAsync();
        using var __ = scenario;
        using var ___ = window;
        var page = window.Mailboxes;
        var progress = page.ProgressText;
        var seen = new List<string>();
        scenario.Harness.Session.BeforeOperation = operation =>
        {
            if (operation.Search?.Filter.Contains("distinguishedName=", StringComparison.OrdinalIgnoreCase) == true)
            {
                seen.Add(page.ProgressText);
                Assert.True(page.IsRunning);
                Assert.True(page.CancelCommand.CanExecute(null));
                page.CancelCommand.Execute(null);
            }
        };

        await page.AddToRecipientsCommand.ExecuteAsync(null);

        Assert.Equal(["Odczytuję dane właścicieli (9)…"], seen);
        Assert.Empty(window.Recipients.Items);
        Assert.False(page.IsRunning);
        Assert.Equal(progress, page.ProgressText);
        Assert.Equal("Przerwano – nie dodano właścicieli do odbiorców", page.StatusText);
        Assert.Equal(page.StatusText, window.StatusText);
        Assert.Equal(CorrespondenceTab.Mailboxes, window.SelectedTab);
        Assert.Null(window.Message.SelectedTemplate);
        Assert.True(page.AddToRecipientsCommand.CanExecute(null));
    }

    [Fact]
    public async Task AddToRecipients_TheSessionEnding_StopsTheRead()
    {
        var (scenario, window, _) = await RunAsync();
        using var __ = scenario;
        using var ___ = window;
        var page = window.Mailboxes;
        var release = new TaskCompletionSource();
        scenario.Harness.Session.BeforeOperation = operation =>
        {
            if (operation.Search?.Filter.Contains("distinguishedName=", StringComparison.OrdinalIgnoreCase) == true)
            {
                release.Task.Wait(TestContext.Current.CancellationToken);
            }
        };

        var add = page.AddToRecipientsCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => page.IsRunning);
        await scenario.Harness.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => !window.Recipients.CanUseDirectory);
        release.SetResult();
        await add;

        Assert.False(page.IsRunning);
        Assert.Empty(window.Recipients.Items);
        Assert.Equal(CorrespondenceTab.Mailboxes, window.SelectedTab);
    }

    [Fact]
    public async Task AddToRecipients_WithoutAReadyOwner_SaysSo()
    {
        var (scenario, window, _) = await RunAsync();
        using var __ = scenario;
        using var ___ = window;
        var page = window.Mailboxes;
        foreach (var row in page.Rows)
        {
            page.ExcludeCommand.Execute(row);
        }

        await page.AddToRecipientsCommand.ExecuteAsync(null);

        Assert.Equal(["info:Nie ma właścicieli do dodania – wybierz właścicieli albo wpisz ich adresy."], scenario.Harness.Dialogs.Shown);
        Assert.Empty(window.Recipients.Items);
        Assert.Equal(CorrespondenceTab.Mailboxes, window.SelectedTab);
    }

    [Fact]
    public async Task TheLanguage_FollowsTheWindow()
    {
        var (scenario, window, sample) = await RunAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        Row(page, sample.Laboratorium).SelectedCandidate = Row(page, sample.Laboratorium).Candidates[0];
        window.Recipients.AddRows([new ImportedContact("anna.zielinska@urzad.pl", "Anna Zielińska", new Dictionary<string, string>())], "kontakty.csv");

        scenario.Harness.Localizer.SetCulture(English);

        Assert.Equal("Shared mailboxes", page.Selection.Text);
        Assert.Equal("All", page.Filter.Text);
        Assert.Equal("the given name and surname (givenName, sn)", page.NameSource.Text);
        Assert.Equal("Matched", Row(page, sample.SekretariatIt).StatusText);
        Assert.Equal("Chosen by hand", Row(page, sample.Laboratorium).StatusText);
        Assert.Equal("Matched: 9 · to review: 2 · not found: 2 · excluded: 0", page.SummaryText);
        Assert.Equal("Recipients: 1 • to send: 1", window.Recipients.CountsText);
        Assert.Equal("Skipped: 0 of your own + 0 from the journal", window.Recipients.ExclusionsText);
    }
}
