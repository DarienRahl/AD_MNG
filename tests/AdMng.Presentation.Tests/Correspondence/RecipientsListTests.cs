using System.Text;
using AdMng.Core.Correspondence.Import;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Settings;
using static AdMng.Presentation.Tests.Correspondence.PageTests;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// "1 · Odbiorcy", the list itself (design note 6.4 and 7.1): counts, the "Nie wysyłać" list (in memory only), the journal
/// marks and "Pomijaj już wysłane", the journal report with prune and clear, the folded filter box, include all/none,
/// remove, clear, the CSV export, the send list and its messages, the settings.
/// </summary>
public sealed class RecipientsListTests
{
    private static readonly Dictionary<string, string> Lodz = new() { ["Miejscowość"] = "Łódź", ["Uwagi"] = "=SUMA(A1)" };

    private static void AddSample(RecipientsPageViewModel page)
    {
        page.AddRows(
        [
            new ImportedContact("anna.zielinska@urzad.pl", "Anna Zielińska", Lodz),
            new ImportedContact("piotr.wisniewski@urzad.pl", "Piotr Wiśniewski", new Dictionary<string, string> { ["Miejscowość"] = "Kraków" }),
            new ImportedContact("zaneta.szymanska@urzad.pl", "Żaneta Szymańska", new Dictionary<string, string> { ["Miejscowość"] = "Wrocław" }),
        ], "kontakty.csv");
    }

    [Fact]
    public async Task Exclusions_FromTheClipboard_SkipRows_AndAreCounted()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        AddSample(page);
        Clipboard(scenario, "Proszę nie pisać do: Piotr <PIOTR.wisniewski@urzad.pl>, kogos@innego.pl");

        await page.ExclusionsFromClipboardCommand.ExecuteAsync(null);

        Assert.True(Recipient(page, "piotr.wisniewski@urzad.pl").IsExcluded);
        Assert.False(Recipient(page, "anna.zielinska@urzad.pl").IsExcluded);
        Assert.Equal(2, page.ExclusionCount);
        Assert.Equal("Odbiorcy: 3 • do wysłania: 2 • pominięci: 1", page.CountsText);
        Assert.Equal("Pominięcia: 2 własnych + 0 z dziennika", page.ExclusionsText);
        Assert.Equal("„Nie wysyłać”: dodano 2, na liście 2.", page.StatusText);
        Assert.DoesNotContain(page.SendList(), r => r.Email == "piotr.wisniewski@urzad.pl");

