using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Messaging;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>The message window (README U1): opened through the launcher, compose → preview → send → report.</summary>
[Collection(UiTestCollection.Name)]
public sealed class UserMessagesWindowTests
{
    [Fact]
    public Task Launcher_OpensTheWindow_PreviewSendAndReport() => WpfTestHost.RunAsync(async () =>
    {
        var transport = new FakeUserMessageTransport();
        var (context, dialogs) = await TestDialogService.CreateContextAsync(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IDirectSmtpTransportFactory>(transport)));
        await using var _ = context;
        await context.ConnectAsync();
        await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(s =>
        {
            s.UserMessages.Smtp.Host = "smtp.contoso.test";
            s.UserMessages.TestAddress = "operator@contoso.test";
        }, TestContext.Current.CancellationToken);
        var windows = context.Services.GetRequiredService<IWindowService>();
        Assert.True(windows.IsRegistered(typeof(UserMessageComposerViewModel)));
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        Assert.True(main.Shell.CanSendMessages);
        Assert.Contains(main.Shell.CommandDescriptors, d => d.Id == "object.sendMessage");

        // "Wyślij wiadomość…" of the review list "Locked": the matching template.
        var company = context.Company;
        await context.Services.GetRequiredService<IUserMessageLauncher>()
            .ShowComposerAsync([company.ZanetaSzymanska, company.JanKowalski], AccountCheck.Locked);
        await UiTestSupport.WaitForIdleAsync();
        var window = Assert.Single(Application.Current.Windows.OfType<UserMessagesWindow>());
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        var composer = window.ViewModel;
        Assert.Equal(BuiltInMessageTemplates.AccountLocked, composer.SelectedTemplate!.Value);
        Assert.Equal(Visibility.Visible, window.ComposePanel.Visibility);
        Assert.Equal(Visibility.Collapsed, window.PreviewPanel.Visibility);
        Assert.Equal(2, window.RecipientsList.Items.Count);
        Assert.Equal("Konto {sAMAccountName} zostało zablokowane", window.SubjectBox.Text);
        Assert.Equal("Sending directly via SMTP: smtp.contoso.test:587", window.TransportText.Text);
        Assert.True(window.PlanButton.IsEnabled);

        await composer.PlanCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, window.PreviewPanel.Visibility);
        Assert.Equal(Visibility.Collapsed, window.ComposePanel.Visibility);
        Assert.Equal(2, window.ItemsList.Items.Count);
        var zaneta = composer.AllItems.Single(i => i.Name == "Żaneta Szymańska");
        window.ItemsList.SelectedItem = zaneta;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("Konto zaneta.szymanska zostało zablokowane", window.SelectedSubjectText.Text);
        Assert.Contains("13 października 2026, 09:30", window.SelectedTextBody.Text, StringComparison.Ordinal);
        Assert.Equal(PlannedMessageStatus.Skipped, composer.AllItems.Single(i => i.Name == "Jan Kowalski").Status);
        Assert.Equal("Send (1)", window.SendButton.Content);

        await composer.SendTestCommand.ExecuteAsync(null);
        Assert.Equal("operator@contoso.test", Assert.Single(transport.Accepted).To[0].Address);

        dialogs.ConfirmAnswer = true;
        await composer.SendCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, window.ReportPanel.Visibility);
        Assert.Equal(2, window.ReportList.Items.Count);
        Assert.Equal(2, transport.Accepted.Count);
        Assert.Equal(composer.ReportSummary, window.ReportSummaryText.Text);
        Assert.Equal(Visibility.Visible, window.ExportReportButton.Visibility);
        context.AssertHealthy();
    });

    [Fact]
    public Task SettingsTab_ShowsTheMessageSettings_InPolish() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var settings = context.Services.GetRequiredService<Presentation.Dialogs.IDialogViewModelFactory>().CreateSettings();
        var window = (SettingsWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(settings);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        window.SettingsTabs.SelectedItem = window.MessagesTab;
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal("Wiadomości e-mail", window.MessagesTab.Header);
        Assert.False(window.TestConnectionButton.IsEnabled);
        window.SmtpHostBox.Text = "smtp.contoso.test";
        window.FromAddressBox.Text = "zły adres";
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.TestConnectionButton.IsEnabled);
        Assert.Equal("Adres nadawcy: nieprawidłowy adres e-mail.", window.ErrorMessage.Text);
        Assert.False(window.SaveButton.IsEnabled);
        settings.Messages!.Tls = settings.Messages.TlsModes.Single(m => m.Value == SmtpTlsMode.None);
        window.FromAddressBox.Text = string.Empty;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.InsecureTlsConfirmBox.IsVisible);
        Assert.Equal("Potwierdź ostrzeżenie o wysyłce bez szyfrowania.", window.ErrorMessage.Text);
        window.InsecureTlsConfirmBox.IsChecked = true;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.SaveButton.IsEnabled);
        context.AssertHealthy();
    });
}
