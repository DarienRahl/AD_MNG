using System.Globalization;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Connections;

/// <summary>
/// Who a connection window is (design note docs/dev/multi-window.md 4): the colour (automatic from the domain, from the
/// profile, moved on a collision), the ordinal of a second identical window, the title and label, and that nothing of it
/// reaches a log through <c>ToString</c>.
/// </summary>
public sealed class ConnectionIdentityTests
{
    private const string Fabrikam = "fabrikam.test";

    [Theory]
    [InlineData("contoso.test", ConnectionColor.Green)]
    [InlineData("CONTOSO.TEST.", ConnectionColor.Green)]
    [InlineData("fabrikam.test", ConnectionColor.Magenta)]
    [InlineData("corp.contoso.com", ConnectionColor.Slate)]
    public void TheAutomaticColour_IsStableForADomain(string domain, ConnectionColor expected)
    {
        // Golden values: FNV-1a of the lower-case name, never string.GetHashCode (randomized per process).
        Assert.Equal(expected, ConnectionColors.Automatic(domain));
    }

    [Fact]
    public void ThePalette_HasNoAmberAndNoRed()
    {
        Assert.Equal(8, ConnectionColors.Palette.Count);
        Assert.DoesNotContain(ConnectionColors.Palette, c => c.ToString() is "Amber" or "Red" or "Orange" or "Yellow");
    }

    [Fact]
    public void TheBadgeText_IsReadableOnEveryColour()
    {
        // White text on the badge: at least 4.5:1 (WCAG AA) for every palette colour, and the colours are distinct.
        Assert.All(ConnectionColors.Palette, color =>
            Assert.True(ConnectionColors.ContrastRatio(ConnectionColors.Rgb(color), (255, 255, 255)) >= 4.5, $"{color} is too light for white text."));
        Assert.Equal(ConnectionColors.Palette.Count, ConnectionColors.Palette.Select(ConnectionColors.Rgb).Distinct().Count());
        Assert.Equal(21, ConnectionColors.ContrastRatio((0, 0, 0), (255, 255, 255)), 3);
    }

    [Fact]
    public async Task TwoDomains_GetTheirAutomaticColours_AndLabels()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();

        await a.ConnectAsync(user: "jan.kowalski");
        await b.ConnectAsync(Fabrikam, "anna.zielinska");

