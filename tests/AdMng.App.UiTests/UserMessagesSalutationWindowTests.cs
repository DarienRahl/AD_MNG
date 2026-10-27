using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Messaging;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// Title-aware salutations (U1d) in the message window and the settings: the "Zwrot" column, "Pola wyliczone", the gender
/// override of one message, the "Zwroty" options and the "Zwroty grzecznościowe" section with its check box.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class UserMessagesSalutationWindowTests
{
    private const string Body = "{$salutation},\nprosimy, aby {$honorific} {$byGender:zmienił/zmieniła} hasło.";

    [Fact]
    public Task Preview_ShowsTheSalutationColumn_TheDerivedFields_AndReplansOnTheOverride() => WpfTestHost.RunAsync(async () =>
    {
        var transport = new FakeUserMessageTransport();
        var (context, _) = await TestDialogService.CreateContextAsync(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IDirectSmtpTransportFactory>(transport)));
        await using var _ = context;
        var sample = context.Directory.AddSalutationSamples(context.Company);
        await context.ConnectAsync();
        await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(
            s => s.UserMessages.Smtp.Host = "smtp.contoso.test", TestContext.Current.CancellationToken);
        var composer = context.Services.GetRequiredService<IDialogViewModelFactory>()
            .CreateUserMessageComposer(new UserMessageRequest(sample.All, BuiltInMessageTemplates.Custom));
        var window = (UserMessagesWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(composer);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal(Visibility.Visible, window.SalutationOptionsPanel.Visibility);
        Assert.True(window.UseTitlesBox.IsChecked);
        Assert.Equal(4, window.TemplateUnknownGenderBox.Items.Count);
        window.SubjectBox.Text = "Zmiana hasła";
        window.BodyBox.Text = Body;
        await UiTestSupport.WaitForIdleAsync();

        await composer.PlanCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(10, window.ItemsList.Items.Count);
        var kim = composer.AllItems.Single(i => i.Name == "Sasza Kim");
        Assert.Equal("⚠ Szanowna Pani / Szanowny Panie", kim.SalutationColumnText);
        window.ItemsList.SelectedItem = kim;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, window.DerivedFieldsPanel.Visibility);
        Assert.Equal(3, window.DerivedFieldsList.Items.Count);
        Assert.Equal(SalutationGenderChoice.Automatic, window.GenderOverrideBox.SelectedValue);

        window.GenderOverrideBox.SelectedValue = SalutationGenderChoice.Feminine;
        await UiWait.UntilAsync(() => !ReferenceEquals(composer.AllItems.Single(i => i.Name == "Sasza Kim"), kim), "the re-planned message");
        await UiTestSupport.WaitForIdleAsync();

        var replanned = composer.AllItems.Single(i => i.Name == "Sasza Kim");
        Assert.Equal("Szanowna Pani", replanned.SalutationColumnText);
        Assert.Same(replanned, window.ItemsList.SelectedItem);
        Assert.Equal(SalutationGenderChoice.Feminine, window.GenderOverrideBox.SelectedValue);
        Assert.StartsWith("Szanowna Pani,", window.SelectedTextBody.Text, StringComparison.Ordinal);

        composer.Filter = composer.Filters.Single(f => f.Value == UserMessagePreviewFilter.Guessed);
        await UiTestSupport.WaitForIdleAsync();
        Assert.DoesNotContain(window.ItemsList.Items.OfType<UserMessageItemViewModel>(), i => i.Name == "Nikola Lewandowski");
        context.AssertHealthy();
    });

    [Fact]
    public Task SettingsSection_ChecksTypedValues_AndBlocksAnInvalidAttribute_InPolish() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var settings = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateSettings();
        var window = (SettingsWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(settings);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        window.SettingsTabs.SelectedItem = window.MessagesTab;
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal("personalTitle", window.TitleAttributeBox.Text);
        Assert.True(window.TitleFromDisplayNameBox.IsChecked);
        Assert.Equal(11, window.TitlesList.Items.Count);
        Assert.Equal(3, window.UnknownGenderBox.Items.Count);

        window.CheckTitleBox.Text = "prof. dr hab.";
        window.CheckGivenNameBox.Text = "Jan";
        window.CheckSurnameBox.Text = "Kowalski";
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("Szanowny Panie Profesorze", window.CheckPolishText.Text);
        Assert.Equal("Dear Professor Kowalski", window.CheckEnglishText.Text);
        Assert.Equal("Tytuł: prof. dr hab. (atrybut tytułu)", window.CheckTitleResultText.Text);

        window.AddGivenNameButton.Command.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Single(window.GivenNamesList.Items);

        window.GenderAttributeBox.Text = "unicodePwd";
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("Atrybut płci: nieprawidłowa nazwa atrybutu albo atrybut niedozwolony w wiadomościach.", window.ErrorMessage.Text);
        Assert.False(window.SaveButton.IsEnabled);
        window.GenderAttributeBox.Text = string.Empty;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.SaveButton.IsEnabled);
        context.AssertHealthy();
    });
}
