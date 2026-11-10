using AdMng.App.BulkCreate;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// Screenshot 43 of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.7): the step <em>Check</em>
/// (<em>Sprawdzenie</em>) with the operator's sheet computed – OK rows, a warning (an ambiguous manager), an error (a login
/// already taken) and a possible existing account, the frozen name columns and the uniqueness scope. UI in the language of
/// <see cref="ScreenshotWriter.LanguageVariable"/> over Polish sample data, light theme. Like <see cref="ScreenshotTests"/> it
/// runs only when <see cref="ScreenshotWriter.DirectoryVariable"/> is set; the class name starts with "ScreenshotTests" so
/// the CI step "Screenshots" takes it too.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScreenshotTestsBulkCreateCheck
{
    private const double WindowWidth = 1440;
    private const double WindowHeight = 860;

    private static readonly ScreenshotInfo Check = new(
        "43-bulk-create-check.png",
        "Zakładanie wielu kont – krok Sprawdzenie: każdy wiersz arkusza z wyliczonymi wartościami (kd208…kd212, UPN, adres e-mail, telefon komórkowy), stan OK / ostrzeżenie / błąd z uwagami, niejednoznaczny menedżer i możliwy duplikat istniejącego konta",
        "Creating multiple accounts – the Check step: every row of the sheet with its computed values (kd208…kd212, UPN, e-mail address, mobile phone), the OK / warning / error status with its notes, an ambiguous manager and a possible duplicate of an existing account");

    [Fact]
    public Task Screenshot43_BulkCreateCheck() => ShootAsync(async context =>
    {
        var editor = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateBatchEditor(new BatchEditorRequest([])
        {
            Create = new BulkCreateRequest(ObjectKind.User, BulkCreateSamples.ContainerDn),
        });
        var window = (BatchEditorWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(editor);
        await ScreenshotWriter.ShowWithClientSizeAsync(window, WindowWidth, WindowHeight);
        await window.Initialization;
        editor.BulkCreate!.ShowSample(BulkCreateSamples.Computation(), BulkCreateSamples.Plan());
        var check = editor.BulkCreate.Check;
        check.SelectedRow = check.Rows[1];
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal(5, check.Rows.Count);
        Assert.True(editor.PreviewCommand.CanExecute(null));
        await ScreenshotWriter.SaveAsync(window, Check, dark: false);
    });

    private static Task ShootAsync(Func<UiTestContext, Task> shoot)
    {
        ScreenshotWriter.RequireOutputDirectory();
        return WpfTestHost.RunAsync(async () =>
        {
            var (created, _) = await TestDialogService.CreateContextAsync(ScreenshotWriter.Language);
            await using var context = created;
            var themes = context.Services.GetRequiredService<IThemeService>();
            themes.Apply(ThemePreference.Light);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            try
            {
                await shoot(context);
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
