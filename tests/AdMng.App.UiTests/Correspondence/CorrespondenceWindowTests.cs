using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using AdMng.App.Behaviors;
using AdMng.App.Correspondence;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Ldap;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests.Correspondence;

/// <summary>
/// The Korespondencja window (U5, design note 9.1) over the real host, the sample company and the in-memory Outlook: opened
/// from the three commands (Tools once per connection window, the list's objects and the mailbox owners in new windows,
/// the menus in their places); every tab in Polish and English without binding errors; recipients pasted from the
/// clipboard build the field columns and hand their selection over; the editor writes the body and "Wstaw pole" goes to
/// the caret of the subject or the body; a run through the fake Outlook; the mailbox owners of the sample; the Outlook
/// section of the settings. The page logic is the view models' (Presentation tests); these tests check the view.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CorrespondenceWindowTests
{
    /// <summary>Wyślij, E-mail, Nazwa, Źródło, Status, Wysłano wcześniej.</summary>
    private const int FixedRecipientColumns = 6;

    [Fact]
    public Task Commands_OpenTheWindow_ToolsOncePerConnection_ObjectsAndMailboxOwnersInNewWindows() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var windows = context.Services.GetRequiredService<IWindowService>();
        Assert.True(windows.IsRegistered(typeof(CorrespondenceViewModel)));
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        try
        {
            foreach (var id in (string[])["tools.correspondence", "object.correspondence", "object.mailboxOwners"])
            {
                Assert.Contains(main.Shell.CommandDescriptors, d => d.Id == id);
            }

            // The menus: Tools after the account review, Action after "send message", the mailbox owners after it.
            var menu = MenuIds(main);
            Assert.Equal(menu.IndexOf("tools.accountReview") + 1, menu.IndexOf("tools.correspondence"));
            Assert.Equal(menu.IndexOf("object.sendMessage") + 1, menu.IndexOf("object.correspondence"));
            Assert.Equal(menu.IndexOf("object.correspondence") + 1, menu.IndexOf("object.mailboxOwners"));

            // Tools → Korespondencja, twice: one window per connection window, without recipients.
            Assert.True(main.Shell.ShowCorrespondenceCommand.CanExecute(null));
            await main.Shell.ShowCorrespondenceCommand.ExecuteAsync(null);
            await main.Shell.ShowCorrespondenceCommand.ExecuteAsync(null);
            await UiTestSupport.WaitForIdleAsync();
            var tool = Assert.Single(Application.Current.Windows.OfType<CorrespondenceWindow>());
            Assert.Same(tool, windows.FindOpen(ToolWindows.CorrespondenceKey));
            await UiWait.InitializedAsync(tool.Initialization);
            Assert.Empty(tool.ViewModel.Request.DistinguishedNames);
            Assert.Equal(CorrespondenceTab.Recipients, tool.ViewModel.SelectedTab);
            Assert.Same(tool.RecipientsTab, tool.CorrespondenceTabs.SelectedItem);
            Assert.Equal(Visibility.Collapsed, tool.MailboxesTab.Visibility);
            Assert.Equal(Visibility.Visible, tool.ShowMailboxesButton.Visibility);

            // The list of IT with Jan Kowalski selected: the object commands and the list's context menu.
            await main.Shell.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
            var document = main.Shell.SelectedDocument!;
            main.SelectedListView!.ListControl.SelectedItem = document.Items.Single(r => r.Name == "Jan Kowalski");
            await UiWait.UntilAsync(() => main.Shell.SendCorrespondenceCommand.CanExecute(null), "the selection reaches the shell");
            var contextMenu = MenuIds(main.SelectedListView.ListControl.ContextMenu);
            Assert.Equal(contextMenu.IndexOf("object.sendMessage") + 1, contextMenu.IndexOf("object.correspondence"));
            Assert.Equal(contextMenu.IndexOf("object.correspondence") + 1, contextMenu.IndexOf("object.mailboxOwners"));

            Assert.True(main.Shell.AskMailboxOwnersCommand.CanExecute(null));
            await main.Shell.SendCorrespondenceCommand.ExecuteAsync(null);
            await main.Shell.AskMailboxOwnersCommand.ExecuteAsync(null);
            await UiTestSupport.WaitForIdleAsync();
            var all = Application.Current.Windows.OfType<CorrespondenceWindow>().ToList();
            Assert.Equal(3, all.Count);
            foreach (var window in all)
            {
                await UiWait.InitializedAsync(window.Initialization);
            }

            var send = Assert.Single(all, w => w.ViewModel.Request is { Start: CorrespondenceStart.Recipients, DistinguishedNames.Count: 1 });
            Assert.True(DistinguishedName.AreEqual(context.Company.JanKowalski, send.ViewModel.Request.DistinguishedNames[0]));
            Assert.Same(send.RecipientsTab, send.CorrespondenceTabs.SelectedItem);
            Assert.Equal(Visibility.Collapsed, send.MailboxesTab.Visibility);

            var owners = Assert.Single(all, w => w.ViewModel.Request.Start == CorrespondenceStart.MailboxOwners);
            Assert.True(DistinguishedName.AreEqual(context.Company.JanKowalski, Assert.Single(owners.ViewModel.Request.DistinguishedNames)));
            Assert.Equal(Visibility.Visible, owners.MailboxesTab.Visibility);
            Assert.Same(owners.MailboxesTab, owners.CorrespondenceTabs.SelectedItem);
            Assert.Equal(Visibility.Collapsed, owners.ShowMailboxesButton.Visibility);

            // The scope of the mailbox owners: the selected objects (offered only to this window), else the domain.
            await UiTestSupport.WaitForIdleAsync();
            Assert.Equal(MailboxScopeKind.Selection, owners.ViewModel.Mailboxes.ScopeKind);
            Assert.True(owners.ScopeSelectionButton.IsVisible);
            Assert.True(owners.ScopeSelectionButton.IsChecked);
            Assert.False(owners.ScopeDomainButton.IsChecked);
            owners.ScopeDomainButton.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Assert.Equal(MailboxScopeKind.Domain, owners.ViewModel.Mailboxes.ScopeKind);
            Assert.False(owners.ScopeSelectionButton.IsChecked);
            Assert.Same(tool, windows.FindOpen(ToolWindows.CorrespondenceKey));
            context.AssertHealthy();
        }
        finally
        {
            UiTestSupport.CloseAllWindows();
            await UiTestSupport.WaitForIdleAsync();
        }
    });

    [Theory]
    [InlineData("pl", "Skrzynki", "1 · Odbiorcy", "2 · Wiadomość", "3 · Podgląd", "4 · Wysyłka", "5 · Odpowiedzi")]
    [InlineData("en", "Mailboxes", "1 · Recipients", "2 · Message", "3 · Preview", "4 · Sending", "5 · Responses")]
    public Task EveryTab_Renders_WithoutBindingErrors(string language, string mailboxes, string recipients, string message, string preview,
        string send, string responses) => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language);
        await context.ConnectAsync();
        var window = await OpenAsync(context, new CorrespondenceRequest([]));

        Assert.Equal(window.ViewModel.WindowTitle, window.Title);
        Assert.Equal([mailboxes, recipients, message, preview, send, responses], Tabs(window).Select(t => (string)t.Header));
        Assert.Equal([CorrespondenceTab.Mailboxes, CorrespondenceTab.Recipients, CorrespondenceTab.Message, CorrespondenceTab.Preview,
            CorrespondenceTab.Send, CorrespondenceTab.Responses], Tabs(window).Select(t => (CorrespondenceTab)t.Tag));
        Assert.Equal(FixedRecipientColumns, window.RecipientsGridView.Columns.Count);
        Assert.Equal(Visibility.Visible, window.NoRecipientsHint.Visibility);

        // "Właściciele skrzynek…" shows the "Skrzynki" tab and selects it.
        window.ShowMailboxesButton.Command.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, window.MailboxesTab.Visibility);
        Assert.Same(window.MailboxesTab, window.CorrespondenceTabs.SelectedItem);
        Assert.Equal(Visibility.Collapsed, window.ShowMailboxesButton.Visibility);
        Assert.False(window.ScopeSelectionButton.IsVisible);   // opened without selected objects
        Assert.True(window.ScopeDomainButton.IsChecked);

        // Every tab once: the view model follows the view…
        foreach (var tab in Tabs(window))
        {
            window.CorrespondenceTabs.SelectedItem = tab;
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal((CorrespondenceTab)tab.Tag, window.ViewModel.SelectedTab);
        }

        // …and the view follows the view model.
        window.ViewModel.SelectedTab = CorrespondenceTab.Preview;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Same(window.PreviewTab, window.CorrespondenceTabs.SelectedItem);

        // The status bar and the pages' controls show their view models.
        Assert.Equal(window.ViewModel.Outlook.StateText, window.OutlookStateText.Text);
        Assert.Equal(window.ViewModel.StatusText, window.StatusText.Text);
        Assert.Same(window.ViewModel.Message, window.MessageEditor.Host);
        Assert.Same(window.ViewModel.Message.SendModeChoices, window.SendModeBox.ItemsSource);
        Assert.Same(window.ViewModel.Preview.WidthChoices, window.PreviewWidthBox.ItemsSource);
        Assert.Same(window.ViewModel.Responses.Rows, window.ResponsesGrid.ItemsSource);
        Assert.Same(window.ViewModel.Send.Results, window.ResultsList.ItemsSource);
        Assert.Same(window.ViewModel.Mailboxes.View, window.MailboxesGrid.ItemsSource);
        Assert.Same(window.ViewModel.Recipients.View, window.RecipientsGrid.ItemsSource);
        Assert.Same(window.RecipientsGrid.SelectedItems, window.ViewModel.Recipients.SelectedItems);
        Assert.Same(window.AttachmentsList.SelectedItems, window.ViewModel.Message.SelectedAttachments);
        context.AssertHealthy();
    });

    [Fact]
    public Task PastedRecipients_BuildTheFieldColumns_FilterAndHandTheSelectionOver() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new TextClipboard(
            "E-mail\tNazwa\tMiejscowość\tKod pocztowy\r\n"
            + "urzad@gmina-zgierz.pl\tUrząd Gminy Zgierz\tZgierz\t95-100\r\n"
            + "sekretariat@andrespol.pl\tUrząd Gminy Andrespol\tAndrespol\t95-020\r\n");
        await using var context = await UiTestContext.CreateAsync(
            "pl", services => services.Replace(ServiceDescriptor.Scoped<IClipboardService>(_ => clipboard)));
        await context.ConnectAsync();
        var window = await OpenAsync(context, new CorrespondenceRequest([]));
        var recipients = window.ViewModel.Recipients;

        window.PasteRecipientsButton.Command.Execute(null);
        await UiWait.UntilAsync(() => window.RecipientsGrid.Items.Count == 2, "the pasted recipients");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Contains("Miejscowość", recipients.Columns);
        Assert.Contains("Kod pocztowy", recipients.Columns);
        Assert.Equal(Visibility.Collapsed, window.NoRecipientsHint.Visibility);

        // One grid column per field, after the fixed ones, headed as in the source and filled through the row indexer.
        var fieldColumns = window.RecipientsGridView.Columns.Skip(FixedRecipientColumns).ToList();
        Assert.Equal(recipients.Columns, fieldColumns.Select(c => ((TextBlock)c.Header).Text));
        Assert.All(fieldColumns, c => Assert.Equal(CorrespondenceWindow.FieldColumnWidth, c.Width));
        var cells = UiWait.Descendants<TextBlock>(window.RecipientsGrid).Select(t => t.Text).ToList();
        Assert.Contains("Andrespol", cells);
        Assert.Contains("95-020", cells);
        Assert.Contains("sekretariat@andrespol.pl", cells);

        // The filter box.
        window.SearchBox.Text = "Andrespol";
        await UiWait.UntilAsync(() => window.RecipientsGrid.Items.Count == 1, "the filtered list");
        window.SearchBox.Text = string.Empty;
        await UiWait.UntilAsync(() => window.RecipientsGrid.Items.Count == 2, "the whole list again");

        // The grid's selection is the page's: "Usuń zaznaczone" removes the selected row.
        var zgierz = recipients.View.Single(r => r.Email == "urzad@gmina-zgierz.pl");
        window.RecipientsGrid.SelectedItem = zgierz;
        Assert.Same(zgierz, Assert.Single(recipients.SelectedItems!.Cast<object>()));
        window.RemoveSelectedButton.Command.Execute(null);
        await UiWait.UntilAsync(() => recipients.Items.Count == 1 && window.RecipientsGrid.Items.Count == 1, "the row removed");
        Assert.Equal("sekretariat@andrespol.pl", recipients.Items[0].Email);

        // Clearing the columns rebuilds the grid down to the fixed ones.
        recipients.Columns.Clear();
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(FixedRecipientColumns, window.RecipientsGridView.Columns.Count);
        context.AssertHealthy();
    });

    [Theory]
    [InlineData("Miejscowość")]
    [InlineData("Kod pocztowy")]
    [InlineData("Uwagi (wewn.)")]
    [InlineData("Termin, godzina")]
    [InlineData("[Pole] ^ znak")]
    [InlineData("Kod_pocztowy")]
    public Task FieldColumns_BindAnyColumnName_ThroughTheRowIndexer(string column) => WpfTestHost.RunAsync(() =>
    {
        var row = new CorrespondenceRecipientViewModel(
            "anna.zielinska@contoso.pl", "Anna Zielińska", "kontakty.csv", new Dictionary<string, string> { [column] = "Zażółć gęślą jaźń" });
        var cell = new TextBlock { DataContext = row };
        cell.SetBinding(TextBlock.TextProperty, new Binding(CorrespondenceWindow.IndexerPath(column)) { Mode = BindingMode.OneWay });

        Assert.Equal("Zażółć gęślą jaźń", cell.Text);
        Assert.StartsWith("[", CorrespondenceWindow.IndexerPath(column), StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Editor_WritesTheBody_AndFieldsGoToTheCaretOfTheSubjectOrTheBody() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        var window = await OpenAsync(context, new CorrespondenceRequest([]));
        var message = window.ViewModel.Message;
        window.CorrespondenceTabs.SelectedItem = window.MessageTab;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Same(message, window.MessageEditor.Host);
        Assert.Same(message.FieldChoices, window.MessageEditor.FieldChoices);
        Assert.Same(message.FieldChoices, window.SubjectFieldBox.ItemsSource);

        // Typing in the editor reaches the view model's body.
        window.MessageEditor.MessageEditorBox.AppendText("Zapraszamy na spotkanie w Łodzi.");
        window.MessageEditor.FlushHtml();
        Assert.Contains("Zapraszamy na spotkanie w Łodzi.", message.BodyHtml, StringComparison.Ordinal);

        // A body set by the view model (a template) is shown by the editor.
        message.BodyHtml = "<p>Szanowni Państwo,</p><p>dziękujemy za <b>współpracę</b>.</p>";
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(message.BodyHtml, window.MessageEditor.Html);
        Assert.Equal(2, window.MessageEditor.MessageEditorBox.Document.Blocks.Count);

        // "Wstaw pole" with the subject box focused: at its caret.
        message.Subject = "Zaproszenie – ";
        await UiTestSupport.WaitForIdleAsync();
        Focused(window.SubjectBox);
        Assert.Equal(CorrespondenceWindow.FieldTarget.Subject, window.InsertTarget);
        window.SubjectBox.Select(window.SubjectBox.Text.Length, 0);
        message.InsertFieldCommand.Execute("{{nazwa}}");
        Assert.Equal("Zaproszenie – {{nazwa}}", message.Subject);
        Assert.Equal(window.SubjectBox.Text.Length, window.SubjectBox.CaretIndex);

        // The editor focused: at the editor's caret.
        Focused(window.MessageEditor);
        Assert.Equal(CorrespondenceWindow.FieldTarget.Body, window.InsertTarget);
        window.MessageEditor.MessageEditorBox.CaretPosition = window.MessageEditor.MessageEditorBox.Document.ContentEnd;
        message.InsertFieldCommand.Execute("{{zwrot}}");
        window.MessageEditor.FlushHtml();
        Assert.Contains("{{zwrot}}", message.BodyHtml, StringComparison.Ordinal);
        Assert.Equal("Zaproszenie – {{nazwa}}", message.Subject);

        // The subject's field box offers the fields of the message, the first one chosen.
        if (message.FieldChoices.Count > 0)
        {
            Assert.Equal(message.FieldChoices[0], window.SubjectFieldBox.SelectedItem);
        }

        // Every button writes pending edits first: a preview never shows a stale body.
        window.MessageEditor.MessageEditorBox.AppendText(" Do zobaczenia w Zgierzu.");
        window.RefreshPreviewButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, window.RefreshPreviewButton));
        Assert.Contains("Do zobaczenia w Zgierzu.", message.BodyHtml, StringComparison.Ordinal);

        // So does a click on a tab header (a click inside a page does not)…
        window.MessageEditor.MessageEditorBox.AppendText(" Pozdrawiamy z Łodzi.");
        var header = UiWait.Descendants<TextBlock>(window.PreviewTab).First();
        header.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        });
        Assert.Contains("Pozdrawiamy z Łodzi.", message.BodyHtml, StringComparison.Ordinal);

        // …and closing the window (the last draft is saved from the body).
        window.MessageEditor.MessageEditorBox.AppendText(" Do widzenia.");
        window.Close();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Contains("Do widzenia.", message.BodyHtml, StringComparison.Ordinal);
        context.AssertHealthy();
    });

    [Fact]
    public Task Send_ThroughTheFakeOutlook_RecordsTheMessage_AndShowsTheResults() => WpfTestHost.RunAsync(async () =>
    {
        var outlook = new FakeOutlookClient();
        var (context, dialogs) = await TestDialogService.CreateContextAsync(
            "pl", services => services.Replace(ServiceDescriptor.Singleton<IOutlookClient>(outlook)));
        await using var _ = context;
        await context.ConnectAsync();
        var window = await OpenAsync(context, new CorrespondenceRequest([]));
        var vm = window.ViewModel;
        dialogs.ConfirmAnswer = true;

        vm.Recipients.AddRows(
            [new ImportedContact("anna.zielinska@contoso.pl", "Anna Zielińska", new Dictionary<string, string> { ["Miejscowość"] = "Łódź" })],
            "kontakty.csv");
        vm.Message.Subject = "Zaproszenie dla {{nazwa}}";
        vm.Message.BodyHtml = "<p>{{zwrot}},</p><p>zapraszamy do {{Miejscowość}}.</p>";
        vm.Message.DelaySeconds = 0;
        window.CorrespondenceTabs.SelectedItem = window.SendTab;
        await UiTestSupport.WaitForIdleAsync();

        await vm.Send.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !vm.Send.IsSending && !outlook.Items.IsEmpty, "the run through the fake Outlook", timeoutSeconds: 30);
        await UiTestSupport.WaitForIdleAsync();

        var sent = Assert.Single(outlook.Succeeded(OutlookSendMode.Send));
        Assert.Equal(["anna.zielinska@contoso.pl"], sent.To);
        Assert.Equal("Zaproszenie dla Anna Zielińska", sent.Subject);
        Assert.Contains("Łódź", sent.HtmlBody, StringComparison.Ordinal);
        Assert.Equal("1", window.SentCountText.Text);
        Assert.Equal("0", window.FailedCountText.Text);
        Assert.NotEmpty(vm.Send.Results);
        Assert.Equal(vm.Send.Results.Count, window.ResultsList.Items.Count);
        Assert.Equal(vm.Send.ProgressText, window.ProgressText.Text);
        Assert.Equal(RecipientStatusKind.Sent, Assert.Single(vm.Recipients.Items).StatusKind);
        context.AssertHealthy();
    });

    [Fact]
    public Task MailboxOwners_RunOnTheSample_ReviewACandidate_AndAddTheOwnersAsRecipients() => WpfTestHost.RunAsync(async () =>
    {
        // Questions are answered "yes" instead of blocking the test with a modal dialog.
        var (context, _) = await TestDialogService.CreateContextAsync("pl");
        await using var _ = context;
        var sample = context.Directory.AddMailboxOwnerSample(context.Company);
        await context.ConnectAsync();
        var window = await OpenAsync(context, new CorrespondenceRequest([]));
        var mailboxes = window.ViewModel.Mailboxes;
        window.ShowMailboxesButton.Command.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Same(window.MailboxesTab, window.CorrespondenceTabs.SelectedItem);
        Assert.Equal(Visibility.Visible, window.NoMailboxesHint.Visibility);

        await mailboxes.FindOwnersCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !mailboxes.IsRunning && mailboxes.Rows.Count > 0, "the mailbox owners of the sample", timeoutSeconds: 30);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(mailboxes.IsExchangeSchemaPresent);
        Assert.Equal(Visibility.Collapsed, window.NoExchangeHint.Visibility);
        Assert.Equal(Visibility.Collapsed, window.NoMailboxesHint.Visibility);
        Assert.Equal(mailboxes.View.Count, window.MailboxesGrid.Items.Count);
        Assert.Equal(mailboxes.SummaryText, window.MailboxSummaryText.Text);

        var sekretariat = Row(mailboxes, sample.SekretariatIt);
        Assert.Equal(OwnerMatchStatus.Matched, sekretariat.Status);
        Assert.Equal("jan.kowalski@contoso.test", sekretariat.OwnerAddress);

        // The ambiguous "Laboratorium": its candidates in the row's box; choosing one is the operator's decision.
        var laboratorium = Row(mailboxes, sample.Laboratorium);
        Assert.Equal(OwnerMatchStatus.Ambiguous, laboratorium.Status);
        Assert.True(laboratorium.HasCandidates);
        window.MailboxesGrid.ScrollIntoView(laboratorium);
        window.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync();
        var container = Assert.IsType<ListViewItem>(window.MailboxesGrid.ItemContainerGenerator.ContainerFromItem(laboratorium));
        var candidates = Assert.Single(UiWait.Descendants<ComboBox>(container));
        Assert.Equal(Visibility.Visible, candidates.Visibility);
        Assert.Equal(laboratorium.Candidates.Count, candidates.Items.Count);
        candidates.SelectedItem = laboratorium.Candidates[0];
        Assert.Same(laboratorium.Candidates[0], laboratorium.SelectedCandidate);

        // The owners become recipients and the window switches to them.
        await mailboxes.AddToRecipientsCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => window.ViewModel.SelectedTab == CorrespondenceTab.Recipients, "the recipients tab");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Same(window.RecipientsTab, window.CorrespondenceTabs.SelectedItem);
        Assert.Contains(window.ViewModel.Recipients.Items, r => r.Email == "jan.kowalski@contoso.test");
        Assert.Equal(window.ViewModel.Recipients.View.Count, window.RecipientsGrid.Items.Count);
        Assert.Equal(FixedRecipientColumns + window.ViewModel.Recipients.Columns.Count, window.RecipientsGridView.Columns.Count);
        context.AssertHealthy();
    });

    [Fact]
    public Task SettingsTab_ShowsTheOutlookSection_ForTheOutlookTransport() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var settings = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateSettings();
        var window = (SettingsWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(settings);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        window.SettingsTabs.SelectedItem = window.MessagesTab;
        await UiTestSupport.WaitForIdleAsync();
        var messages = settings.Messages!;

        messages.Transport = messages.TransportChoices.Single(c => c.Value == UserMessageTransportChoice.DirectSmtp);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Collapsed, window.OutlookSection.Visibility);

        messages.Transport = messages.TransportChoices.Single(c => c.Value == UserMessageTransportChoice.Outlook);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, window.OutlookSection.Visibility);
        Assert.True(window.OutlookAccountBox.IsEditable);
        Assert.Equal("Pobierz konta z Outlooka", window.LoadOutlookAccountsButton.Content);
        Assert.Equal("Wysyłaj w imieniu adresu nadawcy podanego wyżej (konto potrzebuje uprawnienia „Wyślij w imieniu”)",
            ((TextBlock)window.OutlookOnBehalfBox.Content).Text);

        // Every binding of the section reaches the settings view model.
        Assert.Equal(BindingStatus.Active, Expression(window.OutlookSection, UIElement.VisibilityProperty).Status);
        Assert.Equal(BindingStatus.Active, Expression(window.OutlookAccountBox, ComboBox.TextProperty).Status);
        Assert.Equal(BindingStatus.Active, Expression(window.OutlookAccountBox, ItemsControl.ItemsSourceProperty).Status);
        Assert.Equal(BindingStatus.Active, Expression(window.LoadOutlookAccountsButton, ButtonBase.CommandProperty).Status);
        Assert.Equal(BindingStatus.Active, Expression(window.OutlookOnBehalfBox, ToggleButton.IsCheckedProperty).Status);
        Assert.Equal(BindingStatus.Active, Expression(window.OutlookAccountsText, TextBlock.TextProperty).Status);

        // "Pobierz konta z Outlooka" reads the accounts of the (in-memory) profile.
        window.LoadOutlookAccountsButton.Command.Execute(null);
        await UiWait.UntilAsync(() => window.OutlookAccountBox.Items.Count == FakeOutlookClient.SampleProfile.Accounts.Count, "the accounts of the profile");
        await UiWait.UntilAsync(() => window.OutlookAccountsText.Visibility == Visibility.Visible, "the outcome of loading the accounts");
        Assert.Contains(window.OutlookAccountBox.Items.Cast<object>(), a => a.ToString()!.Contains("sekretariat.lodz@contoso.test", StringComparison.Ordinal));
        window.OutlookAccountBox.Text = "sekretariat.lodz@contoso.test";
        window.OutlookOnBehalfBox.IsChecked = true;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("sekretariat.lodz@contoso.test", window.OutlookAccountBox.Text);
        context.AssertHealthy();
    });

    [Fact]
    public Task Window_FitsA1366x768Screen_AndDropsOnlyFiles() => WpfTestHost.RunAsync(() =>
    {
        var window = new Window { Width = 1320, Height = 900, MinWidth = 1000, MinHeight = 680 };
        CorrespondenceWindow.FitToWorkArea(window, new Rect(0, 0, 1366, 728));
        Assert.Equal((1320d, 728d, 1000d, 680d), (window.Width, window.Height, window.MinWidth, window.MinHeight));

        window = new Window { Width = 1320, Height = 900, MinWidth = 1000, MinHeight = 680 };
        CorrespondenceWindow.FitToWorkArea(window, new Rect(0, 0, 960, 600));
        Assert.Equal((960d, 600d, 960d, 600d), (window.Width, window.Height, window.MinWidth, window.MinHeight));

        window = new Window { Width = 1320, Height = 900, MinWidth = 1000, MinHeight = 680 };
        CorrespondenceWindow.FitToWorkArea(window, new Rect(0, 0, 2560, 1400));
        Assert.Equal((1320d, 900d, 1000d, 680d), (window.Width, window.Height, window.MinWidth, window.MinHeight));

        Assert.Equal([@"C:\Users\jkowalski\Downloads\kontakty_urzędy.csv"],
            CorrespondenceWindow.DroppedFiles(new DataObject(DataFormats.FileDrop, new[] { @"C:\Users\jkowalski\Downloads\kontakty_urzędy.csv" })));
        Assert.Empty(CorrespondenceWindow.DroppedFiles(new DataObject(DataFormats.UnicodeText, "anna.zielinska@contoso.pl")));
        return Task.CompletedTask;
    });

    [Fact]
    public void OwnerCandidate_AClearedSelection_NeverReachesTheRow()
    {
        var converter = new KeepChoiceConverter();
        var candidate = new CandidateOption(
            new PersonCandidate("CN=Jan Nowak,OU=IT,OU=Firma,DC=contoso,DC=test", "Jan", "Nowak", "Jan Nowak", "jan.nowak@contoso.test",
                false, MailboxType.None, "IT", "Informatyk", "jan.nowak"),
            "Jan Nowak – IT, jan.nowak@contoso.test", IsDelegate: false, OwnerMatchLevel.Exact);

        Assert.Same(candidate, converter.Convert(candidate, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Same(candidate, converter.ConvertBack(candidate, typeof(CandidateOption), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Same(Binding.DoNothing, converter.ConvertBack(null, typeof(CandidateOption), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A Korespondencja window of the default connection window, shown off-screen and initialized.</summary>
    internal static async Task<CorrespondenceWindow> OpenAsync(UiTestContext context, CorrespondenceRequest request)
    {
        var viewModel = context.Services.GetRequiredService<CorrespondenceViewModelFactory>().Create(request);
        var window = (CorrespondenceWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(viewModel);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        return window;
    }

    /// <summary>The keyboard focus arrives at <paramref name="element"/> (off-screen windows are never active).</summary>
    internal static void Focused(UIElement element) =>
        element.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, null, element)
        {
            RoutedEvent = Keyboard.GotKeyboardFocusEvent,
        });

    private static IReadOnlyList<TabItem> Tabs(CorrespondenceWindow window) => [.. window.CorrespondenceTabs.Items.OfType<TabItem>()];

    private static MailboxOwnerRowViewModel Row(MailboxOwnersPageViewModel page, string mailbox) =>
        page.Rows.Single(r => DistinguishedName.AreEqual(r.Mailbox.DistinguishedName, mailbox));

    private static BindingExpression Expression(DependencyObject element, DependencyProperty property) =>
        BindingOperations.GetBindingExpression(element, property) ?? throw new InvalidOperationException($"{property.Name} is not bound.");

    /// <summary>The command ids of the menu items under <paramref name="root"/>, in menu order.</summary>
    private static List<string> MenuIds(object? root) =>
        [.. LogicalDescendants(root).OfType<MenuItem>().Select(CommandMenu.GetId).OfType<string>()];

    private static IEnumerable<object> LogicalDescendants(object? root)
    {
        if (root is not DependencyObject element)
        {
            yield break;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(element))
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>A clipboard that holds a text (a table copied from Excel), nothing formatted.</summary>
    internal sealed class TextClipboard(string content) : IClipboardService
    {
        public ClipboardContent? ReadFormatted() => new(null, null, null, content);

        public bool SetText(string text) => true;

        public bool SetSecret(string secret) => true;
    }
}
