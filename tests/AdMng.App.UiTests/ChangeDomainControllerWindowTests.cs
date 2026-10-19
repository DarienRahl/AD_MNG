using System.Windows;
using System.Windows.Controls;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Views;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// "Change Domain Controller": the window renders disconnected and connected in English and Polish without binding
/// errors, the File menu and the domain root's context menu offer it, and a switch moves the main window to the new DC.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ChangeDomainControllerWindowTests
{
    /// <summary>Connected to a session of dc01 (which a switch may close); the directory records dc01 (PDC) and dc03.</summary>
    private static async Task<UiTestContext> ConnectedAsync(string language = "en")
    {
        var context = await UiTestContext.CreateAsync(language);
        var directory = context.Directory.Session;
        directory.SeedDomainController("dc01.contoso.test", new Guid("1b4f0d2e-0000-4000-8000-0000000000d1"), isPdc: true);
        directory.SeedDomainController("dc03.contoso.test", new Guid("1b4f0d2e-0000-4000-8000-0000000000d3"), isPdc: false);
        context.Connector.NextSession = new FakeDomainControllerSession(directory, "dc01.contoso.test");
        await context.ConnectAsync();
        return context;
    }

    private static async Task<ChangeDomainControllerWindow> OpenAsync(UiTestContext context)
    {
        var dialog = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateChangeDomainController();
        var window = (ChangeDomainControllerWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        return window;
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public Task Window_Disconnected_SaysNotConnected(string language) => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language);
        Assert.True(context.Services.GetRequiredService<IWindowService>().IsRegistered(typeof(ChangeDomainControllerViewModel)));

        var window = await OpenAsync(context);

        Assert.Equal(window.ViewModel.WindowTitle, window.Title);
        Assert.Equal(Visibility.Visible, window.ErrorBar.Visibility);
        Assert.Equal(window.ViewModel.ErrorText, window.ErrorTextBlock.Text);
        Assert.Empty(window.DomainControllerList.Items);
        Assert.False(window.ChangeButton.IsEnabled);
        Assert.False(window.RefreshButton.IsEnabled);
        Assert.Equal(Visibility.Collapsed, window.UpdateProfileBox.Visibility);
        context.AssertHealthy();
    });

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public Task Window_Connected_ListsTheDomainControllers(string language) => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await ConnectedAsync(language);

        var window = await OpenAsync(context);

        var dialog = window.ViewModel;
        Assert.Equal(Visibility.Collapsed, window.ErrorBar.Visibility);
        Assert.Equal(dialog.CurrentText, window.CurrentTextBlock.Text);
        Assert.Equal(dialog.DomainText, window.DomainTextBlock.Text);

        // dc01 (current, in the client site - DC locator), dc03 (directory only), dc02 (DC locator only, read-only).
        Assert.Equal(["dc01.contoso.test", "dc03.contoso.test", "dc02.contoso.test"], dialog.DomainControllers.Select(d => d.HostName));
        Assert.Equal(3, window.DomainControllerList.Items.Count);
        Assert.Same(dialog.DomainControllers[0], window.DomainControllerList.SelectedItem);
        Assert.Equal("dc01.contoso.test", window.ServerBox.Text);
        Assert.Equal(dialog.TargetHint, window.TargetHintText.Text);
        Assert.False(window.ChangeButton.IsEnabled);
        Assert.True(window.RefreshButton.IsEnabled);
        Assert.True(window.SpecificOption.IsChecked);
        Assert.True(window.AnyWritableOption.IsEnabled);
        Assert.Equal(dialog.AnyWritableText, window.AnyWritableTextBlock.Text);

        // A read-only DC is allowed with a warning; a typed name that is not a host name is not.
        window.DomainControllerList.SelectedItem = dialog.DomainControllers[2];
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("dc02.contoso.test", window.ServerBox.Text);
        Assert.True(window.ChangeButton.IsEnabled);
        Assert.Equal(Visibility.Visible, window.TargetHintText.Visibility);
        window.ServerBox.Text = "ldap://dc02.contoso.test";
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.ChangeButton.IsEnabled);
        Assert.Null(window.DomainControllerList.SelectedItem);
        context.AssertHealthy();
    });

    [Fact]
    public Task Switch_FromTheFileMenu_MovesTheMainWindowToTheNewDc() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await ConnectedAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("dc01.contoso.test", main.Shell.ServerText);

        // File → Change Domain Controller... and the domain root's context menu are wired to the shell command.
        var fileItem = LogicalTreeHelper.GetChildren(main.MainMenu).OfType<MenuItem>().First();
        var menuItem = Assert.Single(fileItem.Items.OfType<MenuItem>(), i => CommandMenu.GetId(i) == "session.changeDc");
        Assert.Same(main.Shell.ChangeDomainControllerCommand, menuItem.Command);
        Assert.True(menuItem.IsEnabled);
        var rootMenu = TreeMenu.Create(
            main.Shell.Tree.DomainNode!,
            main.Shell,
            context.Services.GetRequiredService<ViewLibrary>(),
            context.Services.GetRequiredService<IDialogService>());
        Assert.Contains(rootMenu.OfType<MenuItem>(), i => ReferenceEquals(i.Command, main.Shell.ChangeDomainControllerCommand));

        // The dialog (opened non-modally here) switches to dc03 and closes.
        var window = await OpenAsync(context);
        window.DomainControllerList.SelectedItem = window.ViewModel.DomainControllers.Single(d => d.HostName == "dc03.contoso.test");
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.ChangeButton.IsEnabled);
        await window.ViewModel.ChangeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !window.IsVisible, "the dialog closed");
        await UiWait.UntilAsync(() => main.Shell.SelectedDocument is { IsLoading: false }, "the tab reloaded");

        Assert.True(window.Result);
        Assert.Equal("dc03.contoso.test", context.Services.GetRequiredService<ISessionManager>().Current!.Info.Server);
        Assert.Equal("dc03.contoso.test", main.Shell.ServerText);
        Assert.Single(main.Shell.ListDocuments);
        Assert.True(menuItem.IsEnabled);
        context.AssertHealthy();
    });
}
