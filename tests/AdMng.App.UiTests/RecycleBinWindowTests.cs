using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

[Collection(UiTestCollection.Name)]
public sealed class RecycleBinWindowTests
{
    [Fact]
    public Task AppHost_RegistersTheRecycleBin_AndShowsOneWindow_Disconnected() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var windows = context.Services.GetRequiredService<IWindowService>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        Assert.True(windows.IsRegistered(typeof(RecycleBinViewModel)));

        await dialogs.ShowRecycleBinAsync();
        await dialogs.ShowRecycleBinAsync();
        await UiTestSupport.WaitForIdleAsync();

        var window = Assert.Single(Application.Current.Windows.OfType<RecycleBinWindow>());
        Assert.Same(window, windows.FindOpen(ToolWindows.RecycleBinKey));
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.ViewModel.IsConnected);
        Assert.Equal(Visibility.Visible, window.ErrorBar.Visibility);
        Assert.Equal(window.ViewModel.Error, window.ErrorText.Text);
        Assert.Equal(window.ViewModel.BannerText, window.BannerText.Text);
        Assert.False(window.RestoreButton.IsEnabled);
        Assert.False(window.RestoreToButton.IsEnabled);
        Assert.False(window.RefreshButton.IsEnabled);
        context.AssertHealthy();
    });

    [Fact]
    public Task RecycleBin_ShowsDeletedObjects_AndRestoresOneBackIntoTheOpenList() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var session = context.Directory.Session;
        await session.DeleteAsync(context.Company.JanKowalski, subtree: false, CancellationToken.None);
        await session.DeleteAsync(context.Company.Laptop, subtree: false, CancellationToken.None);

        // The main window shows OU=IT without the deleted user.
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        await main.Shell.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        await UiTestSupport.WaitForIdleAsync();
        var document = main.Shell.SelectedDocument!;
        Assert.DoesNotContain(document.Items, r => r.Name == "Jan Kowalski");

        // Tools → AD Recycle Bin.
        Assert.True(main.Shell.ShowRecycleBinCommand.CanExecute(null));
        await main.Shell.ShowRecycleBinCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        var window = Assert.Single(Application.Current.Windows.OfType<RecycleBinWindow>());
        await UiWait.InitializedAsync(window.Initialization);
        await window.ViewModel.Loading;
        await UiTestSupport.WaitForIdleAsync();

        var bin = window.ViewModel;
        Assert.True(bin.IsRecycleBinEnabled);
        Assert.Equal(2, window.ItemsList.Items.Count);
        Assert.Equal(Visibility.Collapsed, window.ErrorBar.Visibility);
        Assert.Equal(Visibility.Collapsed, window.EmptyHint.Visibility);
        Assert.Equal(bin.StatusText, window.StatusText.Text);
        var jan = bin.Items.Single(r => r.Name == "Jan Kowalski");
        Assert.Equal("contoso.test/Firma/Pracownicy/IT", jan.OriginalLocation);
        Assert.Contains(bin.Items, r => r.Name == "KRK-LT001");

        // Select Jan in the list (the view hands its multi-selection to the view model) and open the preview.
        window.ItemsList.SelectedItem = jan;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal([jan], bin.Selection);
        Assert.True(window.RestoreButton.IsEnabled);
        await bin.RestoreCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.True(bin.IsPreviewing);
        Assert.Single(window.PreviewList.Items);
        Assert.True(window.ConfirmButton.IsVisible);
        Assert.True(window.ConfirmButton.IsEnabled);
        Assert.Equal(bin.ConfirmText, window.ConfirmButton.Content);
        Assert.False(window.RestoreButton.IsVisible);
        Assert.Equal(bin.PreviewSummary, window.PreviewSummaryText.Text);

        // Restore: the object is back, the bin lists only the computer, the open OU=IT list shows the user again.
        await bin.ConfirmRestoreCommand.ExecuteAsync(null);
        await bin.Loading;
        await UiTestSupport.WaitForIdleAsync();

        Assert.True(session.Exists(context.Company.JanKowalski));
        Assert.Equal("IT", session.GetValues(context.Company.JanKowalski, Attr.Department).Single());
        Assert.False(bin.IsPreviewing);
        Assert.Equal("KRK-LT001", Assert.Single(bin.Items).Name);
        Assert.Single(window.ItemsList.Items);
        Assert.True(window.RestoreButton.IsVisible);
        await UiWait.UntilAsync(() => document.Items.Any(r => r.Name == "Jan Kowalski"), "the restored user in the OU=IT list");
        context.AssertHealthy();
    });

    [Fact]
    public Task RecycleBin_InPolish_WithoutTheRecycleBin_WarnsAboutTombstones() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var session = context.Directory.Session;
        session.Info = session.Info with { RecycleBinEnabled = false };
        await context.ConnectAsync();
        await session.DeleteAsync(context.Company.ZanetaSzymanska, subtree: false, CancellationToken.None);

        await context.Services.GetRequiredService<IDialogService>().ShowRecycleBinAsync();
        await UiTestSupport.WaitForIdleAsync();
        var window = Assert.Single(Application.Current.Windows.OfType<RecycleBinWindow>());
        await UiWait.InitializedAsync(window.Initialization);
        await window.ViewModel.Loading;
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal("Kosz Active Directory – contoso.test", window.Title);
        Assert.True(window.ViewModel.IsRecycleBinDisabled);
        Assert.StartsWith("Kosz Active Directory nie jest włączony", window.BannerText.Text, StringComparison.Ordinal);
        var row = Assert.Single(window.ViewModel.Items);
        Assert.Equal("Żaneta Szymańska", row.Name);
        Assert.Equal("Użytkownik", row.TypeText);
        Assert.Equal("Przywróć", window.RestoreButton.Content);
        Assert.Equal("Przywróć do...", window.RestoreToButton.Content);

        // Sorting by a column header and a server side filter keep the window healthy.
        window.ViewModel.SortCommand.Execute(DeletedObjectSort.Name);
        window.ViewModel.Kind = window.ViewModel.Kinds[1];
        await window.ViewModel.Loading;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Single(window.ItemsList.Items);
        Assert.Same(window.ViewModel.Kinds[1], window.KindBox.SelectedItem);
        context.AssertHealthy();
    });
}
