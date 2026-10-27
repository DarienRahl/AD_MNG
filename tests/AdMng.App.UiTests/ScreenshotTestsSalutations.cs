using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Messaging;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// Release screenshot of the title-aware salutations (U1d): the preview with the "Zwrot" column (guessed ≈ and fallback ⚠
/// marks) and the derived fields of the selected message. Like <see cref="ScreenshotTests"/> it runs only when
/// <see cref="ScreenshotWriter.DirectoryVariable"/> is set; the class name starts with "ScreenshotTests" so the CI step
/// "Screenshots" (filter <c>FullyQualifiedName~AdMng.App.UiTests.ScreenshotTests</c>) takes it too.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScreenshotTestsSalutations
{
    private static readonly ScreenshotInfo Salutations = new(
        "25-user-messages-salutations.png",
        "Wiadomości do użytkowników – zwroty grzecznościowe z tytułem i płcią: kolumna „Zwrot” (≈ zgadnięte, ⚠ zwrot neutralny) i pola wyliczone zaznaczonej wiadomości",
        "Messages to users – title-aware salutations: the \"Salutation\" column (≈ guessed, ⚠ neutral fallback) and the derived fields of the selected message");

    [Fact]
    public Task Screenshot25_UserMessageSalutations()
    {
        ScreenshotWriter.RequireOutputDirectory();
        return WpfTestHost.RunAsync(async () =>
        {
            var (context, _) = await TestDialogService.CreateContextAsync("pl", services =>
                services.Replace(ServiceDescriptor.Singleton<IDirectSmtpTransportFactory>(new FakeUserMessageTransport())));
            await using var _ = context;
            var sample = context.Directory.AddSalutationSamples(context.Company);
            await context.ConnectAsync();
            var themes = context.Services.GetRequiredService<IThemeService>();
            themes.Apply(ThemePreference.Light);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            try
            {
                await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(
                    s => s.UserMessages.Smtp.Host = "smtp.contoso.com", TestContext.Current.CancellationToken);
                var composer = context.Services.GetRequiredService<IDialogViewModelFactory>()
                    .CreateUserMessageComposer(new UserMessageRequest(sample.All, BuiltInMessageTemplates.Custom));
                composer.Subject = "Zmiana hasła do konta {sAMAccountName}";
                composer.Body = "{$salutation},\nprosimy, aby {$honorific} {$byGender:zmienił/zmieniła} hasło do konta {sAMAccountName} "
                    + "przed {$passwordExpires}.\n\nZ wyrazami szacunku\nDział IT";
                var window = (UserMessagesWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(composer);
                await ScreenshotWriter.ShowAsync(window);
                await UiWait.InitializedAsync(window.Initialization);
                await composer.PlanCommand.ExecuteAsync(null);
                await UiWait.UntilAsync(() => window.ItemsList.Items.Count == sample.All.Count, "the planned messages");
                window.ItemsList.SelectedItem = composer.AllItems.First(i => i.Name == "Sasza Kim");
                await UiTestSupport.WaitForIdleAsync(rounds: 3);

                await ScreenshotWriter.SaveAsync(window, Salutations, dark: false);
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
