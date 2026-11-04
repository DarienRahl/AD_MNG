using System.IO;
using System.Text;
using AdMng.App.Correspondence;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// Release screenshots of Korespondencja (U5, design note 9.5): 26 the recipients imported from a CSV file after a run
/// (field columns, statuses, a journal mark), 27 the message (editor with formatting and a field, the Outlook signature,
/// attachments, options) and 28 the mailbox owners of the sample after matching. Polish UI, light theme, the in-memory
/// Outlook and directory; the files the pictures need are written to a temporary folder and deleted afterwards. Like
/// <see cref="ScreenshotTests"/> they run only when <see cref="ScreenshotWriter.DirectoryVariable"/> is set; the class name
/// starts with "ScreenshotTests" so the CI step "Screenshots" takes it too.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScreenshotTestsCorrespondence
{
    private const double ClientWidth = 1320;
    private const double ClientHeight = 860;

    private static readonly ScreenshotInfo RecipientsShot = new(
        "26-correspondence-recipients.png",
        "Korespondencja – odbiorcy wczytani z pliku CSV: kolumny pól, statusy wysyłki przez Outlooka i adres pominięty jako „już wysłany”",
        "Correspondence – recipients loaded from a CSV file: field columns, the statuses of a run through Outlook and an address skipped as \"already sent\"");

    private static readonly ScreenshotInfo MessageShot = new(
        "27-correspondence-message.png",
        "Korespondencja – wiadomość: edytor z formatowaniem i polem {{Miejscowość}}, podpis z Outlooka, załączniki, przyciski głosowania i dostarczenie o wskazanej porze",
        "Correspondence – the message: the editor with formatting and the field {{Miejscowość}}, the Outlook signature, attachments, voting buttons and deferred delivery");

    private static readonly ScreenshotInfo MailboxOwnersShot = new(
        "28-correspondence-mailbox-owners.png",
        "Korespondencja – właściciele skrzynek: dopasowani, potwierdzeni przez pełnomocnictwo, niejednoznaczni z kandydatami do wyboru i nieznalezieni",
        "Correspondence – mailbox owners: matched, confirmed by a delegate link, ambiguous with candidates to choose from, and not found");

    /// <summary>Offices of the Łódź area (fictitious addresses); the last row has no valid address.</summary>
    private const string ContactsCsv = """
        Nazwa;E-mail;Miejscowość;Kod pocztowy;Powiat
        Urząd Gminy Andrespol;sekretariat@andrespol.contoso.pl;Andrespol;95-020;łódzki wschodni
        Urząd Miasta Zgierza;kancelaria@zgierz.contoso.pl;Zgierz;95-100;zgierski
        Starostwo Powiatowe w Zgierzu;starostwo@powiat-zgierz.contoso.pl;Zgierz;95-100;zgierski
        Urząd Gminy Nowosolna;gmina@nowosolna.contoso.pl;Nowosolna;92-701;łódzki wschodni
        Urząd Miejski w Koluszkach;sekretariat@koluszki.contoso.pl;Koluszki;95-040;łódzki wschodni
        Urząd Gminy Brójce;ug@brojce.contoso.pl;Brójce;95-006;łódzki wschodni
        Urząd Miejski w Pabianicach;kancelaria@pabianice.contoso.pl;Pabianice;95-200;pabianicki
        Urząd Gminy Ksawerów;sekretariat@ksawerow.contoso.pl;Ksawerów;95-054;pabianicki
        Starostwo Powiatowe w Łodzi;biuro@powiat-lodzki.contoso.pl;Łódź;90-430;łódzki wschodni
        Urząd Gminy Rzgów;(adres w przygotowaniu);Rzgów;95-030;łódzki wschodni
        """;

    private const string Subject = "Zaproszenie na konferencję „Samorząd i prawo 2026” – {{Miejscowość}}";

    private const string Body =
        "<p>{{zwrot}},</p>"
        + "<p>serdecznie zapraszamy przedstawicieli <b>{{nazwa}}</b> na konferencję <b>„Samorząd i prawo 2026”</b>, "
        + "która odbędzie się <u>12 marca 2026 r.</u> na Wydziale Prawa i Administracji w Łodzi.</p>"
        + "<ul><li>panel o finansach gmin i powiatów,</li><li>warsztaty z zamówień publicznych,</li>"
        + "<li>dyżur ekspertów dla gminy {{Miejscowość}} ({{Powiat}}).</li></ul>"
        + "<p>Prosimy o potwierdzenie udziału do <i>27 lutego</i> – wystarczy kliknąć przycisk nad treścią tej wiadomości.</p>";

    [Fact]
    public Task Screenshot26_CorrespondenceRecipients()
    {
        var outlook = new FakeOutlookClient();
        outlook.Script("kancelaria@pabianice.contoso.pl", FakeOutlookClient.Failed(OutlookFailureKind.RecipientUnresolved, "recipients"));
        return ShootAsync(RecipientsShot, outlook, before: null, arrange: async (context, window, files) =>
        {
            var vm = window.ViewModel;

            // An office written to earlier (the journal "już wysłane"), skipped by this run.
            await context.Services.GetRequiredService<CorrespondenceJournal>()
                .AppendAsync("biuro@powiat-lodzki.contoso.pl", "Zaproszenie na konferencję – edycja jesienna", TestContext.Current.CancellationToken);

            var contacts = files.Write("kontakty_urzedy.csv", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ContactsCsv)).ToArray());
            await vm.Recipients.AddFilesAsync([contacts]);
            await UiWait.UntilAsync(() => vm.Recipients.Items.Count >= 9, "the offices of the CSV file", timeoutSeconds: 30);
            vm.Recipients.Items.Single(r => r.Email == "ug@brojce.contoso.pl").Include = false;

            vm.Message.Subject = Subject;
            vm.Message.BodyHtml = Body;
            vm.Message.DelaySeconds = 0;
            await vm.Send.SendCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => !vm.Send.IsSending, "the run through the in-memory Outlook", timeoutSeconds: 60);

            window.CorrespondenceTabs.SelectedItem = window.RecipientsTab;
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            window.RecipientsGrid.SelectedItem = vm.Recipients.View.FirstOrDefault(r => r.StatusKind == RecipientStatusKind.Failed);
        });
    }

    [Fact]
    public Task Screenshot27_CorrespondenceMessage() =>
        ShootAsync(MessageShot, new FakeOutlookClient(), before: null, arrange: async (_, window, files) =>
        {
            var vm = window.ViewModel;
            vm.Recipients.AddRows(
            [
                new ImportedContact("sekretariat@andrespol.contoso.pl", "Urząd Gminy Andrespol",
                    new Dictionary<string, string> { ["Miejscowość"] = "Andrespol", ["Kod pocztowy"] = "95-020", ["Powiat"] = "łódzki wschodni" }),
                new ImportedContact("kancelaria@zgierz.contoso.pl", "Urząd Miasta Zgierza",
                    new Dictionary<string, string> { ["Miejscowość"] = "Zgierz", ["Kod pocztowy"] = "95-100", ["Powiat"] = "zgierski" }),
            ], "kontakty_urzedy.csv");
            vm.Message.Subject = Subject;
            vm.Message.BodyHtml = Body;

            // The signature of the (in-memory) Outlook, two attachments, a read receipt, voting and a delivery time.
            await UiWait.UntilAsync(() => vm.Message.Signatures.Any(s => s.Name == FakeOutlookSignatureSource.SignatureName), "the Outlook signatures");
            vm.Message.SelectedSignature = vm.Message.Signatures.First(s => s.Name == FakeOutlookSignatureSource.SignatureName);
            var program = files.Write("Program konferencji 2026.pdf", PdfLike(412_000));
            var form = files.Write("Formularz zgłoszeniowy.docx", PdfLike(38_000));
            await vm.Message.AddAttachmentFilesAsync([program, form]);
            await UiWait.UntilAsync(() => vm.Message.Attachments.Count == 2, "the attachments");
            vm.Message.ReadReceipt = true;
            vm.Message.UseVoting = true;
            vm.Message.DeliverLater = true;
            vm.Message.DeliverTime = "8:30";

            window.CorrespondenceTabs.SelectedItem = window.MessageTab;
            await UiTestSupport.WaitForIdleAsync(rounds: 3);
        });

    [Fact]
    public Task Screenshot28_CorrespondenceMailboxOwners() =>
        ShootAsync(MailboxOwnersShot, new FakeOutlookClient(), before: context => context.Directory.AddMailboxOwnerSample(context.Company), arrange: async (_, window, _) =>
        {
            var mailboxes = window.ViewModel.Mailboxes;
            window.ViewModel.ShowMailboxesCommand.Execute(null);
            await mailboxes.FindOwnersCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => !mailboxes.IsRunning && mailboxes.Rows.Count > 0, "the mailbox owners of the sample", timeoutSeconds: 30);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            window.MailboxesGrid.SelectedItem = mailboxes.View.FirstOrDefault(r => r.MailboxName == "Laboratorium");
        });

    /// <summary>
    /// One picture: a fresh host in Polish (dialogs scripted: every confirmation answered yes) over the sample company with
    /// <paramref name="outlook"/>, connected, light theme; the Korespondencja window at the picture's client size, arranged
    /// by <paramref name="arrange"/> (files it needs go to a temporary folder that is deleted after the picture). The theme is
    /// process wide, so it is reset to the system theme afterwards.
    /// </summary>
    private static Task ShootAsync(ScreenshotInfo shot, FakeOutlookClient outlook, Action<UiTestContext>? before,
        Func<UiTestContext, CorrespondenceWindow, TemporaryFiles, Task> arrange)
    {
        ScreenshotWriter.RequireOutputDirectory();
        return WpfTestHost.RunAsync(async () =>
        {
            var (created, dialogs) = await TestDialogService.CreateContextAsync(
                "pl", services => services.Replace(ServiceDescriptor.Singleton<IOutlookClient>(outlook)));
            await using var context = created;
            using var files = new TemporaryFiles();
            dialogs.ConfirmAnswer = true;
            before?.Invoke(context);
            await context.ConnectAsync();
            var themes = context.Services.GetRequiredService<IThemeService>();
            themes.Apply(ThemePreference.Light);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            try
            {
                var viewModel = context.Services.GetRequiredService<CorrespondenceViewModelFactory>().Create(new CorrespondenceRequest([]));
                var window = (CorrespondenceWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(viewModel);
                await ScreenshotWriter.ShowWithClientSizeAsync(window, ClientWidth, ClientHeight);
                await UiWait.InitializedAsync(window.Initialization);
                await UiTestSupport.WaitForIdleAsync();
                await arrange(context, window, files);
                await UiTestSupport.WaitForIdleAsync(rounds: 3);

                await ScreenshotWriter.SaveAsync(window, shot, dark: false);
                context.AssertHealthy();
            }
            finally
            {
                UiTestSupport.CloseAllWindows();
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                themes.Apply(ThemePreference.System);
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
            }
        });
    }

    /// <summary>Bytes that start like a PDF (an attachment of the given size; its content is never read).</summary>
    private static byte[] PdfLike(int length)
    {
        var data = new byte[length];
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(data, 0);
        return data;
    }

    /// <summary>Files of one picture in a private temporary folder, deleted with it.</summary>
    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string _folder = System.IO.Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AdMng-screenshot-" + Guid.NewGuid().ToString("N"))).FullName;

        public string Write(string name, byte[] content)
        {
            var path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(_folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temporary folder left behind is harmless.
            }
        }
    }
}