        var contoso = a.Context.Identity!;
        Assert.Equal(ConnectionColor.Green, contoso.Color);
        Assert.Equal(ConnectionColorSource.Automatic, contoso.ColorSource);
        Assert.Equal(1, contoso.Ordinal);
        Assert.Equal(@"CONTOSO\jan.kowalski", contoso.Account);
        Assert.Equal(@"contoso.test · CONTOSO\jan.kowalski", contoso.Label);
        Assert.Equal(" – contoso.test", a.Context.TitleSuffix);
        Assert.Equal(ConnectionColor.Magenta, b.Context.Identity!.Color);
    }

    [Fact]
    public async Task ASecondIdenticalWindow_GetsAnOrdinal_AndSharesTheColour()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();

        await a.ConnectAsync(user: "jan.kowalski");
        await b.ConnectAsync(user: "jan.kowalski");

        Assert.Equal(1, a.Context.Identity!.Ordinal);
        Assert.Equal(2, b.Context.Identity!.Ordinal);
        Assert.Equal(@"CONTOSO\jan.kowalski (2)", b.Context.Identity.Account);
        Assert.Equal(@"contoso.test · CONTOSO\jan.kowalski (2)", b.Context.Label);

        // Same identity: the colour is not "taken" (it is the same connection twice), the ordinal tells them apart.
        Assert.Equal(a.Context.Identity.Color, b.Context.Identity.Color);
    }

    [Fact]
    public async Task AnotherAccountOfTheSameDomain_MovesToTheNextFreeColour_AndAClosedWindowFreesIt()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");

        await b.ConnectAsync(user: "odczyt.test");

        Assert.Equal(ConnectionColor.Green, a.Context.Identity!.Color);
        Assert.Equal(ConnectionColor.Purple, b.Context.Identity!.Color);
        Assert.Equal(1, b.Context.Identity.Ordinal);

        // Open windows keep their colour; the colour of a closed window is free again for the next one.
        await a.DisposeAsync();
        Assert.Equal(ConnectionColor.Purple, b.Context.Identity!.Color);
        await using var c = host.CreateConnection();
        await c.ConnectAsync(user: "anna.nowak");
        Assert.Equal(ConnectionColor.Green, c.Context.Identity!.Color);
    }

    [Fact]
    public async Task AProfileColour_Wins_AndIsNeverMoved()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");
        b.Context.ProfileName = "Contoso – helpdesk";
        b.Context.PreferredColor = ConnectionColor.Green;

        await b.ConnectAsync(user: "helpdesk");

        Assert.Equal(ConnectionColor.Green, b.Context.Identity!.Color);
        Assert.Equal(ConnectionColorSource.Profile, b.Context.Identity.ColorSource);
        Assert.Equal("Contoso – helpdesk", b.Context.Identity.ProfileName);
    }

    [Fact]
    public async Task TheTitle_StartsWithTheDomain_InBothLanguages()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");
        var shell = a.Get<ShellViewModel>();
        var localizer = a.Get<ILocalizer>();

        try
        {
            localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
            Assert.Equal(@"contoso.test – CONTOSO\jan.kowalski – Active Directory Users and Computers", shell.WindowTitle);
            localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
            Assert.Equal(@"contoso.test – CONTOSO\jan.kowalski – Użytkownicy i komputery usługi Active Directory", shell.WindowTitle);
            Assert.Equal(@"contoso.test · CONTOSO\jan.kowalski", shell.ConnectionLabel);
            Assert.Equal(ConnectionColor.Green, shell.ConnectionColor);
        }
        finally
        {
            localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        }
    }

    [Fact]
    public async Task ToString_NamesNoDomainAndNoAccount()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");

        foreach (var text in new[] { a.Context.ToString(), a.Context.Identity!.ToString() })
        {
            Assert.DoesNotContain("contoso", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("jan.kowalski", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(a.Context.Id.ToString("N"), a.Context.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionChangeAfterTheWindowClosed_DoesNotRegisterIt()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        var context = a.Context;
        var sessions = a.Sessions;
        var changes = 0;
        context.Changed += (_, _) => changes++;

        context.Dispose();

        // A bind that completes late raises SessionChanged on a manager whose context is gone: the window stays unregistered.
        Assert.Empty(host.Registry.All);
        await sessions.ConnectAsync(new Core.Directory.ConnectionOptions { Domain = "contoso.test" }, null, TestContext.Current.CancellationToken);
        Assert.Empty(host.Registry.All);
        Assert.Empty(host.Registry.Connections);
        Assert.Null(context.Identity);
        Assert.Equal(0, changes);
        Assert.True(context.IsClosed);
    }

    [Fact]
    public void NamingContextFilter_KeepsOnlyTheWindowsDomain()
    {
        Assert.True(NamingContextFilter.Contains("DC=contoso,DC=test", "OU=IT,OU=Firma,DC=contoso,DC=test"));
        Assert.True(NamingContextFilter.Contains("DC=contoso,DC=test", "dc=CONTOSO,dc=test"));
        Assert.False(NamingContextFilter.Contains("DC=contoso,DC=test", "OU=Sprzedaż,DC=fabrikam,DC=test"));
        Assert.False(NamingContextFilter.Contains("DC=contoso,DC=test", "not a dn"));
        Assert.True(NamingContextFilter.Contains(null, "OU=Sprzedaż,DC=fabrikam,DC=test"));
    }

    private static ConnectionScopeHost CreateHost() =>
        new(domains: [new FakeDirectoryBuilder().Build(), new FakeDirectoryBuilder(Fabrikam).Build()]);
}
