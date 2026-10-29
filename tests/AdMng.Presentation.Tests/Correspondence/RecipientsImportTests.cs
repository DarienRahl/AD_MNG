using System.Text;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Csv;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;
using static AdMng.Presentation.Tests.Correspondence.PageTests;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// "1 · Odbiorcy", every import source (design note 6.4 and 7.1): files through the window's multi-file dialog (the ANSI
/// code page of the system, failures reported per file), the newest contacts file of Downloads, the clipboard (text, else
/// CF_HTML), typed addresses, drag and drop – de-duplicated, with the columns in import order and MailerWPiA's status lines.
/// </summary>
public sealed class RecipientsImportTests
{
    private const string ContactsCsv =
        "Nazwa;E-mail;Miejscowość;Kod pocztowy\r\n"
        + "Urząd Gminy Zgierz;urzad@gmina-zgierz.pl;Zgierz;95-100\r\n"
        + "Urząd Gminy Andrespol;sekretariat@andrespol.pl;Andrespol;95-020\r\n"
        + "Starostwo bez adresu;brak;Łódź;90-001\r\n";

    [Fact]
    public async Task ImportFiles_ReadsALegacyCsv_WithTheAnsiCodePage_AndTellsTheCounts()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var csv = File(scenario, "kontakty.csv", Cp1250.GetBytes(ContactsCsv));
        scenario.Dialogs.Files.Enqueue([csv]);

        await page.ImportFilesCommand.ExecuteAsync(null);

