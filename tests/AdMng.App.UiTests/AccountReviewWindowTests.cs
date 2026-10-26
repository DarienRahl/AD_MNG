using System.Windows;
using System.Windows.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.PropertySheets;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>The account review window (README U2) and the account state on the Account tab.</summary>
[Collection(UiTestCollection.Name)]
public sealed class AccountReviewWindowTests
{
    [Fact]
    public Task ToolsMenu_OpensOneWindow_TheTileShowsItsList_AndUnlockEmptiesIt() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var windows = context.Services.GetRequiredService<IWindowService>();
        Assert.True(windows.IsRegistered(typeof(AccountReviewViewModel)));
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        Assert.Contains(main.Shell.CommandDescriptors, d => d.Id == "tools.accountReview");

        // Tools → Account review, twice: one window.
        Assert.True(main.Shell.ShowAccountReviewCommand.CanExecute(null));
        await main.Shell.ShowAccountReviewCommand.ExecuteAsync(null);
        await main.Shell.ShowAccountReviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        var window = Assert.Single(Application.Current.Windows.OfType<AccountReviewWindow>());
        Assert.Same(window, windows.FindOpen(ToolWindows.AccountReviewKey));
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        var review = window.ViewModel;
        Assert.Equal(Visibility.Visible, window.NotRunHint.Visibility);
        Assert.Equal(Visibility.Collapsed, window.TilesList.Visibility);
        Assert.Equal(Enum.GetValues<AccountCheck>().Length, window.CheckOptionsList.Items.Count);
        Assert.True(window.RunButton.IsEnabled);

        await review.RunCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, window.TilesList.Visibility);
        Assert.Equal(Visibility.Collapsed, window.NotRunHint.Visibility);
        Assert.Equal(review.Tiles.Count, window.TilesList.Items.Count);
        Assert.Equal(review.StatusText, window.StatusText.Text);

        // The "Locked" tile: its columns and the locked user of the sample company.
        window.TilesList.SelectedItem = review.Tiles.Single(t => t.Check == AccountCheck.Locked);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(AccountCheck.Locked, review.SelectedCheck);
        Assert.Equal(review.Columns.Count, window.ItemsGrid.Columns.Count);
        Assert.Equal("Żaneta Szymańska", Assert.Single(review.Items).Name);
        Assert.Single(window.ItemsList.Items);
        Assert.Equal(Visibility.Visible, window.UnlockButton.Visibility);
        Assert.Equal(Visibility.Collapsed, window.ExtendButton.Visibility);
        Assert.Equal(Visibility.Visible, window.SendMessageButton.Visibility);
        Assert.Contains(UiWait.Descendants<TextBlock>(window.ItemsList), t => t.Text == review.Items[0][AccountReviewColumns.Login]);

        // Unlock through the change plan: the list is read again and empty.
        window.ItemsList.SelectedItem = review.Items[0];
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.UnlockButton.IsEnabled);
        await review.UnlockCommand.ExecuteAsync(null);
        await review.Refreshing;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("0", context.Directory.Session.GetValues(context.Company.ZanetaSzymanska, Attr.LockoutTime).Single());
        Assert.Empty(window.ItemsList.Items);
        Assert.Equal(Visibility.Visible, window.EmptyHint.Visibility);

        // Another tile rebuilds the columns.
        window.TilesList.SelectedItem = review.Tiles.Single(t => t.Check == AccountCheck.Disabled);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(review.Columns.Count, window.ItemsGrid.Columns.Count);
        Assert.Contains(review.Items, r => r.Name == "Zbigniew Kaźmierczak");
        context.AssertHealthy();
    });

    [Fact]
    public Task AccountTab_ShowsTheAccountState_InPolish() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        await context.ConnectAsync();
        var factory = context.Services.GetRequiredService<IDialogViewModelFactory>();
        var window = (PropertiesWindow)context.Services.GetRequiredService<IWindowService>()
            .CreateWindow(factory.CreatePropertySheet(context.Company.ZanetaSzymanska));
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        var sheet = window.ViewModel;
        sheet.SelectedPage = sheet.Pages.Single(p => p.Key == Presentation.Localization.L.Pages.Account);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        var status = Assert.Single(((FieldsPageViewModel)sheet.SelectedPage).Fields.OfType<AccountStatusFieldViewModel>());
        Assert.StartsWith("Zablokowane od ", status.StateText, StringComparison.Ordinal);
        var texts = UiWait.Descendants<TextBlock>(window).Where(t => t.IsVisible).Select(t => t.Text).ToList();
        Assert.Contains("Stan konta", texts);
        Assert.Contains(status.StateText, texts);
        Assert.Contains(status.PasswordText, texts);
        var check = UiWait.Descendants<Button>(window).Single(b => b.Name == "CheckDomainControllersButton");
        Assert.True(check.IsEnabled);
        await status.CheckDomainControllersCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Contains(UiWait.Descendants<TextBlock>(window), t => t.IsVisible && t.Text == status.DomainControllersText);
        context.AssertHealthy();
    });
}
