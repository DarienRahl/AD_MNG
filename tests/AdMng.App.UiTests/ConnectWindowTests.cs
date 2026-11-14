using System.Globalization;
using System.Windows;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

[Collection(UiTestCollection.Name)]
public sealed class ConnectWindowTests
{
    [Fact]
    public Task ConnectWindow_RendersEveryState_WithoutBindingErrors() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = context.Services.GetRequiredService<ConnectWindow>();
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();
        var viewModel = window.ViewModel;
        Assert.Equal(FakeDirectoryBuilder.DefaultDomain, viewModel.Domain);

        // The header shows the connection picture of the icon set at 48 px (its glyph in the monochrome style).
        Assert.Equal(AppIcons.Connection, window.HeaderIcon.IconKey);
        Assert.Equal(Glyphs.Network, window.HeaderIcon.Glyph);
        Assert.Equal(new Size(48, 48), window.HeaderIcon.RenderSize);

        // Explicit credentials, profile name and the domain controller drop-down.
        viewModel.Authentication = AuthenticationMode.Credentials;
        viewModel.UserName = @"CONTOSO\jan.kowalski";
        viewModel.SaveProfile = true;
        viewModel.ProfileName = "Laboratorium Łódź";
        await viewModel.FindDomainControllersCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(2, viewModel.DomainControllers.Count);
        viewModel.SelectedDomainController = viewModel.DomainControllers[1];
        window.ServerBox.IsDropDownOpen = true;
        await UiTestSupport.WaitForIdleAsync();
        window.ServerBox.IsDropDownOpen = false;
        Assert.Equal("dc02.contoso.test", viewModel.Server);

        // What is missing (no password) is said in the status line: nothing was tried.
        await viewModel.ConnectCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(string.IsNullOrEmpty(viewModel.ErrorText));
        Assert.Equal(PageStatusKind.Attention, window.PageStatus.Kind);
        Assert.EndsWith(viewModel.ErrorText!, window.PageStatus.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Connector.ConnectCount);

        context.AssertHealthy();
    });

    [Fact]
    public Task ConnectWindow_Connects_ClearsThePassword_AndRaisesConnected() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = context.Services.GetRequiredService<ConnectWindow>();
        var connected = 0;
        window.Connected += (_, _) => connected++;
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();

        window.ViewModel.Authentication = AuthenticationMode.Credentials;
        window.ViewModel.UserName = "administrator@contoso.test";
        window.PasswordBox.Password = "Zażółć-gęślą-jaźń-1";
        await window.ViewModel.ConnectCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal(1, connected);
        Assert.Equal(1, context.Connector.ConnectCount);
        Assert.NotNull(context.Services.GetRequiredService<ISessionManager>().Current);
        Assert.Equal(string.Empty, window.PasswordBox.Password);
        context.AssertHealthy();
    });

    [Fact]
    public Task ConnectWindow_FollowsALanguageSwitchLive() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var localizer = context.Services.GetRequiredService<ILocalizer>();
        var window = context.Services.GetRequiredService<ConnectWindow>();
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("Połącz z usługą Active Directory", window.Title);

        localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal(localizer[L.Connect.Title], window.Title);
        Assert.Equal("Connect to Active Directory", window.Title);
        context.AssertHealthy();
    });
}
