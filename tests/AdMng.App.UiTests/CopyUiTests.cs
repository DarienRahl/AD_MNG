using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AdMng.App.Behaviors;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// Phase 1 "Kopiowanie wartości" in the real views: copy buttons and selectable values of the details pane, Ctrl+C and
/// "Kopiuj jako" on the object list (context menu, Action menu, toolbar), copy buttons of property pages and list tabs,
/// the session log. The clipboard is recorded; every test ends without binding errors.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CopyUiTests
{
    [Fact]
    public Task DetailsPane_ValuesAreSelectable_AndCopyButtonsCopyThem() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new RecordingClipboard();
        await using var context = await UiTestContext.CreateAsync(configure: UseClipboard(clipboard));
        await context.ConnectAsync();
        var window = await OpenMainWindowAsync(context, context.Company.ItOu);
        var document = window.Shell.SelectedDocument!;
        window.SelectedListView!.ListControl.SelectedItem = document.Items.Single(r => r.Name == "Jan Kowalski");
        await UiWait.UntilAsync(
            () => window.DetailsContent.Visibility == Visibility.Visible && window.Shell.SelectedObject?.Name == "Jan Kowalski",
            "details of Jan Kowalski");
        await UiTestSupport.WaitForIdleAsync();

        // Values are read-only text boxes: selectable with the mouse, not tab stops.
        var mail = UiWait.Descendants<TextBox>(window.DetailsContent)
            .Single(t => t.DataContext is SummaryField && t.Text == "jan.kowalski@contoso.test");
        Assert.True(mail.IsReadOnly);
        Assert.False(mail.IsTabStop);
        mail.SelectAll();
        Assert.Equal("jan.kowalski@contoso.test", mail.SelectedText);
        Assert.Equal(@"CONTOSO\jan.kowalski", UiWait.Descendants<TextBox>(window.DetailsContent)
            .Single(t => t.DataContext is SummaryField { Label: "Domain logon name" }).Text);

        // One click copies the value; the glyph turns into a check mark and a toast confirms it.
        var copyMail = UiWait.Descendants<Button>(window.DetailsContent)
            .Single(b => b.CommandParameter is SummaryField { Value: "jan.kowalski@contoso.test" });
        Assert.Equal(Visibility.Visible, copyMail.Visibility);
        Assert.StartsWith("Copy: ", AutomationProperties.GetName(copyMail), StringComparison.Ordinal);
        await ClickAsync(copyMail);
        Assert.Equal("jan.kowalski@contoso.test", clipboard.Text);
        Assert.True(CopyFeedback.GetIsShowing(copyMail));
        Assert.Equal(Glyphs.Completed, copyMail.Content);
        Assert.Contains(context.Services.GetRequiredService<NotificationService>().Toasts, t => t.Message.StartsWith("Copied: ", StringComparison.Ordinal));

        await ClickAsync(window.CopyDnButton);
        Assert.Equal(context.Company.JanKowalski, clipboard.Text);
        Assert.Equal(context.Company.JanKowalski, window.DetailsDnText.Text);
        await ClickAsync(window.CopyPathButton);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT/Jan Kowalski", clipboard.Text);
        await ClickAsync(window.CopyNameButton);
        Assert.Equal("Jan Kowalski", clipboard.Text);

        // Status lines have no copy button.
        var state = UiWait.Descendants<Button>(window.DetailsContent).Single(b => b.CommandParameter is SummaryField { Label: "Status" });
        Assert.Equal(Visibility.Collapsed, state.Visibility);
        context.AssertHealthy();
    });

    [Fact]
    public Task ObjectList_CtrlCAndCopyAs_AreWiredInTheContextMenuActionMenuAndToolbar() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new RecordingClipboard();
        await using var context = await UiTestContext.CreateAsync(configure: UseClipboard(clipboard));
        await context.ConnectAsync();
        var window = await OpenMainWindowAsync(context, context.Company.ItOu);
        var shell = window.Shell;
        var list = window.SelectedListView!.ListControl;
        list.SelectAll();
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(2, shell.SelectedDocument!.Selection.Count);

        // Ctrl+C on the list: the visible columns, tab separated (the shortcut is bound to the document area).
        Assert.Contains(window.DocumentHost.InputBindings.OfType<KeyBinding>(), k =>
            k.Key == Key.C && k.Modifiers == ModifierKeys.Control && ReferenceEquals(k.Command, shell.CopyRowsCommand));
        Assert.Contains(window.DocumentHost.InputBindings.OfType<KeyBinding>(), k =>
            k.Key == Key.M && k.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && ReferenceEquals(k.Command, shell.CopyAsCommand)
            && Equals(k.CommandParameter, CopyFormat.Mail));
        shell.CopyRowsCommand.Execute(null);
        Assert.StartsWith("Jan Kowalski\tUser", clipboard.Text, StringComparison.Ordinal);

        // Context menu: "Copy as" submenu bound to the palette's descriptors.
        var menu = list.ContextMenu!;
        menu.PlacementTarget = list;
        menu.IsOpen = true;
        await UiTestSupport.WaitForIdleAsync();
        var copyAs = menu.Items.OfType<MenuItem>().Single(i => i.Items.OfType<MenuItem>().Any(s => CommandMenu.GetId(s) == "copyAs.mail"));
        Assert.Equal("Copy as", copyAs.Header);
        copyAs.IsSubmenuOpen = true;
        await UiTestSupport.WaitForIdleAsync();
        var mail = copyAs.Items.OfType<MenuItem>().Single(i => CommandMenu.GetId(i) == "copyAs.mail");
        await UiWait.UntilAsync(() => copyAs.Items.OfType<MenuItem>().All(i => i.Command is not null), "submenu items wired when shown");
        Assert.Same(shell.CopyAsCommand, mail.Command);
        Assert.Equal(CopyFormat.Mail, mail.CommandParameter);
        Assert.Equal("E-mail address", mail.Header);
        Assert.False(string.IsNullOrEmpty(mail.InputGestureText));
        Assert.All(copyAs.Items.OfType<MenuItem>(), i => Assert.NotNull(i.Command));
        mail.Command.Execute(mail.CommandParameter);
        await UiWait.UntilAsync(() => clipboard.Text?.Contains("; ", StringComparison.Ordinal) == true, "e-mail addresses copied");
        Assert.Equal("jan.kowalski@contoso.test; lukasz.wisniewski@contoso.test", clipboard.Text);
        copyAs.IsSubmenuOpen = false;
        menu.IsOpen = false;

        // The Action menu has the same submenu; the toolbar button opens it as a drop-down.
        Assert.Contains(window.CopyAsMenuItem.Items.OfType<MenuItem>(), i => CommandMenu.GetId(i) == "copyAs.sid");
        window.CopyAsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await UiTestSupport.WaitForIdleAsync();
        var toolbarMenu = window.CopyAsButton.ContextMenu!;
        Assert.True(toolbarMenu.IsOpen);
        Assert.Same(window.CopyAsButton, toolbarMenu.PlacementTarget);
        await UiWait.UntilAsync(
            () => toolbarMenu.Items.OfType<MenuItem>().Any(i => CommandMenu.GetId(i) == "object.copyRowsWithHeaders" && ReferenceEquals(i.Command, shell.CopyRowsWithHeadersCommand)),
            "toolbar menu wired");
        toolbarMenu.IsOpen = false;
        await UiTestSupport.WaitForIdleAsync();
        context.AssertHealthy();
    });

    [Fact]
    public Task PropertySheet_CopyButtons_CopyMailLogonUpnAndManager() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new RecordingClipboard();
        await using var context = await UiTestContext.CreateAsync(configure: UseClipboard(clipboard));
        await context.ConnectAsync();
        var window = await OpenSheetAsync(context, context.Company.LukaszWisniewski);
        var sheet = window.ViewModel;

        sheet.SelectedPage = sheet.Pages.Single(p => p.Key == L.Pages.General);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var mailButton = UiWait.Descendants<Button>(window).Single(b => b.Name == "CopyButton" && b.DataContext is FieldViewModel { Attribute: Attr.Mail });
        Assert.Equal(Visibility.Visible, mailButton.Visibility);
        await ClickAsync(mailButton);
        Assert.Equal("lukasz.wisniewski@contoso.test", clipboard.Text);
        Assert.True(CopyFeedback.GetIsShowing(mailButton));

        sheet.SelectedPage = sheet.Pages.Single(p => p.Key == L.Pages.Account);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var qualified = UiWait.Descendants<Button>(window).Single(b => b.Name == "QualifiedCopyButton" && b.IsVisible);
        Assert.Equal(@"CONTOSO\", qualified.Content);
        await ClickAsync(qualified);
        Assert.Equal(@"CONTOSO\lukasz.wisniewski", clipboard.Text);
        await ClickAsync(UiWait.Descendants<Button>(window).Single(b => b.Name == "CopyUpnButton"));
        Assert.Equal("lukasz.wisniewski@contoso.test", clipboard.Text);

        sheet.SelectedPage = sheet.Pages.Single(p => p.Key == L.Pages.Organization);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var manager = UiWait.Descendants<Button>(window).Single(b => b.Name == "CopyReferenceButton" && b.DataContext is DnFieldViewModel { Attribute: Attr.Manager });
        await UiWait.UntilAsync(() => manager.IsEnabled, "manager resolved");
        await ClickAsync(manager);
        Assert.Equal("Jan Kowalski", clipboard.Text);
        context.AssertHealthy();
    });

    [Fact]
    public Task PropertySheet_ListTabs_CopyAnAddressWithoutPrefix_AndTheMembersAddresses() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new RecordingClipboard();
        await using var context = await UiTestContext.CreateAsync(configure: UseClipboard(clipboard));
        await context.ConnectAsync();
        context.Directory.Session.SetValues(context.Company.JanKowalski, Attr.ProxyAddresses, "SMTP:jan.kowalski@contoso.test", "smtp:jk@contoso.test");

        // E-mail addresses: one address, without "smtp:".
        var user = await OpenSheetAsync(context, context.Company.JanKowalski);
        user.ViewModel.SelectedPage = user.ViewModel.Pages.Single(p => p.Key == L.Pages.EmailAddresses);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var proxies = (ProxyAddressesFieldViewModel)UiWait.Descendants<ListView>(user).Single(l => l.Name == "ProxyAddressList").DataContext;
        proxies.SelectedItem = proxies.Items.Single(i => i.Address == "jk@contoso.test");
        await UiTestSupport.WaitForIdleAsync();
        await ClickAsync(UiWait.Descendants<Button>(user).Single(b => b.Name == "CopyAddressButton"));
        Assert.Equal("jk@contoso.test", clipboard.Text);

        // Members: every member's address for Outlook, and the list's context menu.
        var group = await OpenSheetAsync(context, context.Company.GroupIt);
        var members = group.ViewModel.Pages.OfType<MembersPageViewModel>().Single();
        group.ViewModel.SelectedPage = members;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        await UiWait.UntilAsync(() => members.Items.Count == 2 && !members.IsLoading, "the two members of GG_IT");
        var copyAll = UiWait.Descendants<Button>(group).Single(b => b.Name == "CopyAllMailButton");
        Assert.Equal(Visibility.Visible, copyAll.Visibility);
        await ClickAsync(copyAll);
        Assert.Equal("jan.kowalski@contoso.test; lukasz.wisniewski@contoso.test", clipboard.Text);

        var list = UiWait.Descendants<ListView>(group).Single(l => l.Name == "MembershipList");
        list.SelectedItems.Add(members.Items[0]);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var menu = list.ContextMenu!;
        menu.PlacementTarget = list;
        menu.IsOpen = true;
        await UiTestSupport.WaitForIdleAsync();
        Assert.All(menu.Items.OfType<MenuItem>(), i => Assert.NotNull(i.Command));
        var copyName = menu.Items.OfType<MenuItem>().Single(i => ReferenceEquals(i.Command, members.CopyNamesCommand));
        Assert.True(copyName.IsEnabled);
        copyName.Command.Execute(null);
        Assert.Equal(members.Items[0].Name, clipboard.Text);
        menu.IsOpen = false;
        await UiTestSupport.WaitForIdleAsync();
        context.AssertHealthy();
    });

    [Fact]
    public Task SessionLog_CtrlCCopiesTheEntry_AndCtrlShiftCItsDn() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new RecordingClipboard();
        await using var context = await UiTestContext.CreateAsync(configure: UseClipboard(clipboard));
        await context.ConnectAsync();
        var log = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateSessionLog();
        var window = (SessionLogWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(log);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        var outcome = await context.Services.GetRequiredService<IChangeCoordinator>().ApplyAsync(
            new ChangeRequest([context.Company.GrazynaDabrowska], [new SetAttributeOperation(Attr.Department, "Finanse")], "Zmiana działu"));
        Assert.Equal(ChangeOutcome.Executed, outcome);
        await UiTestSupport.WaitForIdleAsync();
        log.SelectedEntry = Assert.Single(log.Entries);
        await UiTestSupport.WaitForIdleAsync();

        var copyEntry = window.EntriesList.InputBindings.OfType<KeyBinding>().Single(k => k.Key == Key.C && k.Modifiers == ModifierKeys.Control);
        Assert.Same(log.CopyEntryCommand, copyEntry.Command);
        copyEntry.Command.Execute(null);
        Assert.Contains("After: Finanse", clipboard.Text, StringComparison.Ordinal);
        Assert.Contains(context.Company.GrazynaDabrowska, clipboard.Text, StringComparison.Ordinal);

        var copyDn = window.EntriesList.InputBindings.OfType<KeyBinding>().Single(k => k.Key == Key.C && k.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
        copyDn.Command.Execute(null);
        Assert.Equal(context.Company.GrazynaDabrowska, clipboard.Text);
        Assert.Same(log.CopyEntryCommand, window.CopyEntryMenuItem.Command);
        context.AssertHealthy();
    });

    private static Action<IServiceCollection> UseClipboard(RecordingClipboard clipboard) =>
        services => services.Replace(ServiceDescriptor.Singleton<IClipboardService>(clipboard));

    private static async Task<MainWindow> OpenMainWindowAsync(UiTestContext context, string container)
    {
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await window.Shell.NavigateAsync(ListLocation.ForContainer(container));
        await UiTestSupport.WaitForIdleAsync();
        return window;
    }

    private static async Task<PropertiesWindow> OpenSheetAsync(UiTestContext context, string distinguishedName)
    {
        var sheet = context.Services.GetRequiredService<IDialogViewModelFactory>().CreatePropertySheet(distinguishedName);
        var window = (PropertiesWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(sheet);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(window.ViewModel.LoadError);
        return window;
    }

    /// <summary>Clicks like the operator (UI Automation invoke): the Click event and then the command.</summary>
    private static async Task ClickAsync(Button button)
    {
        Assert.True(button.IsEnabled, $"{button.Name} is enabled");
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!;
        invoke.Invoke();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public bool SetText(string text)
        {
            Text = text;
            return true;
        }

        public bool SetSecret(string secret)
        {
            Text = secret;
            return true;
        }
    }
}
