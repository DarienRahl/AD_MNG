using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using static AdMng.Presentation.Tests.Correspondence.PageTests;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// "Skrzynki", the query (design note 6.4, 3.7 and the sample of section 10): the scope (OU, selection, domain), the mailbox
/// types, the operator's filter, the owner name source, the person base, the run streamed from the window's session with
/// progress, its limits and cancellation, a directory without the Exchange schema, and the settings.
/// </summary>
public sealed class MailboxOwnersPageTests
{
    private static async Task<(CorrespondenceShellScenario Scenario, CorrespondenceViewModel Window, MailboxOwnerSample Sample)> OpenAsync(
        CorrespondenceRequest? request = null)
    {
        var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var sample = MailboxSample(scenario);
        var window = await scenario.OpenAsync(request ?? new CorrespondenceRequest([], CorrespondenceStart.MailboxOwners));
        return (scenario, window, sample);
    }

    [Fact]
    public async Task TheSampleOfTheDesignNote_MatchesEveryMailboxAsExpected()
    {
        var (scenario, window, sample) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        Assert.True(page.IsExchangeSchemaPresent);
        await UseSampleOuAsync(scenario, page, sample);
        page.Selection = page.SelectionChoices.Single(c => c.Value == MailboxSelection.SharedRoomsAndEquipment);

        await page.FindOwnersCommand.ExecuteAsync(null);

        Assert.False(page.IsRunning);
        Assert.Null(page.Error);
        Assert.Equal(sample.Mailboxes.Count, page.Rows.Count);
        foreach (var expected in sample.Expected)
        {
            var row = Row(page, expected.Mailbox);
            Assert.True(expected.Status == row.Status, $"{row.MailboxName}: {row.Status}, expected {expected.Status}");
            Assert.Equal(MailboxOwnerRowState.Automatic, row.RowState);
            var chosen = expected.Status is OwnerMatchStatus.Matched or OwnerMatchStatus.MatchedByDelegate or OwnerMatchStatus.Probable;
            if (chosen)
            {
                Assert.True(DistinguishedName.AreEqual(expected.Owner, row.Owner?.DistinguishedName), row.MailboxName);
                Assert.Equal(expected.Level, row.SelectedCandidate!.Level);
                Assert.True(row.IsReady, row.MailboxName);
            }
            else
            {
                Assert.Null(row.Owner);
                Assert.False(row.IsReady, row.MailboxName);
            }
        }

        var sekretariat = Row(page, sample.SekretariatIt);
        Assert.Equal("Sekretariat IT", sekretariat.MailboxName);
        Assert.Equal("sekretariat.it@contoso.test", sekretariat.MailboxAddress);
        Assert.Equal("contoso.test/Firma/Skrzynki", sekretariat.MailboxLocation);
        Assert.Equal("skrzynka współdzielona", sekretariat.MailboxTypeText);
        Assert.Equal("Jan Kowalski", sekretariat.OwnerNameText);
        Assert.Equal("Jan Kowalski", sekretariat.OwnerDisplayName);
        Assert.Equal("jan.kowalski@contoso.test", sekretariat.OwnerAddress);
        Assert.Equal("IT", sekretariat.OwnerDepartment);
        Assert.Equal("Dopasowano", sekretariat.StatusText);
        Assert.Equal(MailboxRowStatusKind.Ok, sekretariat.StatusKind);
        Assert.Equal("zgodne imię i nazwisko", sekretariat.DetailText);
        Assert.Equal("Jan Kowalski – IT, jan.kowalski@contoso.test", sekretariat.SelectedCandidate!.Text);
        Assert.False(sekretariat.NeedsReview);

        Assert.Equal("sala", Row(page, sample.Sala101).MailboxTypeText);
        var rekrutacja = Row(page, sample.Rekrutacja);
        Assert.Equal("zgodne bez polskich znaków", rekrutacja.DetailText);
        var faktury = Row(page, sample.Faktury);
        Assert.Equal("Prawdopodobne", faktury.StatusText);
        Assert.Equal(MailboxRowStatusKind.Warning, faktury.StatusKind);
        Assert.True(faktury.NeedsReview);
        var wydarzenia = Row(page, sample.Wydarzenia);
        Assert.True(wydarzenia.SelectedCandidate!.IsDelegate);
        Assert.Equal("Dopasowano (dostęp do skrzynki)", wydarzenia.StatusText);
        Assert.Contains("ma pełny dostęp do skrzynki lub wysyła w jej imieniu", wydarzenia.DetailText, StringComparison.Ordinal);
        Assert.Contains("ma dostęp do skrzynki", wydarzenia.SelectedCandidate.Text, StringComparison.Ordinal);

        var stara = Row(page, sample.StaraSkrzynka);
        Assert.Equal("Właściciel wyłączony", stara.StatusText);
        Assert.True(DistinguishedName.AreEqual(scenario.Company.ZbigniewKazmierczak, stara.Match.Best?.Person.DistinguishedName));
        Assert.Equal("zgodne imię i nazwisko · konto właściciela jest wyłączone · nie wybrano automatycznie", stara.DetailText);
        Assert.Contains("konto wyłączone", stara.Candidates[0].Text, StringComparison.Ordinal);

        var laboratorium = Row(page, sample.Laboratorium);
        Assert.Equal(2, laboratorium.Candidates.Count);
        Assert.Equal("kandydaci: 2 – wybierz właściciela", laboratorium.DetailText);
        Assert.Equal("Niejednoznaczne", laboratorium.StatusText);

        var pomoc = Row(page, sample.PomocTechniczna);
        Assert.Equal(MailboxRowStatusKind.Error, pomoc.StatusKind);
        Assert.Equal("skrzynka nie zawiera imienia i nazwiska właściciela", pomoc.DetailText);
        Assert.Equal("nie znaleziono osoby „Karolina Malinowska”", Row(page, sample.ProjektUe).DetailText);
        Assert.Equal("agnieszka.zolc@contoso.test", Row(page, sample.Wnioski).OwnerAddress);

        Assert.Equal(sample.Mailboxes.Count, page.View.Count);
        Assert.StartsWith("Skrzynki: 14 · osoby: ", page.ProgressText, StringComparison.Ordinal);
        Assert.Equal("Dopasowane: 9 · do sprawdzenia: 3 · nieznalezione: 2 · wykluczone: 0", page.SummaryText);
        Assert.True(page.AddToRecipientsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Selection_SharedMailboxes_LeaveTheRoomOut_AndTheFilterNarrowsTheRows()
    {
        var (scenario, window, sample) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await UseSampleOuAsync(scenario, page, sample);

        await page.FindOwnersCommand.ExecuteAsync(null);

        Assert.Equal(13, page.Rows.Count);
        Assert.DoesNotContain(page.Rows, r => DistinguishedName.AreEqual(r.Mailbox.DistinguishedName, sample.Sala101));
        page.Filter = page.FilterChoices.Single(c => c.Value == MailboxRowFilter.NotFound);
        Assert.Equal(["Pomoc techniczna", "Projekt UE"], page.View.Select(r => r.MailboxName).Order(StringComparer.Ordinal));
        page.Filter = page.FilterChoices.Single(c => c.Value == MailboxRowFilter.NeedsReview);
        Assert.Equal(["Księgowość – faktury", "Laboratorium", "Pomoc techniczna", "Projekt UE", "Stara skrzynka"],
            page.View.Select(r => r.MailboxName).Order(StringComparer.Ordinal));
        page.Filter = page.FilterChoices.Single(c => c.Value == MailboxRowFilter.Ready);
        Assert.Equal(9, page.View.Count);   // the matched ones and the probable one, chosen automatically
        Assert.All(page.View, r => Assert.True(r.IsReady));
    }

    [Fact]
    public async Task Selection_TheObjectsOfTheRequest_AreTheScope()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var sample = MailboxSample(scenario);
        using var window = await scenario.OpenAsync(new CorrespondenceRequest([sample.SekretariatIt, sample.Wnioski, scenario.Company.JanKowalski], CorrespondenceStart.MailboxOwners));
        var page = window.Mailboxes;
        Assert.Equal(MailboxScopeKind.Selection, page.ScopeKind);
        Assert.Equal("Zaznaczone obiekty: 3", page.ScopeText);

        await page.FindOwnersCommand.ExecuteAsync(null);

        Assert.Equal(["Sekretariat IT", "Wnioski"], page.Rows.Select(r => r.MailboxName).Order(StringComparer.Ordinal));   // Jan is no shared mailbox

        page.ScopeKind = MailboxScopeKind.Domain;
        Assert.Equal(FakeDirectoryBuilder.DefaultDomain, page.ScopeText);
        await page.FindOwnersCommand.ExecuteAsync(null);
        Assert.Equal(13, page.Rows.Count);
    }

    [Fact]
    public async Task OrganizationalUnit_WithoutAUnit_OpensThePicker()
    {
        var (scenario, window, sample) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        page.ScopeKind = MailboxScopeKind.OrganizationalUnit;
        Assert.Equal("Nie wybrano jednostki – kliknij „Wybierz jednostkę…”.", page.ScopeText);

        await page.FindOwnersCommand.ExecuteAsync(null);   // the picker is cancelled
        Assert.Empty(page.Rows);

        scenario.Harness.Dialogs.PickedContainers.Enqueue(sample.Ou);
        await page.FindOwnersCommand.ExecuteAsync(null);
        Assert.Equal("contoso.test/Firma/Skrzynki", page.ScopeText);
        Assert.Equal(13, page.Rows.Count);
    }

    [Fact]
    public async Task NameSource_DisplayName_AndAnInvalidAttribute()
    {
        var (scenario, window, sample) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await UseSampleOuAsync(scenario, page, sample);
        page.NameSource = page.NameSourceChoices.Single(c => c.Value == OwnerNameSource.DisplayName);

        await page.FindOwnersCommand.ExecuteAsync(null);

        Assert.Equal(OwnerMatchStatus.NotFound, Row(page, sample.PomocTechniczna).Status);
        Assert.Equal("Pomoc techniczna", Row(page, sample.PomocTechniczna).OwnerNameText);

        page.NameSource = page.NameSourceChoices.Single(c => c.Value == OwnerNameSource.Attribute);
        page.NameAttribute = "unicodePwd";
        await page.FindOwnersCommand.ExecuteAsync(null);
        Assert.Equal("„unicodePwd” nie może zawierać nazwiska właściciela – wpisz nazwę LDAP atrybutu, który nie zawiera danych poufnych.", page.Error);
        Assert.Equal(page.Error, page.ProgressText);

        page.NameAttribute = "description";
        await page.FindOwnersCommand.ExecuteAsync(null);
        Assert.Null(page.Error);
        Assert.Equal(13, page.Rows.Count);   // "Skrzynka współdzielona: Sekretariat IT" names nobody
    }

    [Fact]
    public async Task ExtraFilter_IsValidated_AndAnded()
    {
        var (scenario, window, sample) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await UseSampleOuAsync(scenario, page, sample);

        page.ExtraFilter = "(description=Sala";
        Assert.Equal("Nieprawidłowy filtr LDAP: Unbalanced parentheses.", page.ExtraFilterError);
        Assert.False(page.FindOwnersCommand.CanExecute(null));

        page.ExtraFilter = "description=Sala*";
        Assert.Null(page.ExtraFilterError);
        page.Selection = page.SelectionChoices.Single(c => c.Value == MailboxSelection.SharedRoomsAndEquipment);
        await page.FindOwnersCommand.ExecuteAsync(null);

        var search = scenario.Harness.Session.Operations.First(o => o.Search?.BaseDn == sample.Ou).Search!;
        Assert.Contains("(description=Sala*)", search.Filter, StringComparison.Ordinal);
        Assert.Contains("(msExchRecipientTypeDetails=16)", search.Filter, StringComparison.Ordinal);
        Assert.Equal("Sala 101", Assert.Single(page.Rows).MailboxName);
    }

    [Fact]
    public async Task PersonBase_LimitsWhereTheOwnersAreLookedFor_AndIsSavedPortable()
    {
        var (scenario, window, sample) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await UseSampleOuAsync(scenario, page, sample);
        Assert.Equal("Cała domena (contoso.test)", page.PersonBaseText);
        scenario.Harness.Dialogs.PickedContainers.Enqueue(scenario.Company.ItOu);

        await page.BrowsePersonBaseCommand.ExecuteAsync(null);
        await page.FindOwnersCommand.ExecuteAsync(null);

        Assert.Equal("contoso.test/Firma/Pracownicy/IT", page.PersonBaseText);
        Assert.Equal(OwnerMatchStatus.Matched, Row(page, sample.SekretariatIt).Status);
        Assert.Equal(OwnerMatchStatus.Matched, Row(page, sample.Rekrutacja).Status);
        Assert.Equal(OwnerMatchStatus.NotFound, Row(page, sample.Archiwum).Status);
        var settings = new CorrespondenceSettings();
        page.SaveTo(settings);
        Assert.Equal("OU=IT,OU=Pracownicy,OU=Firma,{domain}", settings.Mailboxes.PersonBase);

        page.ClearPersonBaseCommand.Execute(null);
        page.SaveTo(settings);
        Assert.Equal(string.Empty, settings.Mailboxes.PersonBase);
        Assert.Equal("Cała domena (contoso.test)", page.PersonBaseText);
    }

    [Fact]
    public async Task Settings_RoundTrip_AndTheFieldDefinitions()
    {
        var (scenario, window, _) = await OpenAsync();
        using var __ = scenario;
        using var ___ = window;
        var page = window.Mailboxes;
        var settings = new CorrespondenceSettings
        {
            Mailboxes = new MailboxOwnerSettings
            {
                Selection = MailboxSelection.AllMailboxes,
                ExtraFilter = "(department=IT)",
                NameSource = OwnerNameSource.Attribute,
                NameAttribute = "info",
                PersonBase = "OU=Pracownicy,OU=Firma,{domain}",
                EnabledOwnersOnly = false,
                GroupByOwner = false,
                Fields = [new("Skrzynka", "{displayName}", DirectoryFieldTarget.Mailbox), new("Zwrot osobowy", "{$salutation}")],
            },
        };

        page.LoadFrom(settings);

        Assert.Equal(MailboxSelection.AllMailboxes, page.Selection.Value);
        Assert.Equal("(department=IT)", page.ExtraFilter);
        Assert.Equal(OwnerNameSource.Attribute, page.NameSource.Value);
        Assert.Equal("info", page.NameAttribute);
        Assert.Equal("contoso.test/Firma/Pracownicy", page.PersonBaseText);
        Assert.False(page.EnabledOwnersOnly);
        Assert.False(page.GroupByOwner);
        Assert.Equal(["Skrzynka", "Zwrot osobowy"], page.MailboxFields.Select(f => f.Name));

        page.AddMailboxFieldCommand.Execute(null);
        Assert.Equal(new DirectoryFieldDefinition("Nowe pole skrzynki", "{displayName}", DirectoryFieldTarget.Mailbox), page.MailboxFields[^1].ToDefinition());
        page.RemoveMailboxFieldCommand.Execute(page.MailboxFields[0]);
        var saved = new CorrespondenceSettings();
        page.SaveTo(saved);
        Assert.Equal(MailboxSelection.AllMailboxes, saved.Mailboxes.Selection);
        Assert.Equal("OU=Pracownicy,OU=Firma,{domain}", saved.Mailboxes.PersonBase);
        Assert.Equal(["Zwrot osobowy", "Nowe pole skrzynki"], saved.Mailboxes.Fields.Select(f => f.Name));

        page.ResetMailboxFieldsCommand.Execute(null);
        page.SaveTo(saved);
        Assert.Empty(saved.Mailboxes.Fields);
        Assert.Equal(DirectoryFieldDefinition.MailboxDefaults, page.MailboxFields.Select(f => f.ToDefinition()));
    }

    [Fact]
    public async Task WithoutTheExchangeSchema_OnlyAllAccountsIsOffered()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var builder = new FakeDirectoryBuilder(schema: FakeSchema.CreateWithoutMailboxes(), timeProvider: scenario.Harness.Time);
        var company = builder.AddSampleCompany();
        var sample = builder.AddMailboxOwnerSample(company);
        await scenario.Harness.ReconnectAsync(builder.Session);
        await scenario.Harness.Settings.UpdateAsync(s => s.Correspondence.Mailboxes.Selection = MailboxSelection.SharedRoomsAndEquipment, TestContext.Current.CancellationToken);
        using var window = await scenario.OpenAsync(new CorrespondenceRequest([], CorrespondenceStart.MailboxOwners));
        var page = window.Mailboxes;

        Assert.False(page.IsExchangeSchemaPresent);
        Assert.Equal([MailboxSelection.AllAccounts], page.SelectionChoices.Select(c => c.Value));
        Assert.Equal(MailboxSelection.AllAccounts, page.Selection.Value);
        Assert.Equal("Wszystkie konta użytkowników", page.Selection.Text);
        scenario.Harness.Dialogs.PickedContainers.Enqueue(sample.Ou);
        await page.BrowseScopeCommand.ExecuteAsync(null);

        await page.FindOwnersCommand.ExecuteAsync(null);

        Assert.Equal(14, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.Equal("konto użytkownika", r.MailboxTypeText));
        Assert.Equal(OwnerMatchStatus.Matched, page.Rows.Single(r => r.MailboxName == "Sekretariat IT").Status);
        Assert.DoesNotContain(builder.Session.Operations.Where(o => o.Search is not null),
            o => o.Search!.Filter.Contains("msExch", StringComparison.OrdinalIgnoreCase)
                || o.Search.Attributes.Any(a => a.StartsWith("msExch", StringComparison.OrdinalIgnoreCase)));

        var saved = new CorrespondenceSettings();
        page.SaveTo(saved);
        Assert.Equal(MailboxSelection.SharedRoomsAndEquipment, saved.Mailboxes.Selection);   // the preference stays
    }

