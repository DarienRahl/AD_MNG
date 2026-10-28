using System.Windows;
using System.Windows.Input;
using AdMng.App.Correspondence;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests.Correspondence;

/// <summary>
/// The dialogs of the Korespondencja window through the connection window's <see cref="DialogService"/> (U5, design note
/// 9.4): the one-line and multiline text prompt (OK, Cancel, closed), the read-only monospace report with "Kopiuj" and
/// "Zamknij" (Polish UI), and the Korespondencja window itself – one per connection window without objects, a new one for
/// objects or mailbox owners. No binding errors.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CorrespondenceDialogsTests
{
    [Fact]
    public Task Prompt_OneLine_ReturnsTheTypedText() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var dialogs = context.Services.GetRequiredService<ICorrespondenceDialogs>();
        var drive = UiWait.DriveModalAsync<TextPromptWindow>(async window =>
        {
            await UiTestSupport.WaitForIdleAsync();
            Assert.Equal("Dodaj odbiorcę", window.Title);
            Assert.Equal("Adres e-mail odbiorcy:", window.MessageText.Text);
            Assert.Equal(Visibility.Visible, window.MessageText.Visibility);
            Assert.Equal("jan.kowalski@contoso.pl", window.PromptTextBox.Text);
            Assert.Equal("jan.kowalski@contoso.pl", window.PromptTextBox.SelectedText);
            Assert.False(window.PromptTextBox.AcceptsReturn);
            Assert.Equal("OK", window.OkButton.Content);
            Assert.Equal("Anuluj", window.CancelButton.Content);
            Assert.True(window.OkButton.IsDefault);
            Assert.True(window.CancelButton.IsCancel);
            window.PromptTextBox.Text = "Łucja Żółkiewska <lucja.zolkiewska@contoso.pl>";
            window.OkButton.Command.Execute(null);
        });

        var text = await dialogs.PromptTextAsync("Dodaj odbiorcę", "Adres e-mail odbiorcy:", "jan.kowalski@contoso.pl", multiline: false);
        await drive;

        Assert.Equal("Łucja Żółkiewska <lucja.zolkiewska@contoso.pl>", text);
        Assert.Empty(Application.Current.Windows.OfType<TextPromptWindow>());
        context.AssertHealthy();
    });

    [Fact]
    public Task Prompt_Multiline_HasATallBox_AndCancelGivesNull() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var dialogs = context.Services.GetRequiredService<ICorrespondenceDialogs>();
        var drive = UiWait.DriveModalAsync<TextPromptWindow>(async window =>
        {
            await UiTestSupport.WaitForIdleAsync();
            Assert.True(window.PromptTextBox.AcceptsReturn);
            Assert.Equal(TextWrapping.Wrap, window.PromptTextBox.TextWrapping);
            Assert.Equal(TextPromptWindow.MultilineHeight, window.PromptTextBox.Height);
            Assert.Equal("a@contoso.pl\nb@contoso.pl", window.PromptTextBox.Text);
            Assert.Contains(window.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.Return && b.Modifiers == ModifierKeys.Control
                                                                            && ReferenceEquals(b.Command, window.ViewModel.OkCommand));
            window.PromptTextBox.Text = "zmienione";
            window.CancelButton.Command.Execute(null);
        });

        var text = await dialogs.PromptTextAsync("Pominięcia", string.Empty, "a@contoso.pl\nb@contoso.pl", multiline: true);
        await drive;

        Assert.Null(text);
        context.AssertHealthy();
    });

    [Fact]
    public Task Prompt_ClosedByTheOperator_GivesNull_AndAnEmptyMessageIsHidden() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var dialogs = context.Services.GetRequiredService<ICorrespondenceDialogs>();
        var drive = UiWait.DriveModalAsync<TextPromptWindow>(async window =>
        {
            await UiTestSupport.WaitForIdleAsync();
            Assert.Equal(Visibility.Collapsed, window.MessageText.Visibility);
            window.Close();
            await UiTestSupport.WaitForIdleAsync();
        });

        var text = await dialogs.PromptTextAsync("Tytuł", string.Empty, "tekst", multiline: false);
        await drive;

        Assert.Null(text);
        context.AssertHealthy();
    });

    [Fact]
    public Task Report_IsReadOnlyMonospace_CopiesAndCloses_InPolish() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new RecordingClipboard();
        await using var context = await UiTestContext.CreateAsync(
            language: "pl",
            configure: services => services.Replace(ServiceDescriptor.Scoped<IClipboardService>(_ => clipboard)));
        var dialogs = context.Services.GetRequiredService<ICorrespondenceDialogs>();
        const string Report = "Outlook: połączono (2 konta)\nWersja: 16.0.18025\nProfil: Outlook\nPodpisy: Zespół IT, Łódź – sekretariat\n"
                              + "Folder Pobrane: C:\\Users\\jkowalski\\Downloads";
        var drive = UiWait.DriveModalAsync<ReportWindow>(async window =>
        {
            await UiTestSupport.WaitForIdleAsync();
            Assert.Equal("Sprawdź Outlooka", window.Title);
            Assert.Equal(Report, window.ReportTextBox.Text);
            Assert.True(window.ReportTextBox.IsReadOnly);
            Assert.Contains("Consolas", window.ReportTextBox.FontFamily.Source, StringComparison.Ordinal);
            Assert.Equal("Kopiuj", window.CopyButton.Content);
            Assert.Equal("Zamknij", window.CloseButton.Content);
            Assert.True(window.CloseButton.IsCancel);

            window.CopyButton.Command.Execute(null);
            Assert.Equal(Report, clipboard.Text);
            Assert.True(window.IsVisible);
            window.CloseButton.Command.Execute(null);
        });

        await dialogs.ShowReportAsync("Sprawdź Outlooka", Report);
        await drive;

        Assert.Empty(Application.Current.Windows.OfType<ReportWindow>());
        Assert.Equal(Report, clipboard.Text);
        context.AssertHealthy();
    });

    [Fact]
    public Task Correspondence_IsOnePerConnectionWindow_WithoutObjects_ElseANewWindow() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var windows = context.Services.GetRequiredService<IWindowService>();
        Assert.True(windows.IsRegistered(typeof(CorrespondenceViewModel)));
        Assert.True(windows.IsRegistered(typeof(TextPromptViewModel)));
        Assert.True(windows.IsRegistered(typeof(ReportViewModel)));
        Assert.Same(context.Services.GetRequiredService<DialogService>(), context.Services.GetRequiredService<ICorrespondenceLauncher>());
        Assert.Same(context.Services.GetRequiredService<DialogService>(), context.Services.GetRequiredService<ICorrespondenceDialogs>());
        var launcher = context.Services.GetRequiredService<ICorrespondenceLauncher>();
        try
        {
            await launcher.ShowCorrespondenceAsync(new CorrespondenceRequest([]));
            await launcher.ShowCorrespondenceAsync(new CorrespondenceRequest([]));
            await UiTestSupport.WaitForIdleAsync();
            var tool = Assert.Single(Application.Current.Windows.OfType<CorrespondenceWindow>());
            Assert.Same(tool, windows.FindOpen(ToolWindows.CorrespondenceKey));
            Assert.Empty(tool.ViewModel.Request.DistinguishedNames);

            await launcher.ShowCorrespondenceAsync(new CorrespondenceRequest([context.Company.JanKowalski]));
            await launcher.ShowCorrespondenceAsync(new CorrespondenceRequest([context.Company.JanKowalski]));
            await launcher.ShowCorrespondenceAsync(new CorrespondenceRequest([], CorrespondenceStart.MailboxOwners));
            await UiTestSupport.WaitForIdleAsync();

            var all = Application.Current.Windows.OfType<CorrespondenceWindow>().ToList();
            Assert.Equal(4, all.Count);
            Assert.Equal(2, all.Count(w => w.ViewModel.Request.DistinguishedNames.SequenceEqual([context.Company.JanKowalski])));
            var mailboxes = Assert.Single(all, w => w.ViewModel.Request.Start == CorrespondenceStart.MailboxOwners);
            Assert.True(mailboxes.ViewModel.IsMailboxesTabVisible);
            Assert.Equal(CorrespondenceTab.Mailboxes, mailboxes.ViewModel.SelectedTab);
            foreach (var window in all)
            {
                await UiWait.InitializedAsync(window.Initialization);
            }

            context.AssertHealthy();
        }
        finally
        {
            UiTestSupport.CloseAllWindows();
            await UiTestSupport.WaitForIdleAsync();
        }
    });

    /// <summary>Records what "Kopiuj" puts on the clipboard (no real clipboard in this test).</summary>
    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public bool SetText(string text)
        {
            Text = text;
            return true;
        }

        public bool SetSecret(string secret) => false;
    }
}
