using System.Globalization;
using AdMng.Core.ActiveDirectory;
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
    public async Task Wyslij_IsTickedOnlyForAnOwnerToWriteTo_AndFollowsTheOwnerUntilTheOperatorSetsIt()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var sample = MailboxSample(scenario);
        var withoutAddress = scenario.Harness.Builder.AddUser("Bożena", "Wróbel", scenario.Company.SalesOu);   // no address at all
        using var window = await scenario.OpenAsync(new CorrespondenceRequest([], CorrespondenceStart.MailboxOwners));
        var page = window.Mailboxes;
        await UseSampleOuAsync(scenario, page, sample);
        await page.FindOwnersCommand.ExecuteAsync(null);

        // Ticked where an owner with an address was chosen; never where nothing would be sent.
        Assert.All(page.Rows, row => Assert.True(row.Include == EmailAddressText.IsValid(row.OwnerAddress), row.MailboxName));
        Assert.True(Row(page, sample.SekretariatIt).Include);                      // matched
        Assert.True(Row(page, sample.Faktury).Include);                            // probable, a swapped name
        Assert.True(Row(page, sample.Wydarzenia).Include);                         // a delegate
        Assert.False(Row(page, sample.PomocTechniczna).Include);                   // no name
        Assert.False(Row(page, sample.ProjektUe).Include);                         // not found
        Assert.False(Row(page, sample.Laboratorium).Include);                      // ambiguous, nobody chosen
        Assert.False(Row(page, sample.StaraSkrzynka).Include);                     // a disabled owner, not allowed
        Assert.All(page.Rows, row => Assert.Equal(row.Include, row.IsReady));

        // Choosing a candidate, picking a user or typing an address ticks it.
        var laboratorium = Row(page, sample.Laboratorium);
        laboratorium.SelectedCandidate = laboratorium.Candidates[0];
        Assert.True(laboratorium.Include);
        Assert.True(laboratorium.IsReady);
        var projekt = Row(page, sample.ProjektUe);
        scenario.Harness.Dialogs.PickedObjects.Enqueue([scenario.Company.PawelLewandowski]);
        await page.PickOwnerCommand.ExecuteAsync(projekt);
        Assert.True(projekt.Include);
        var pomoc = Row(page, sample.PomocTechniczna);
        scenario.Dialogs.Prompts.Enqueue("pomoc.it@contoso.test");
        await page.TypeOwnerAddressCommand.ExecuteAsync(pomoc);
        Assert.True(pomoc.Include);
        Assert.True(pomoc.IsReady);

        // Until the operator sets it, it follows the owner both ways: a person without an address clears it.
        var sekretariat = Row(page, sample.SekretariatIt);
        scenario.Harness.Dialogs.PickedObjects.Enqueue([withoutAddress]);
        await page.PickOwnerCommand.ExecuteAsync(sekretariat);
        Assert.Equal(("Bożena Wróbel", ""), (sekretariat.OwnerDisplayName, sekretariat.OwnerAddress));
        Assert.False(sekretariat.Include);
        scenario.Dialogs.Prompts.Enqueue("bozena.wrobel@poczta.pl");
        await page.TypeOwnerAddressCommand.ExecuteAsync(sekretariat);
        Assert.True(sekretariat.Include);

        // A row the operator cleared stays cleared whatever owner is chosen later.
        var archiwum = Row(page, sample.Archiwum);
        archiwum.Include = false;
        scenario.Harness.Dialogs.PickedObjects.Enqueue([scenario.Company.GrazynaDabrowska]);
        await page.PickOwnerCommand.ExecuteAsync(archiwum);
        Assert.Equal("grazyna.dabrowska@contoso.test", archiwum.OwnerAddress);
        Assert.False(archiwum.Include);
        Assert.False(archiwum.IsReady);

        // One the operator ticked stays ticked, also before it has an owner.
        var stara = Row(page, sample.StaraSkrzynka);
        stara.Include = true;
        Assert.False(stara.IsReady);
        scenario.Harness.Dialogs.PickedObjects.Enqueue([withoutAddress]);
        await page.PickOwnerCommand.ExecuteAsync(stara);
        Assert.True(stara.Include);
        stara.SelectedCandidate = stara.Candidates.Single(c => c.Person.IsDisabled);   // the operator takes the disabled owner
        Assert.True(stara.Include);
        Assert.True(stara.IsReady);
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

        // The address of someone else than the chosen person (who has an address of their own): the person goes – their
        // name, salutation and fields must not go to another address.
        var stara = Row(page, sample.StaraSkrzynka);
        stara.SelectedCandidate = stara.Candidates[0];
        scenario.Dialogs.Prompts.Enqueue("kadry@contoso.test");
        await page.TypeOwnerAddressCommand.ExecuteAsync(stara);
        Assert.Equal("kadry@contoso.test", stara.OwnerAddress);
        Assert.Null(stara.Owner);
        Assert.Null(stara.SelectedCandidate);
        Assert.Equal(string.Empty, stara.OwnerDisplayName);
        Assert.Equal("adres wpisany ręcznie", stara.DetailText);
        Assert.True(stara.IsReady);
    }

    [Fact]
    public async Task TypeOwnerAddress_OfACandidate_ChoosesThem_AndAnOwnerWithoutAnAddressIsKept()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var sample = MailboxSample(scenario);
        var builder = scenario.Harness.Builder;
        builder.AddUser("Bożena", "Wróbel", scenario.Company.SalesOu);   // no address at all
        builder.AddUser("Bożena", "Wróbel", sample.Ou, new Dictionary<string, object?>
        {
            [Attr.DisplayName] = "Biuro zarządu",
            [Attr.Mail] = "biuro.zarzadu@contoso.test",
            [MailboxTypes.RecipientTypeDetails] = FakeDirectoryBuilder.SharedMailboxType,
            [Attr.UserAccountControl] = (int)(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable),
        });
        using var window = await scenario.OpenAsync(new CorrespondenceRequest([], CorrespondenceStart.MailboxOwners));
        var page = window.Mailboxes;
        await UseSampleOuAsync(scenario, page, sample);
        await page.FindOwnersCommand.ExecuteAsync(null);

        // The owner has no address: a typed one is hers – her name and salutation go with it.
        var biuro = Assert.Single(page.Rows, r => r.MailboxName == "Biuro zarządu");
        Assert.Equal(OwnerMatchStatus.OwnerWithoutAddress, biuro.Status);
        Assert.False(biuro.IsReady);
        Assert.False(biuro.Include);
        scenario.Dialogs.Prompts.Enqueue("bozena.wrobel@poczta.pl");
        await page.TypeOwnerAddressCommand.ExecuteAsync(biuro);
        Assert.Equal(("bozena.wrobel@poczta.pl", "Bożena Wróbel"), (biuro.OwnerAddress, biuro.OwnerDisplayName));
        Assert.NotNull(biuro.Owner);
        Assert.True(biuro.Include);
        Assert.True(biuro.IsReady);

        // Two of the same name: the address of one of them chooses him.
        var laboratorium = Row(page, sample.Laboratorium);
        scenario.Dialogs.Prompts.Enqueue("Jan.Nowak2@contoso.test");
        await page.TypeOwnerAddressCommand.ExecuteAsync(laboratorium);
        Assert.True(DistinguishedName.AreEqual(sample.JanNowakSales, laboratorium.Owner!.DistinguishedName));
        Assert.False(laboratorium.HasTypedAddress);
        Assert.Equal(("jan.nowak2@contoso.test", "Sprzedaż"), (laboratorium.OwnerAddress, laboratorium.OwnerDepartment));
        Assert.Equal(MailboxOwnerRowState.Manual, laboratorium.RowState);
    }

    [Fact]
    public async Task APartialMatch_IsNeverChosenAutomatically_TheOperatorConfirmsIt()
    {
        // "Karolina Malinowska" (Projekt UE) against the only "Karolina Malinowska-Wrona": maybe the owner after a change of
        // name, maybe someone else – a match by a part of the surname is shown, never sent without the operator's choice.
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var sample = MailboxSample(scenario);
        scenario.Harness.Builder.AddUser("Karolina", "Malinowska-Wrona", scenario.Company.SalesOu,
            new Dictionary<string, object?> { [Attr.Mail] = "karolina.malinowska-wrona@contoso.test" });
        using var window = await scenario.OpenAsync(new CorrespondenceRequest([], CorrespondenceStart.MailboxOwners));
        var page = window.Mailboxes;
        await UseSampleOuAsync(scenario, page, sample);
        await page.FindOwnersCommand.ExecuteAsync(null);

        var projekt = Row(page, sample.ProjektUe);
        Assert.Equal(OwnerMatchStatus.Probable, projekt.Status);
        Assert.Equal(OwnerMatchLevel.Partial, projekt.Match.Best!.Level);
        Assert.Null(projekt.SelectedCandidate);
        Assert.False(projekt.IsReady);
        Assert.False(projekt.Include);
        Assert.True(projekt.NeedsReview);
        Assert.Equal("Prawdopodobne", projekt.StatusText);
        Assert.Equal("zgodna część nazwiska · nie wybrano automatycznie", projekt.DetailText);
        Assert.True(Row(page, sample.Faktury).IsReady);                  // a swapped name is still chosen (design note 3.7)

        await page.AddToRecipientsCommand.ExecuteAsync(null);
        Assert.DoesNotContain(window.Recipients.Items, r => r.Email == "karolina.malinowska-wrona@contoso.test");

        projekt.SelectedCandidate = projekt.Candidates[0];                // the operator confirms her
        Assert.Equal(MailboxOwnerRowState.Manual, projekt.RowState);
        Assert.True(projekt.Include);
        Assert.True(projekt.IsReady);
        Assert.False(projekt.NeedsReview);
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
        Assert.True(stara.Include);                                       // disabled owners are allowed
        Assert.True(stara.IsReady);
        Assert.True(stara.NeedsReview);
        Assert.Equal(MailboxRowStatusKind.Warning, stara.StatusKind);
        Assert.Equal("zgodne imię i nazwisko · konto właściciela jest wyłączone", stara.DetailText);
    }

    [Theory]
    [InlineData(true, OwnerMatchStatus.Matched)]
    [InlineData(false, OwnerMatchStatus.Ambiguous)]
    public async Task TwoOwnersOfTheSameName_OneDisabled_TheEnabledOneWinsOnlyWhileDisabledOwnersAreNotAllowed(bool enabledOnly, OwnerMatchStatus expected)
    {
        // docs/KORESPONDENCJA.md: with several candidates the only enabled account wins with "Właścicielem może być tylko
        // włączone konto"; otherwise the result is ambiguous (the disabled one may be the owner).
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var sample = MailboxSample(scenario);
        scenario.Harness.Builder.AddUser("Ewa", "Zielińska", scenario.Company.SalesOu, new Dictionary<string, object?>
        {
            [Attr.Mail] = "ewa.zielinska2@contoso.test",
            [Attr.UserAccountControl] = (int)(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable),
        });
        using var window = await scenario.OpenAsync(new CorrespondenceRequest([], CorrespondenceStart.MailboxOwners));
        await UseSampleOuAsync(scenario, window.Mailboxes, sample);
        window.Mailboxes.EnabledOwnersOnly = enabledOnly;

        await window.Mailboxes.FindOwnersCommand.ExecuteAsync(null);

        var kadry = Row(window.Mailboxes, sample.DzialKadr);
        Assert.Equal(2, kadry.Candidates.Count);
        Assert.Equal(expected, kadry.Status);
        Assert.Equal(enabledOnly, kadry.IsReady);
    }

    [Fact]
    public async Task AddToRecipients_OneMessagePerOwner_WithTheMailboxAndOwnerFields()
    {
        var (scenario, window, sample) = await RunAsync(page => page.GroupByOwner = true);
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

        // The empty message gets the built-in template that fits the grouping: one message per owner.
        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededGroupedId, window.Message.SelectedTemplate?.Id);
        Assert.Equal("Weryfikacja skrzynek pocztowych", window.Message.Subject);
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
    public async Task AddToRecipients_OneMessagePerMailbox_IsKept_WhenTheTemplateIsLoadedAfterAQuestion()
    {
        var (scenario, window, _) = await RunAsync(page => page.GroupByOwner = false);
        using var __ = scenario;
        using var ___ = window;
        window.Message.Subject = "Skrzynki do przeglądu";              // a subject, an empty body: loading asks first
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Harness.Dialogs.BeforeConfirmAnswer = () => answer.Task;
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);

        var add = window.Mailboxes.AddToRecipientsCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => scenario.Harness.Dialogs.Shown.Count > 0);
        answer.SetResult();
        await add;
        await (window.Message.TemplateLoading ?? Task.CompletedTask);

        Assert.StartsWith("confirm:", Assert.Single(scenario.Harness.Dialogs.Shown), StringComparison.Ordinal);
        Assert.Contains("wskazana w temacie tej wiadomości", window.Message.BodyHtml, StringComparison.Ordinal);
        Assert.Equal("Weryfikacja skrzynki pocztowej: {{Skrzynka}}", window.Message.Subject);
        Assert.False(window.Message.GroupByAddress);                     // "Jedna wiadomość na właściciela" was off
        Assert.Equal(9, window.Recipients.Units(window.Message.GroupByAddress).Count);
    }

    [Fact]
    public async Task AddToRecipients_AgainWithAnEmptiedMessage_LoadsTheTemplateAgain()
    {
        var (scenario, window, _) = await RunAsync();
        using var __ = scenario;
        using var ___ = window;
        await window.Mailboxes.AddToRecipientsCommand.ExecuteAsync(null);
        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededId, window.Message.SelectedTemplate?.Id);
        window.Message.BodyHtml = string.Empty;                          // the operator emptied the body by hand
        window.Message.Subject = string.Empty;

        await window.Mailboxes.AddToRecipientsCommand.ExecuteAsync(null);

        Assert.Contains("wskazana w temacie tej wiadomości", window.Message.BodyHtml, StringComparison.Ordinal);
        Assert.Equal("Weryfikacja skrzynki pocztowej: {{Skrzynka}}", window.Message.Subject);
        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededId, window.Message.SelectedTemplate?.Id);
    }

    [Fact]
    public async Task GroupByOwner_IsOffByDefault_OneMessagePerMailboxWithItsNameInTheSubject()
    {
        var (scenario, window, _) = await RunAsync();
        using var __ = scenario;
        using var ___ = window;
        Assert.False(window.Mailboxes.GroupByOwner);

        await window.Mailboxes.AddToRecipientsCommand.ExecuteAsync(null);

        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededId, window.Message.SelectedTemplate?.Id);
        Assert.False(window.Message.GroupByAddress);
        Assert.False(window.Message.UseVoting);
        var mailboxRows = window.Recipients.Items.Where(r => r.Origin == RecipientOrigin.Mailboxes).ToList();
        Assert.Equal(mailboxRows.Count, window.Recipients.Units(window.Message.GroupByAddress).Count);
        var draft = await window.Message.DraftAsync();
        var first = window.Recipients.RecipientUnits(false)[0].Unit;
        var rendered = Core.Correspondence.Composition.CorrespondenceRenderer.Render(draft, first, window.Recipients.KnownColumns);
        Assert.Equal("Weryfikacja skrzynki pocztowej: " + first.Rows[0]["Skrzynka"], rendered.Subject);
        Assert.Contains("<b>27 października 2026r.</b>", rendered.HtmlDocument, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddingAgainWithTheOtherGrouping_SwitchesAnUnchangedBuiltIn_KeepingTheDeadline()
    {
        var (scenario, window, _) = await RunAsync();
        using var __ = scenario;
        using var ___ = window;
        await window.Mailboxes.AddToRecipientsCommand.ExecuteAsync(null);
        var termin = window.Message.CustomFields[0];
        Assert.Equal("Termin", termin.Name);
        termin.Date = new DateTime(2026, 12, 4);                         // the operator chose the deadline

        // The same grouping again: the built-in that fits stays as it is (the deadline too).
        await window.Mailboxes.AddToRecipientsCommand.ExecuteAsync(null);
        Assert.Same(termin, window.Message.CustomFields[0]);
        Assert.Equal(3, window.Message.CustomFields.Count);

        // Grouped now: the other built-in replaces the unchanged one without a question, the deadline is kept.
        window.Mailboxes.GroupByOwner = true;
        await window.Mailboxes.AddToRecipientsCommand.ExecuteAsync(null);

        Assert.Empty(scenario.Harness.Dialogs.Shown);
        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededGroupedId, window.Message.SelectedTemplate?.Id);
        Assert.Equal("Weryfikacja skrzynek pocztowych", window.Message.Subject);
        Assert.True(window.Message.GroupByAddress);
        Assert.Equal(3, window.Message.CustomFields.Count);
        Assert.Equal(new DateTime(2026, 12, 4), window.Message.CustomFields[0].Date);
        Assert.Equal("4 grudnia 2026r.", window.Message.CustomFields[0].Sample);

        // A message of the operator's is never replaced.
        window.Message.BodyHtml += "<p>Dopisek operatora.</p>";
        window.Mailboxes.GroupByOwner = false;
        await window.Mailboxes.AddToRecipientsCommand.ExecuteAsync(null);
        Assert.Equal("Weryfikacja skrzynek pocztowych", window.Message.Subject);
        Assert.Empty(scenario.Harness.Dialogs.Shown);
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