        Assert.Equal(["urzad@gmina-zgierz.pl", "sekretariat@andrespol.pl"], page.Items.Select(r => r.Email));
        var zgierz = Recipient(page, "urzad@gmina-zgierz.pl");
        Assert.Equal("Urząd Gminy Zgierz", zgierz.Name);
        Assert.Equal("kontakty.csv", zgierz.Source);
        Assert.Equal(RecipientOrigin.File, zgierz.Origin);
        Assert.Equal("Zgierz", zgierz["Miejscowość"]);
        Assert.Equal("95-100", zgierz["kod_pocztowy"]);           // a column named alike
        Assert.Equal(string.Empty, zgierz["Uwagi"]);
        Assert.Equal(["Miejscowość", "Kod pocztowy"], page.Columns);  // the e-mail and name columns are the fixed ones
        Assert.Same(page.Columns, page.KnownColumns);
        Assert.Equal(page.Items, page.View);
        Assert.Equal("Wczytano plik „kontakty.csv” (CSV): 2 nowe, 1 błędny adres", page.StatusText);
        Assert.Equal(page.StatusText, window.StatusText);
        Assert.Equal("Odbiorcy: 2 • do wysłania: 2", page.CountsText);
        Assert.Empty(scenario.Harness.Dialogs.Errors);
    }

    [Fact]
    public async Task ImportFiles_SeveralFiles_AreDeDuplicatedAgainstTheList()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var first = File(scenario, "kontakty.csv", Cp1250.GetBytes(ContactsCsv));
        var second = Text(scenario, "gminy.tsv",
            "E-mail\tNazwa\tWójt\r\nurzad@gmina-zgierz.pl\tGmina Zgierz\tJan Żak\r\nugbrojce@brojce.pl\tGmina Brójce\tŁucja Źrebiec\r\n");
        scenario.Dialogs.Files.Enqueue([first]);
        await page.ImportFilesCommand.ExecuteAsync(null);
        scenario.Dialogs.Files.Enqueue([first, second]);

        await page.ImportFilesCommand.ExecuteAsync(null);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal("Urząd Gminy Zgierz", Recipient(page, "urzad@gmina-zgierz.pl").Name);   // the first row stays
        Assert.Equal("gminy.tsv", Recipient(page, "ugbrojce@brojce.pl").Source);
        Assert.Equal(["Miejscowość", "Kod pocztowy", "Wójt"], page.Columns);
        Assert.Equal("Wczytano pliki (2): 1 nowy, 3 powtórzone, 1 błędny adres", page.StatusText);
    }

    [Fact]
    public async Task ImportFiles_ReportsFilesThatCannotBeRead_AndKeepsTheOthers()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var good = File(scenario, "kontakty.csv", Cp1250.GetBytes(ContactsCsv));
        var broken = File(scenario, "zepsuty.xlsx", Encoding.ASCII.GetBytes("to nie jest arkusz"));
        var large = Path.Combine(scenario.Paths.SettingsDirectory, "duży.csv");
        using (var stream = System.IO.File.Create(large))
        {
            stream.SetLength(ContactFileReader.MaxFileBytes + 1);   // sparse: nothing is written
        }

        var missing = Path.Combine(scenario.Paths.SettingsDirectory, "usunięty.csv");
        scenario.Dialogs.Files.Enqueue([broken, good, large, missing]);

        await page.ImportFilesCommand.ExecuteAsync(null);

        Assert.Equal(2, page.Items.Count);
        var (title, message) = Assert.Single(scenario.Harness.Dialogs.Errors);
        Assert.Equal("Wczytywanie adresów", title);
        var lines = message.Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Nie udało się wczytać „zepsuty.xlsx”: plik jest uszkodzony, większy niż 50 MB albo w formacie, którego nie da się odczytać", lines[0]);
        Assert.StartsWith("Nie udało się wczytać „duży.csv”: plik jest uszkodzony, większy niż 50 MB", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("Nie udało się wczytać „usunięty.csv”: ", lines[2], StringComparison.Ordinal);
        Assert.Equal("Wczytano pliki (1): 2 nowe, 1 błędny adres", page.StatusText);
    }

    [Fact]
    public async Task ImportFiles_TellTheFormatOfEachFile()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var html = Text(scenario, "gminy.html",
            "<html><body><p>Lista gmin</p><table><tr><th>E-mail</th><th>Nazwa</th><th>Gmina</th></tr>"
            + "<tr><td>wojt@brojce.pl</td><td>Urząd Gminy Brójce</td><td>Brójce</td></tr></table></body></html>");
        var rtf = File(scenario, "pismo.rtf",
            Encoding.ASCII.GetBytes(@"{\rtf1\ansi\ansicpg1250\deff0{\fonttbl{\f0 Calibri;}}\f0 Urz\'b9d Miasta \'a3\'f3d\'9f <urzad@uml.lodz.pl>\par}"));

        scenario.Dialogs.Files.Enqueue([html]);
        await page.ImportFilesCommand.ExecuteAsync(null);
        Assert.Equal("Wczytano plik „gminy.html” (HTML): 1 nowy", page.StatusText);
        Assert.Equal("Brójce", Recipient(page, "wojt@brojce.pl")["Gmina"]);

        scenario.Dialogs.Files.Enqueue([rtf]);
        await page.ImportFilesCommand.ExecuteAsync(null);
        Assert.Equal("Wczytano plik „pismo.rtf” (RTF): 1 nowy", page.StatusText);
        Assert.Equal("Urząd Miasta Łódź", Recipient(page, "urzad@uml.lodz.pl").Name);
    }

    [Fact]
    public async Task AddRows_StopsAtTheRecipientLimit_AndSaysHowManyWereLeftOut()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var page = new RecipientsPageViewModel(scenario.Harness.Services, scenario.Services, () => scenario.Harness.Session);
        var none = new Dictionary<string, string>();
        var contacts = Enumerable.Range(0, Core.Correspondence.Composition.CorrespondenceLimits.MaxRecipients + 3)
            .Select(i => new ImportedContact($"urzad{i}@gmina.pl", string.Empty, none))
            .ToList();

        var added = page.AddRows(contacts, "duża-lista.csv", RecipientOrigin.File);

        Assert.Equal(Core.Correspondence.Composition.CorrespondenceLimits.MaxRecipients, added.Added);
        Assert.Equal(3, added.OverLimit);
        Assert.Equal(Core.Correspondence.Composition.CorrespondenceLimits.MaxRecipients, page.Items.Count);
        Assert.Equal("100 000 nowych, lista mieści najwyżej 100 000 odbiorców – pominięto 3", page.CountsPart(added, 0).Replace(' ', ' '));

        // A full list: nothing new, but the operator learns why.
        var more = page.AddRows([new ImportedContact("nowy@gmina.pl", string.Empty, none)], "druga.csv", RecipientOrigin.File);
        Assert.Equal("0 nowych, lista mieści najwyżej 100 000 odbiorców – pominięto 1", page.CountsPart(more, 0).Replace(' ', ' '));
    }

    [Fact]
    public async Task ImportFromDownloads_TakesTheNewestMatchingFile()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var downloads = scenario.Platform.DownloadsFolder!;
        var old = Text(scenario, "prawo-pl_kontakty_2026-09-01.csv", "E-mail;Nazwa\r\nstary@urzad.pl;Stary urząd\r\n", downloads);
        var fresh = Text(scenario, "kontakty-gminy.csv", "E-mail;Nazwa\r\nnowy@urzad.pl;Nowy urząd\r\nwojt@gmina.pl;Wójt gminy\r\n", downloads);
        var other = Text(scenario, "faktury.csv", "E-mail;Nazwa\r\nfaktury@firma.pl;Faktury\r\n", downloads);
        System.IO.File.SetLastWriteTimeUtc(old, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));
        System.IO.File.SetLastWriteTimeUtc(fresh, new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));
        System.IO.File.SetLastWriteTimeUtc(other, new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc));

        Assert.True(page.ImportFromDownloadsCommand.CanExecute(null));
        await page.ImportFromDownloadsCommand.ExecuteAsync(null);

        Assert.Equal(["nowy@urzad.pl", "wojt@gmina.pl"], page.Items.Select(r => r.Email));
        Assert.All(page.Items, r => Assert.Equal("kontakty-gminy.csv", r.Source));
        Assert.Equal("Wczytano plik „kontakty-gminy.csv” (CSV): 2 nowe", page.StatusText);
    }

    [Fact]
    public async Task ImportFromDownloads_WithoutAMatchingFileOrFolder_SaysSo()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var downloads = scenario.Platform.DownloadsFolder!;
        Text(scenario, "faktury.csv", "E-mail\r\nfaktury@firma.pl\r\n", downloads);

        await page.ImportFromDownloadsCommand.ExecuteAsync(null);
        scenario.Platform.DownloadsFolder = null;
        await page.ImportFromDownloadsCommand.ExecuteAsync(null);

        Assert.Empty(page.Items);
        Assert.Equal(
        [
            ("Wczytywanie adresów", $"W folderze Pobrane ({downloads}) nie ma pliku pasującego do „*kontakty*.csv”. Użyj „Z pliku”, jeśli plik leży gdzie indziej."),
            ("Wczytywanie adresów", "Nie udało się ustalić folderu Pobrane."),
        ], scenario.Harness.Dialogs.Errors);
    }

    [Fact]
    public async Task Paste_AnExcelTable_BuildsTheFieldColumns()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        Clipboard(scenario,
            "E-mail\tNazwa\tMiejscowość\tKod pocztowy\r\n"
            + "urzad@gmina-zgierz.pl\tUrząd Gminy Zgierz\tZgierz\t95-100\r\n"
            + "sekretariat@andrespol.pl\tUrząd Gminy Andrespol\tAndrespol\t95-020\r\n");

        await page.PasteCommand.ExecuteAsync(null);

        Assert.Equal(["Miejscowość", "Kod pocztowy"], page.Columns);
        Assert.All(page.Items, r => Assert.Equal("schowek", r.Source));
        Assert.All(page.Items, r => Assert.Equal(RecipientOrigin.Clipboard, r.Origin));
        Assert.Equal("Andrespol", Recipient(page, "sekretariat@andrespol.pl")["Miejscowość"]);
        Assert.Equal("Ze schowka: 2 nowe", page.StatusText);

        await page.PasteCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("Ze schowka: 0 nowych, 2 powtórzone", page.StatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paste_WithoutText_ReadsTheTextOfTheHtml(bool asBytes)
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        const string html = "<html><body><!--StartFragment--><table>"
            + "<tr><th>E-mail</th><th>Nazwa</th><th>Miejscowość</th></tr>"
            + "<tr><td>biuro@lodz.pl</td><td>Biuro Łódź</td><td>Łódź</td></tr>"
            + "<tr><td>gmina@zgierz.pl</td><td>Gmina Zgierz</td><td>Zgierz</td></tr>"
            + "</table><!--EndFragment--></body></html>";
        scenario.Harness.Clipboard.Formatted = asBytes
            ? new ClipboardContent(Encoding.UTF8.GetBytes(html), null, null, "  ")
            : new ClipboardContent(null, html, null, null);

        await page.PasteCommand.ExecuteAsync(null);

        Assert.Equal(["biuro@lodz.pl", "gmina@zgierz.pl"], page.Items.Select(r => r.Email));
        Assert.Equal("Biuro Łódź", Recipient(page, "biuro@lodz.pl").Name);
        Assert.Equal("Łódź", Recipient(page, "biuro@lodz.pl")["Miejscowość"]);
    }

    [Fact]
    public async Task Paste_AnEmptyClipboard_SaysSo()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;

        await page.PasteCommand.ExecuteAsync(null);
        Clipboard(scenario, "nic tu nie ma");
        await page.PasteCommand.ExecuteAsync(null);

        Assert.Empty(page.Items);
        Assert.Equal("Ze schowka: nie znaleziono adresów e-mail", page.StatusText);
        Clipboard(scenario, null);
        await page.PasteCommand.ExecuteAsync(null);
        Assert.Equal("Schowek jest pusty – skopiuj adresy (np. z Worda, Excela lub wiadomości) i spróbuj ponownie.", page.StatusText);
    }

    [Fact]
    public async Task AddManual_ReadsNamedAndBareAddresses()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        scenario.Dialogs.Prompts.Enqueue("Jan Kowalski <jan.kowalski@contoso.test>; anna.zielinska@urzad.pl\nzły-adres@\n");
        scenario.Dialogs.Prompts.Enqueue(null);

        Assert.True(page.AddManualCommand.CanExecute(null));
        await page.AddManualCommand.ExecuteAsync(null);
        await page.AddManualCommand.ExecuteAsync(null);   // cancelled

        Assert.Equal(["jan.kowalski@contoso.test", "anna.zielinska@urzad.pl"], page.Items.Select(r => r.Email));
        Assert.Equal("Jan Kowalski", page.Items[0].Name);
        Assert.All(page.Items, r => Assert.Equal("ręcznie", r.Source));
        Assert.Equal("Dodano ręcznie: 2 nowe", page.StatusText);
        Assert.Empty(page.Columns);
    }

    [Fact]
    public async Task DroppedFiles_ReadTheContactFiles_AndNameTheOthers()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var csv = File(scenario, "kontakty.csv", Cp1250.GetBytes(ContactsCsv));
        var pdf = File(scenario, "pismo.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 jan@x.pl"));

        await page.AddFilesAsync([csv, pdf, Path.Combine(scenario.Paths.SettingsDirectory, "brak.csv")]);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal("Wczytano plik „kontakty.csv” (CSV): 2 nowe, 1 błędny adres Pominięto pliki w innych formatach: pismo.pdf, brak.csv.", page.StatusText);
    }

    [Fact]
    public async Task WithoutTheWindowDialogs_FilesUseTheSingleFileDialog_AndTypingIsOff()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var services = new CorrespondenceServices(scenario.Templates, scenario.Journal, new CsvCodec(), scenario.Harness.AuditLog, scenario.Harness.Time);
        var page = new RecipientsPageViewModel(scenario.Harness.Services, services, () => scenario.Harness.Session);
        scenario.Harness.Files.NextOpenPath = File(scenario, "kontakty.csv", Cp1250.GetBytes(ContactsCsv));

        await page.ImportFilesCommand.ExecuteAsync(null);

        Assert.Equal(2, page.Items.Count);
        Assert.False(page.AddManualCommand.CanExecute(null));
        Assert.False(page.ImportFromDownloadsCommand.CanExecute(null));
    }

    [Fact]
    public async Task AddRows_DeDuplicatesByAddress_AndSkipsInvalidContacts()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var fields = new Dictionary<string, string> { ["Miejscowość"] = "Łódź" };

        var first = page.AddRows([new("Anna@Urzad.PL", "Anna Zielińska", fields), new("zły@", "Zły", fields), new("anna@urzad.pl", "Druga", fields)], "lista.csv");
        var second = page.AddRows([new("anna@urzad.pl", "Trzecia", fields), new("piotr@urzad.pl", "Piotr Wiśniewski", fields)], "lista2.csv");

        Assert.Equal((1, 1), first);
        Assert.Equal((1, 1), second);
        Assert.Equal(["anna@urzad.pl", "piotr@urzad.pl"], page.Items.Select(r => r.Email));
        Assert.Equal("Anna Zielińska", page.Items[0].Name);
    }
}
