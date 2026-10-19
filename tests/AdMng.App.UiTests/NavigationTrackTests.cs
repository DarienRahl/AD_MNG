using System.Windows;
using System.Windows.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>Navigation track of phase 1: well-known container badges in the tree and the virtual (VLV) object list.</summary>
[Collection(UiTestCollection.Name)]
public sealed class NavigationTrackTests
{
    [Fact]
    public Task Tree_MarksTheWellKnownContainers_WithABadgeAndTooltip() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();

        var domain = window.Shell.Tree.DomainNode!;
        var users = domain.Children.OfType<ContainerNodeViewModel>().Single(c => c.Name == "Users");
        var company = domain.Children.OfType<ContainerNodeViewModel>().First(c => c.WellKnown is null && c.Kind == Core.Model.ObjectKind.OrganizationalUnit);
        Assert.Equal(WellKnownContainer.Users, users.WellKnown);
        await UiWait.UntilAsync(() => Badge(window, users) is not null, "badge of CN=Users realized");

        var badge = Badge(window, users)!;
        Assert.Equal(Visibility.Visible, badge.Visibility);
        Assert.Equal("Domyślny kontener nowych użytkowników i grup", System.Windows.Automation.AutomationProperties.GetName(badge));
        Assert.StartsWith("Domyślny kontener nowych użytkowników i grup", Assert.IsType<string>(((FrameworkElement)badge.Parent).ToolTip), StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, Badge(window, company)?.Visibility ?? Visibility.Collapsed);
        context.AssertHealthy();
    });

    [Fact]
    public Task ObjectList_VeryLargeContainer_IsAVirtualList_WithoutBindingErrors() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        var ou = context.Directory.AddOu("Masowe");
        for (var i = 0; i < 300; i++)
        {
            context.Directory.AddUser("Użytkownik", $"Numer{i:000}", ou);
        }

        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        var document = window.Shell.SelectedDocument!;
        document.VirtualThreshold = 100;
        document.VirtualPageSize = 50;
        await window.Shell.NavigateAsync(ListLocation.ForContainer(ou));
        await UiTestSupport.WaitForIdleAsync();

        Assert.True(document.IsVirtual);
        var list = window.SelectedListView!.ListControl;
        Assert.Same(document.VirtualRows, list.ItemsSource);
        Assert.Equal(300, list.Items.Count);
        Assert.False(list.IsTextSearchEnabled);
        Assert.Equal(Visibility.Visible, window.VirtualListBadge.Visibility);
        Assert.Contains("lista wirtualna (VLV)", window.Shell.StatusText, StringComparison.Ordinal);

        // Scrolling to the end reads the last page: its placeholders are replaced in place.
        list.ScrollIntoView(list.Items[299]);
        await UiWait.UntilAsync(() => document.VirtualRows!.GetLoaded(299) is not null, "last page read");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("Użytkownik Numer299", document.VirtualRows!.GetLoaded(299)!.Name);

        // Type-to-find seeks on the server and selects the row.
        Assert.True(await document.FindByPrefixAsync("Użytkownik Numer15"));
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("Użytkownik Numer150", document.SelectedItem?.Name);
        Assert.Same(document.SelectedItem, list.SelectedItem);

        // Another container: the normal list again, the badge disappears.
        await window.Shell.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        await UiTestSupport.WaitForIdleAsync();
        Assert.Same(document.Items, list.ItemsSource);
        Assert.True(list.IsTextSearchEnabled);
        Assert.Equal(Visibility.Collapsed, window.VirtualListBadge.Visibility);
        context.AssertHealthy();
    });

    private static TextBlock? Badge(MainWindow window, ContainerNodeViewModel node) =>
        UiWait.Descendants<TextBlock>(window.NavigationTree)
            .FirstOrDefault(t => t.Name == "WellKnownBadge" && ReferenceEquals(t.DataContext, node));
}
