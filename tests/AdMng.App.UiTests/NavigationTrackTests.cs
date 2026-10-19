using System.Windows;
using System.Windows.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
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

    private static TextBlock? Badge(MainWindow window, ContainerNodeViewModel node) =>
        UiWait.Descendants<TextBlock>(window.NavigationTree)
            .FirstOrDefault(t => t.Name == "WellKnownBadge" && ReferenceEquals(t.DataContext, node));
}