        page.ClearExclusionsCommand.Execute(null);
        Assert.False(Recipient(page, "piotr.wisniewski@urzad.pl").IsExcluded);
        Assert.Equal("Odbiorcy: 3 • do wysłania: 3", page.CountsText);
        Assert.Equal("Wyczyszczono listę „Nie wysyłać”.", page.StatusText);
    }

    [Fact]
    public async Task Exclusions_WithAnEmptyClipboard_AskForTheAddresses_OrReadFiles()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        AddSample(page);
        scenario.Dialogs.Prompts.Enqueue("zaneta.szymanska@urzad.pl");
        var file = Text(scenario, "nie-wysylac.csv", "E-mail;Uwagi\r\nanna.zielinska@urzad.pl;rezygnacja\r\n");
        scenario.Dialogs.Files.Enqueue([file]);

        await page.ExclusionsFromClipboardCommand.ExecuteAsync(null);
        await page.ExclusionsFromFileCommand.ExecuteAsync(null);

        Assert.True(Recipient(page, "zaneta.szymanska@urzad.pl").IsExcluded);
        Assert.True(Recipient(page, "anna.zielinska@urzad.pl").IsExcluded);
        Assert.Equal(["piotr.wisniewski@urzad.pl"], page.SendList().Select(r => r.Email));
        Assert.Equal("„Nie wysyłać”: dodano 1, na liście 2.", page.StatusText);

        // A later import is checked against the list too.
        page.AddRows([new ImportedContact("ANNA.zielinska@urzad.pl", "Anna", Lodz), new ImportedContact("nowa@urzad.pl", "Nowa", Lodz)], "druga.csv");
        Assert.True(Recipient(page, "nowa@urzad.pl").Include);
        Assert.False(Recipient(page, "nowa@urzad.pl").IsExcluded);
    }

    [Fact]
    public async Task AlreadySent_RowsShowTheJournalEntry_AndAreSkippedWhileTheOptionIsOn()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        await scenario.Journal.LoadAsync(TimeSpan.FromDays(365), TestContext.Current.CancellationToken);
        await scenario.Journal.AppendAsync("anna.zielinska@urzad.pl", "Zaproszenie na szkolenie dla {{nazwa}} – edycja jesienna 2026 w Łodzi i Warszawie", TestContext.Current.CancellationToken);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;

        AddSample(page);

        var anna = Recipient(page, "anna.zielinska@urzad.pl");
        Assert.True(anna.IsAlreadySent);
        Assert.Equal("13.10.2026 · Zaproszenie na szkolenie dla {{nazwa}} – edycja jesienna 20…", anna.PreviouslySentText);
        Assert.Null(Recipient(page, "piotr.wisniewski@urzad.pl").PreviouslySentText);
        Assert.Equal("Odbiorcy: 3 • do wysłania: 2 • pominięci: 1", page.CountsText);
        Assert.Equal("Pominięcia: 0 własnych + 1 z dziennika", page.ExclusionsText);
        Assert.Equal(2, page.SendList().Count);

        page.SkipAlreadySent = false;
        Assert.False(anna.IsAlreadySent);
        Assert.NotNull(anna.PreviouslySentText);
        Assert.Equal(3, page.SendList().Count);

        // Sent meanwhile (the send page appends): the send list reads the journal again.
        page.SkipAlreadySent = true;
        scenario.Harness.Time.Advance(TimeSpan.FromHours(2));
        await scenario.Journal.AppendAsync("piotr.wisniewski@urzad.pl", "Temat", TestContext.Current.CancellationToken);
        Assert.Equal(["zaneta.szymanska@urzad.pl"], page.SendList().Select(r => r.Email));
        Assert.Equal("13.10.2026 · Temat", Recipient(page, "piotr.wisniewski@urzad.pl").PreviouslySentText);
        Assert.Equal("Pominięcia: 0 własnych + 2 z dziennika", page.ExclusionsText);
    }

    [Fact]
    public async Task ShowJournal_ReportsIt_ThenPrunesAfterAConfirmation()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        await scenario.Journal.LoadAsync(TimeSpan.FromDays(3650), TestContext.Current.CancellationToken);
        await scenario.Journal.AppendAsync("anna.zielinska@urzad.pl", "Stary temat", TestContext.Current.CancellationToken);
        scenario.Harness.Time.Advance(TimeSpan.FromDays(400));
        await scenario.Journal.AppendAsync("piotr.wisniewski@urzad.pl", "Nowy temat", TestContext.Current.CancellationToken);
        await scenario.Harness.Settings.UpdateAsync(s => s.Correspondence.JournalRetentionDays = 3650, TestContext.Current.CancellationToken);
        using var window = await scenario.OpenAsync();
        await scenario.Harness.Settings.UpdateAsync(s => s.Correspondence.JournalRetentionDays = 365, TestContext.Current.CancellationToken);
        var page = window.Recipients;
        AddSample(page);
        Assert.True(Recipient(page, "anna.zielinska@urzad.pl").IsAlreadySent);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await page.ShowJournalCommand.ExecuteAsync(null);

        var (title, report) = Assert.Single(scenario.Dialogs.Reports);
        Assert.Equal("Dziennik „już wysłane”", title);
        Assert.StartsWith("Wpisy: 2\nNajstarszy: 13.10.2026 12:00\nNajnowszy: 17.11.2027 12:00\nPrzechowywanie: 365 dni\nFolder: ", report.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("@", report, StringComparison.Ordinal);
        Assert.Equal(["confirm:Usunąć z dziennika wpisy starsze niż 365 dni?"], scenario.Harness.Dialogs.Shown);
        Assert.Equal(1, scenario.Journal.Count);
        Assert.Equal("Usunięto wpisy dziennika: 1.", page.StatusText);
        Assert.False(Recipient(page, "anna.zielinska@urzad.pl").IsAlreadySent);
        Assert.True(Recipient(page, "piotr.wisniewski@urzad.pl").IsAlreadySent);
    }

    [Fact]
    public async Task ShowJournal_ClearsItAfterTheSecondConfirmation()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        await scenario.Journal.LoadAsync(TimeSpan.FromDays(365), TestContext.Current.CancellationToken);
        await scenario.Journal.AppendAsync("anna.zielinska@urzad.pl", "Temat", TestContext.Current.CancellationToken);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        AddSample(page);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(false);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await page.ShowJournalCommand.ExecuteAsync(null);

        Assert.Equal(2, scenario.Harness.Dialogs.Shown.Count);
        Assert.Equal("confirm:Wyczyścić cały dziennik? Do każdego adresu będzie można wysłać ponownie.", scenario.Harness.Dialogs.Shown[1]);
        Assert.Equal(0, scenario.Journal.Count);
        Assert.Equal("Dziennik wyczyszczony.", page.StatusText);
        Assert.All(page.Items, r => Assert.Null(r.PreviouslySentText));
        Assert.Equal("Pominięcia: 0 własnych + 0 z dziennika", page.ExclusionsText);
    }

    [Fact]
    public async Task ShowJournal_WithoutAJournal_SaysItIsUnavailable()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        scenario.Protector.Failure = new PlatformNotSupportedException();
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        AddSample(page);

        await page.ShowJournalCommand.ExecuteAsync(null);

        Assert.Equal(["info:Dziennik „już wysłane” jest niedostępny w tym oknie."], scenario.Harness.Dialogs.Shown);
        Assert.Empty(scenario.Dialogs.Reports);
        Assert.Equal(3, page.SendList().Count);
    }

    [Fact]
    public async Task SearchText_FiltersFolded_OverAddressNameSourceAndFields()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        AddSample(page);

        page.SearchText = "lodz";
        Assert.Equal(["anna.zielinska@urzad.pl"], page.View.Select(r => r.Email));
        page.SearchText = "  ŻANETA ";
        Assert.Equal(["zaneta.szymanska@urzad.pl"], page.View.Select(r => r.Email));
        page.SearchText = "kontakty.csv";
        Assert.Equal(3, page.View.Count);

        // "Zaznacz/odznacz wszystkie" act on the rows shown.
        page.SearchText = "wroclaw";
        page.IncludeNoneCommand.Execute(null);
        Assert.Equal("Odbiorcy: 3 • do wysłania: 2", page.CountsText);
        Assert.False(Recipient(page, "zaneta.szymanska@urzad.pl").Include);
        page.SearchText = string.Empty;
        Assert.Equal(3, page.View.Count);
        page.IncludeAllCommand.Execute(null);
        Assert.Equal(3, page.SendList().Count);

        Recipient(page, "anna.zielinska@urzad.pl").Include = false;
        Assert.Equal("Odbiorcy: 3 • do wysłania: 2", page.CountsText);
    }

    [Fact]
    public async Task RemoveSelected_AndClear()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        AddSample(page);

        page.RemoveSelectedCommand.Execute(null);
        Assert.Equal("Najpierw zaznacz wiersze do usunięcia.", page.StatusText);
        page.SelectedItems = new List<object> { Recipient(page, "piotr.wisniewski@urzad.pl") };
        page.RemoveSelectedCommand.Execute(null);
        Assert.Equal(["anna.zielinska@urzad.pl", "zaneta.szymanska@urzad.pl"], page.Items.Select(r => r.Email));
        Assert.Equal(page.Items, page.View);
        Assert.Equal("Usunięto z listy: 1.", page.StatusText);

        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(false);
        await page.ClearCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Items.Count);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await page.ClearCommand.ExecuteAsync(null);
        Assert.Equal(["confirm:Usunąć wszystkich odbiorców (2)?", "confirm:Usunąć wszystkich odbiorców (2)?"], scenario.Harness.Dialogs.Shown);
        Assert.Empty(page.Items);
        Assert.Empty(page.View);
        Assert.Empty(page.Columns);
        Assert.Equal("Odbiorcy: 0 • do wysłania: 0", page.CountsText);
        Assert.Equal("Lista odbiorców wyczyszczona.", page.StatusText);
    }

    [Fact]
    public async Task Export_WritesTheListAsCsv_WithFormulasProtected()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;

        await page.ExportCommand.ExecuteAsync(null);
        Assert.Equal("Nie ma czego zapisać.", page.StatusText);

        AddSample(page);
        Recipient(page, "anna.zielinska@urzad.pl").SetStatus(RecipientStatusKind.Sent, CorrespondenceText.StatusText(scenario.Harness.Localizer, RecipientStatusKind.Sent));
        var path = Path.Combine(scenario.Paths.SettingsDirectory, "eksport.csv");
        Directory.CreateDirectory(scenario.Paths.SettingsDirectory);
        scenario.Harness.Files.NextSavePath = path;

        await page.ExportCommand.ExecuteAsync(null);

        Assert.Equal("odbiorcy.csv", scenario.Harness.Files.LastDefaultFileName);
        var lines = System.IO.File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal("Nazwa;E-mail;Źródło;Status;Miejscowość;Uwagi", lines[0]);
        Assert.Equal("Anna Zielińska;anna.zielinska@urzad.pl;kontakty.csv;wysłano;Łódź;'=SUMA(A1)", lines[1]);
        Assert.Equal("Piotr Wiśniewski;piotr.wisniewski@urzad.pl;kontakty.csv;;Kraków;", lines[2]);
        Assert.Equal(4, lines.Length);
        Assert.Equal($"Zapisano listę odbiorców: {path}", page.StatusText);
    }

    [Fact]
    public async Task Units_OneMessagePerRow_OrPerAddress()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        AddSample(page);
        var anna = Recipient(page, "anna.zielinska@urzad.pl");
        var mailboxRows = new[]
        {
            new CorrespondenceRecipientViewModel("jan.kowalski@contoso.test", "Jan Kowalski", "Skrzynki", new Dictionary<string, string> { ["Skrzynka"] = "Sekretariat IT" },
                mailboxKeys: ["sekretariat.it@contoso.test"]) { Origin = RecipientOrigin.Mailboxes },
            new CorrespondenceRecipientViewModel("jan.kowalski@contoso.test", "Jan Kowalski", "Skrzynki", new Dictionary<string, string> { ["Skrzynka"] = "Laboratorium" },
                mailboxKeys: ["lab@contoso.test"]) { Origin = RecipientOrigin.Mailboxes },
        };
        Assert.Equal(2, page.ReplaceMailboxRows(mailboxRows));

        var single = page.Units(false);
        var grouped = page.Units(true);
        var withRows = page.RecipientUnits(true);

        Assert.Equal(5, single.Count);
        Assert.Equal(4, grouped.Count);
        var jan = Assert.Single(grouped, u => u.Email == "jan.kowalski@contoso.test");
        Assert.Equal(["Sekretariat IT", "Laboratorium"], jan.Rows.Select(r => r["Skrzynka"]));
        Assert.Equal(grouped.Select(u => u.Email), withRows.Select(u => u.Unit.Email));
        var janUnit = Assert.Single(withRows, u => u.Unit.Email == "jan.kowalski@contoso.test");
        Assert.Equal(mailboxRows, janUnit.Rows);
        Assert.Equal(["sekretariat.it@contoso.test", "lab@contoso.test"], janUnit.Keys);
        Assert.Same(anna, withRows[0].Rows[0]);
        Assert.Empty(withRows[0].Keys);
        Assert.Equal(5, page.RecipientUnits(false).Count);
        Assert.Equal(nameof(RecipientUnit) + " (2 rows)", janUnit.ToString());
    }

    [Fact]
    public async Task Settings_SkipAlreadySent_AndTheFieldDefinitions()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var settings = new CorrespondenceSettings();

        page.SaveTo(settings);
        Assert.True(settings.SkipAlreadySent);
        Assert.Empty(settings.DirectoryFields);   // the defaults are not stored

        page.SkipAlreadySent = false;
        page.AddDirectoryFieldCommand.Execute(null);
        page.DirectoryFields[^1].Expression = "{department} / {title}";
        page.SaveTo(settings);
        Assert.False(settings.SkipAlreadySent);
        Assert.Equal(DirectoryFieldDefinition.Defaults.Count + 1, settings.DirectoryFields.Count);
        Assert.Equal(new DirectoryFieldDefinition("Nowe pole", "{department} / {title}"), settings.DirectoryFields[^1]);

        var other = new RecipientsPageViewModel(scenario.Harness.Services, scenario.Services, () => scenario.Harness.Session);
        settings.DownloadsPattern = "*adresy*.csv";
        other.LoadFrom(settings);
        Assert.False(other.SkipAlreadySent);
        Assert.Equal(settings.DirectoryFields, other.DirectoryFields.Select(f => f.ToDefinition()));
        other.ResetDirectoryFieldsCommand.Execute(null);
        Assert.Equal(DirectoryFieldDefinition.Defaults, other.DirectoryFields.Select(f => f.ToDefinition()));
    }

    [Fact]
    public void Rows_NeverShowAddressesInToString()
    {
        var row = new CorrespondenceRecipientViewModel("anna@urzad.pl", "Anna Zielińska", "kontakty.csv", Lodz);
        var unit = new RecipientUnit(new Core.Correspondence.Composition.CorrespondenceUnit("anna@urzad.pl", "Anna", [Lodz]), [row]);

        Assert.Equal(nameof(CorrespondenceRecipientViewModel), row.ToString());
        Assert.DoesNotContain("anna", unit.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Łódź", row["miejscowosc"]);
    }
}
