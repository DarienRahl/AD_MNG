using System.Text.Json;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Presentation.Comparison;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.Comparison;

/// <summary>
/// Where the comparison starts (the object's list command, the palette, the property sheet) and the connection's read
/// contexts it uses (design note docs/dev/multi-window.md 7 and 8.2).
/// </summary>
public sealed class ComparisonEntryPointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheListCommand_NeedsOneComparableObject_AndAnotherConnection()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync(s => s.AddScoped<RecordingComparisonLauncher>()
            .AddScoped<IObjectComparisonLauncher>(sp => sp.GetRequiredService<RecordingComparisonLauncher>()));
        await using var _ = host;
        var shell = a.Get<ShellViewModel>();
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(domains.Contoso.Company.ItOu));
        var descriptor = Assert.Single(shell.CommandDescriptors, c => c.Id == "object.compareWithConnection");
        Assert.True(shell.CanCompareWithConnections);

        // Nothing selected.
        Assert.False(shell.CompareWithConnectionCommand.CanExecute(null));
        Assert.Equal("Select one object: a user, group, computer or contact", descriptor.DisabledReason!(null));

        // One user, but no other connection window yet.
        Select(shell, "Łukasz Wiśniewski");
        host.Windows.SetWindows(a.Context);
        Assert.False(shell.CompareWithConnectionCommand.CanExecute(null));
        Assert.Equal("Open a second connection (Ctrl+Shift+N)", descriptor.DisabledReason!(null));

        host.Windows.SetWindows(a.Context, b.Context);
        Assert.True(shell.CompareWithConnectionCommand.CanExecute(null));
        Assert.Null(descriptor.DisabledReason!(null));
        await shell.CompareWithConnectionCommand.ExecuteAsync(null);

        Assert.Equal([domains.Contoso.Company.LukaszWisniewski], a.Get<RecordingComparisonLauncher>().Requests);
        Assert.Empty(b.Get<RecordingComparisonLauncher>().Requests);

        // Two objects: no comparison.
        var document = shell.SelectedDocument!;
        document.SelectedItems = document.Items.Where(r => r.Kind == ObjectKind.User).ToList();
        Assert.False(shell.CompareWithConnectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task ThePropertySheet_OffersTheComparison_ForComparableObjectsOnly()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync(s => s.AddScoped<RecordingComparisonLauncher>()
            .AddScoped<IObjectComparisonLauncher>(sp => sp.GetRequiredService<RecordingComparisonLauncher>()));
        await using var _ = host;
        var factory = a.Get<IDialogViewModelFactory>();
        using var user = factory.CreatePropertySheet(domains.Contoso.Company.LukaszWisniewski);
        using var ou = factory.CreatePropertySheet(domains.Contoso.Company.ItOu);
        await user.InitializeAsync();
        await ou.InitializeAsync();

        Assert.True(user.IsComparisonAvailable);
        Assert.False(ou.IsComparisonAvailable);
        Assert.False(ou.CompareWithConnectionCommand.CanExecute(null));
        await user.CompareWithConnectionCommand.ExecuteAsync(null);

        Assert.Equal([domains.Contoso.Company.LukaszWisniewski], a.Get<RecordingComparisonLauncher>().Requests);
    }

    [Fact]
    public async Task APinnedReadContext_EndsWithItsSession_AndWithItsWindow()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        var sessionChanges = 0;
        b.Context.SessionChanged += (_, _) => sessionChanges++;

        var first = b.Context.Pin()!;
        Assert.Equal("fabrikam.test", first.Info.DomainDnsName);
        Assert.Equal(b.Context.Id, first.ConnectionId);
        Assert.Equal(b.Context.Identity, first.Identity);
        Assert.NotNull(await first.Reader.GetAsync(domains.FabrikamDomain.Company.LukaszWisniewski, [Attr.Name], Ct));
        Assert.DoesNotContain("fabrikam", first.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(first.Reader is Core.Directory.IDirectorySession);

        await b.Sessions.SwitchDomainControllerAsync("dc02.fabrikam.test", null, Ct);
        Assert.True(first.IsEnded);
        Assert.Equal(1, sessionChanges);

        var second = b.Context.Pin()!;
        Assert.Equal("dc02.fabrikam.test", second.Info.Server);
        Assert.False(second.IsEnded);

        var contextB = b.Context;
        await b.DisposeAsync();
        Assert.True(second.IsEnded);
        Assert.Null(contextB.Pin());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second.Reader.GetAsync(domains.FabrikamDomain.Company.LukaszWisniewski, [Attr.Name], Ct));
        Assert.NotNull(a.Context.Pin());
    }

    [Fact]
    public async Task ADisconnectedWindow_HasNothingToPin()
    {
        await using var host = new ConnectionScopeHost();
        await using var a = host.CreateConnection();
        _ = a.Context;

        Assert.Null(a.Context.Pin());
    }

    [Fact]
    public void ComparisonSettings_RememberNamesOnly_AndRepairUnknownValues()
    {
        var settings = new AppSettings();
        settings.Comparison.Rules[ObjectKind.User] = ObjectMatchRule.Mail;
        settings.Comparison.Rules[ObjectKind.Computer] = ObjectMatchRule.UserPrincipalNamePrefix;
        settings.Comparison.Rules[(ObjectKind)999] = ObjectMatchRule.Name;
        settings.Comparison.OnlyDifferences = false;

        var json = JsonSerializer.Serialize(settings, JsonSettingsStore.SerializerOptions);
        var read = JsonSerializer.Deserialize<AppSettings>(json, JsonSettingsStore.SerializerOptions)!;
        read.Normalize();

        Assert.Contains("\"User\": \"Mail\"", json, StringComparison.Ordinal);
        Assert.Equal(ObjectMatchRule.Mail, read.Comparison.RuleFor(ObjectKind.User));
        Assert.Equal(ObjectMatchRule.Automatic, read.Comparison.RuleFor(ObjectKind.Computer));
        Assert.Equal(ObjectMatchRule.Automatic, read.Comparison.RuleFor(ObjectKind.Group));
        Assert.DoesNotContain((ObjectKind)999, read.Comparison.Rules.Keys);
        Assert.False(read.Comparison.OnlyDifferences);
        Assert.True(read.Comparison.CompareAddressesWithoutDomain);
    }

    [Fact]
    public void EveryMatchRule_HasItsName_InBothLanguages()
    {
        var english = new ResxLocalizer(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var polish = new ResxLocalizer(System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));

        foreach (var rule in Enum.GetValues<ObjectMatchRule>())
        {
            Assert.True(english.Contains(ComparisonText.RuleKey(rule)), rule.ToString());
            Assert.DoesNotContain("[", polish[ComparisonText.RuleKey(rule)], StringComparison.Ordinal);
        }

        Assert.Equal("Prefiks UPN", polish[ComparisonText.RuleKey(ObjectMatchRule.UserPrincipalNamePrefix)]);
        Assert.Equal("Porównaj z obiektem w innym połączeniu…", polish[L.Commands.CompareWithConnection]);
    }

    private static void Select(ShellViewModel shell, string name)
    {
        var document = shell.SelectedDocument!;
        document.SelectedItems = null;
        document.SelectedItem = document.Items.Single(r => r.Name == name);
        shell.RefreshCommandStates();
    }
}

/// <summary>Records what the shell or a property sheet asked the comparison window for.</summary>
internal sealed class RecordingComparisonLauncher : IObjectComparisonLauncher
{
    public List<string> Requests { get; } = [];

    public Task CompareAsync(string distinguishedName)
    {
        Requests.Add(distinguishedName);
        return Task.CompletedTask;
    }
}
