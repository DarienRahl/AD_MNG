using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using AdMng.Testing.BulkCreate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// Screenshots 40–42 of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.6): <em>Data</em> with a range pasted from
/// Excel, <em>Columns</em> with the automatic mapping and <em>Names and options</em> with the <em>Numbered accounts</em>
/// profile, the <em>Numbering</em> panel and the live samples (<em>Dane</em>, <em>Kolumny</em>, <em>Nazwy i opcje</em>,
/// <em>Konta numerowane</em>, <em>Numeracja</em> in the Polish UI) – UI in the language of
/// <see cref="ScreenshotWriter.LanguageVariable"/>, Polish sample data (the Polish headers are recognized in every UI
/// language), light theme, on the bulk-create sample (OU <em>Nowi pracownicy</em>, live kd201, kd203, kd205 and kd207 in the
/// Recycle Bin). Like <see cref="ScreenshotTests"/> they run only when <see cref="ScreenshotWriter.DirectoryVariable"/> is set;
/// the class name starts with "ScreenshotTests" so the CI step "Screenshots" takes them too.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScreenshotTestsBulkCreateInput
{
    private const double WindowWidth = 1440;
    private const double WindowHeight = 900;

    private static readonly ScreenshotInfo Data = new(
        "40-bulk-create-data.png",
        "Zakładanie wielu kont – krok Dane: zakres wklejony z Excela (telefon komórkowy, imię, nazwisko, e-mail, stanowisko, dział, przełożony), nagłówki rozpoznane automatycznie",
        "Creating multiple accounts – the Data step: a range pasted from Excel (mobile, given name, surname, e-mail, job title, department, manager) with the headers recognized");

    private static readonly ScreenshotInfo Columns = new(
        "41-bulk-create-columns.png",
        "Zakładanie wielu kont – krok Kolumny: kolumny przypisane automatycznie do atrybutów (Telefon komórkowy → mobile, Przełożony → menedżer) z przykładami z arkusza",
        "Creating multiple accounts – the Columns step: the columns mapped to attributes automatically (Telefon komórkowy → mobile, Przełożony → Manager) with samples from the sheet");

    private static readonly ScreenshotInfo Rules = new(
        "42-bulk-create-rules.png",
        "Zakładanie wielu kont – krok Nazwy i opcje: profil „Konta numerowane (kd201…)”, panel Numeracja z pierwszym wolnym numerem kd208 i przykładami kont, sufiks UPN contoso.com, grupa GG_Nowi",
        "Creating multiple accounts – the Names and options step: the \"Numbered accounts (kd201…)\" profile, the Numbering panel with the first free number kd208 and sample accounts, the UPN suffix contoso.com, the group GG_Nowi");

    [Fact]
    public Task Screenshot40_BulkCreateData() => ShootAsync(async (context, sample, window, wizard) =>
    {
        Assert.Equal(5, wizard.Source.Rows.Count);
        await ScreenshotWriter.SaveAsync(window, Data, dark: false);
    });

    [Fact]
    public Task Screenshot41_BulkCreateColumns() => ShootAsync(async (context, sample, window, wizard) =>
    {
        await wizard.NextStepCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Null(wizard.Columns.MappingError);
        Assert.Contains(wizard.Columns.Mappings, m => m.Attribute == Attr.Mobile);
        await ScreenshotWriter.SaveAsync(window, Columns, dark: false);
    });

    [Fact]
    public Task Screenshot42_BulkCreateRules() => ShootAsync(async (context, sample, window, wizard) =>
    {
        await wizard.NextStepCommand.ExecuteAsync(null);
        await wizard.NextStepCommand.ExecuteAsync(null);
        var rules = wizard.Rules;
        rules.Profile = rules.Profiles.Single(p => p.Value == BulkCreateProfileStore.NumberedId);
        await UiWait.UntilAsync(() => rules.FirstFreeText?.Contains("kd208", StringComparison.Ordinal) == true, "the first free number");
        rules.UpnSuffix = "contoso.com";
        var group = await context.Directory.Session.GetAsync(sample.GroupNew, DirectoryItem.Attributes, TestContext.Current.CancellationToken);
        rules.Groups.Add(DirectoryItem.From(group!, context.Localizer()));
        await UiWait.UntilAsync(() => rules.Rules.Any(r => r.Sample.StartsWith("kd208@contoso.com", StringComparison.Ordinal)), "the live samples");
        await UiTestSupport.WaitForIdleAsync(rounds: 4);
        Assert.Null(rules.Error);
        await ScreenshotWriter.SaveAsync(window, Rules, dark: false);
    });

    private static Task ShootAsync(Func<UiTestContext, BulkCreateSample, BatchEditorWindow, BulkCreateViewModel, Task> shoot)
    {
        ScreenshotWriter.RequireOutputDirectory();
        return WpfTestHost.RunAsync(async () =>
        {
            var clipboard = new TableClipboard();
            await using var context = await UiTestContext.CreateAsync(
                ScreenshotWriter.Language, services => services.Replace(ServiceDescriptor.Scoped<IClipboardService>(_ => clipboard)));
            var themes = context.Services.GetRequiredService<IThemeService>();
            themes.Apply(ThemePreference.Light);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            try
            {
                var sample = context.Directory.AddBulkCreateSample(context.Company);
                await context.ConnectAsync();
                var editor = context.Services.GetRequiredService<IDialogViewModelFactory>()
                    .CreateBatchEditor(new BatchEditorRequest([]) { Create = new BulkCreateRequest(Core.Model.ObjectKind.User, sample.NewEmployeesOu) });
                var window = (BatchEditorWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(editor);
                await ScreenshotWriter.ShowWithClientSizeAsync(window, WindowWidth, WindowHeight);
                await UiWait.InitializedAsync(window.Initialization);
                var wizard = editor.BulkCreate!;
                clipboard.Content = new ClipboardContent(null, null, null, ExcelSamples.PolishTsv);
                await wizard.Source.PasteCommand.ExecuteAsync(null);
                await UiTestSupport.WaitForIdleAsync(rounds: 3);
                await shoot(context, sample, window, wizard);
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
}