    [Fact]
    public async Task Cancel_StopsTheRun()
    {
        var (scenario, window, sample) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await UseSampleOuAsync(scenario, page, sample);
        Assert.False(page.CancelCommand.CanExecute(null));
        scenario.Harness.Session.BeforeOperation = operation =>
        {
            if (operation.Search?.Filter.Contains("givenName=*", StringComparison.Ordinal) == true)
            {
                Assert.True(page.IsRunning);
                Assert.True(page.CancelCommand.CanExecute(null));
                page.CancelCommand.Execute(null);
            }
        };

        await page.FindOwnersCommand.ExecuteAsync(null);

        Assert.False(page.IsRunning);
        Assert.Empty(page.Rows);
        Assert.Equal("Przerwano – Skrzynki: 13", page.ProgressText);
        Assert.False(page.CancelCommand.CanExecute(null));
        Assert.True(page.FindOwnersCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheSessionEnding_CancelsARun()
    {
        var (scenario, window, sample) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await UseSampleOuAsync(scenario, page, sample);
        var release = new TaskCompletionSource();
        scenario.Harness.Session.BeforeOperation = operation =>
        {
            if (operation.Search?.Filter.Contains("givenName=*", StringComparison.Ordinal) == true)
            {
                release.Task.Wait(TestContext.Current.CancellationToken);
            }
        };

        var run = page.FindOwnersCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => page.ProgressText == "Skrzynki: 13");
        await scenario.Harness.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => !window.Recipients.CanUseDirectory);
        release.SetResult();
        await run;

        Assert.False(page.IsRunning);
        Assert.Empty(page.Rows);
        Assert.StartsWith("Przerwano", page.ProgressText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, 300_000, "Znaleziono ponad 3 skrzynek – zawęź zakres, rodzaj skrzynek albo filtr.")]
    [InlineData(20_000, 5, "W miejscu szukania właścicieli jest ponad 5 kont – wybierz węższą jednostkę.")]
    public async Task Limits_StopTheRun_WithAClearError(int mailboxes, int persons, string error)
    {
        var (scenario, window, sample) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await UseSampleOuAsync(scenario, page, sample);
        page.MaxMailboxes = mailboxes;
        page.MaxPersons = persons;

        await page.FindOwnersCommand.ExecuteAsync(null);

        Assert.Empty(page.Rows);
        Assert.Equal(error, page.Error);
        Assert.Equal(error, page.ProgressText);
        Assert.False(page.IsRunning);
        var search = scenario.Harness.Session.Operations.Last(o => o.Search is not null).Search!;
        Assert.Equal((mailboxes < 100 ? mailboxes : persons) + 1, search.SizeLimit);
    }

    [Fact]
    public async Task ADirectoryError_IsShownInTheProgress()
    {
        var (scenario, window, sample) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await UseSampleOuAsync(scenario, page, sample);
        scenario.Harness.Session.FailNext(FakeOperationKind.Search, sample.Ou, DirectoryErrorKind.Busy);

        await page.FindOwnersCommand.ExecuteAsync(null);

        Assert.StartsWith("Nie udało się odczytać skrzynek: ", page.ProgressText, StringComparison.Ordinal);
        Assert.Equal(page.Error, page.ProgressText);
        Assert.False(page.IsRunning);
    }

    [Fact]
    public async Task NoMailboxes_SaysSo()
    {
        var (scenario, window, _) = await OpenAsync();
        using var __ = scenario;
        using var ___ = window;
        var page = window.Mailboxes;
        scenario.Harness.Dialogs.PickedContainers.Enqueue(scenario.Company.ItOu);
        await page.BrowseScopeCommand.ExecuteAsync(null);

        await page.FindOwnersCommand.ExecuteAsync(null);

        Assert.Empty(page.Rows);
        Assert.Equal("W tym zakresie nie znaleziono skrzynek.", page.ProgressText);
        Assert.False(page.AddToRecipientsCommand.CanExecute(null));
    }
}
